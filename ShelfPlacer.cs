using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SinglesSlinger
{
    /// <summary>
    /// Orchestrates card placement onto shelves using a three-phase pipeline:
    /// SCAN (gather eligible cards) → ASSIGN (map cards to compartments) →
    /// SPAWN (create 3D objects in batches). Supports both ungraded and graded cards.
    ///
    /// Event-driven triggers (customer pickup) use a two-tier strategy:
    /// 1. If a card cache exists from a prior full pipeline run, performs a fast
    ///    synchronous refill targeting only the emptied compartment(s) — typically
    ///    completes in under 1 ms.
    /// 2. Otherwise, falls back to a full debounced pipeline run.
    ///
    /// Hotkeys and day-start triggers always execute the full pipeline immediately.
    /// </summary>
    internal static class ShelfPlacer
    {
        /// <summary>Determines whether to place ungraded or graded cards.</summary>
        internal enum RunMode
        {
            NormalSingles,
            GradedCards
        }

        private static bool isRunningNormal;
        private static bool isRunningGraded;

        // ── Debounce state ──────────────────────────────────────────────
        private static bool _normalRefillRequested;
        private static bool _gradedRefillRequested;
        private static float _normalRequestTime;
        private static float _gradedRequestTime;

        // ── Card cache for quick event-driven refills ──────────────────
        // Populated during the full pipeline SCAN phase. Contains all eligible
        // CardData objects that passed filters at scan time. The quick refill
        // path verifies live inventory counts before placing, so stale entries
        // are harmlessly skipped and lazily pruned.
        private static readonly List<CardData> _normalCardCache = new List<CardData>();

        // ── Tracked empty compartments from customer pickups ───────────
        private static readonly List<InteractableCardCompartment> _pendingNormalCompartments
            = new List<InteractableCardCompartment>();
        private static readonly List<InteractableCardCompartment> _pendingGradedCompartments
            = new List<InteractableCardCompartment>();

        /// <summary>
        /// Marks that a refill is needed for the given mode. For event-driven
        /// triggers (customer pickup), also tracks the emptied compartment so
        /// the quick refill can target it directly. Each call resets the
        /// debounce timer so rapid pickups collapse into one operation.
        /// </summary>
        internal static void RequestRefill(
            RunMode mode,
            InteractableCardCompartment emptiedCompartment = null)
        {
            float now = Time.realtimeSinceStartup;

            if (mode == RunMode.NormalSingles)
            {
                if (emptiedCompartment != null &&
                    !_pendingNormalCompartments.Contains(emptiedCompartment))
                {
                    _pendingNormalCompartments.Add(emptiedCompartment);
                }

                _normalRefillRequested = true;
                _normalRequestTime = now;
                LogHelper.LogDebug(
                    "[SinglesSlinger] Normal refill requested (debounce started/reset).");
            }
            else
            {
                if (emptiedCompartment != null &&
                    !_pendingGradedCompartments.Contains(emptiedCompartment))
                {
                    _pendingGradedCompartments.Add(emptiedCompartment);
                }

                _gradedRefillRequested = true;
                _gradedRequestTime = now;
                LogHelper.LogDebug(
                    "[SinglesSlinger] Graded refill requested (debounce started/reset).");
            }
        }

        /// <summary>
        /// Called every frame from the Update patch. Checks whether any debounced
        /// refill requests have settled. For normal cards, uses the quick refill
        /// path when a card cache is available and compartments are pending;
        /// otherwise falls back to the full pipeline. Graded cards always use
        /// the full pipeline.
        /// Cost when idle: two boolean checks + two float comparisons.
        /// </summary>
        internal static void ProcessPendingRefills()
        {
            float now = Time.realtimeSinceStartup;
            float delay = Plugin.RefillDebounceDelay != null
                ? Plugin.RefillDebounceDelay.Value
                : 2f;

            if (_normalRefillRequested && !isRunningNormal &&
                (now - _normalRequestTime) >= delay)
            {
                _normalRefillRequested = false;

                if (_normalCardCache.Count > 0 &&
                    _pendingNormalCompartments.Count > 0)
                {
                    LogHelper.LogDebug(
                        "[SinglesSlinger] Normal debounce elapsed — quick refill " +
                        "from cache (" + _normalCardCache.Count + " cached, " +
                        _pendingNormalCompartments.Count + " pending).");
                    RunQuickNormalRefill();
                }
                else
                {
                    LogHelper.LogDebug(
                        "[SinglesSlinger] Normal debounce elapsed — cache empty " +
                        "or no pending compartments, starting full pipeline.");
                    _pendingNormalCompartments.Clear();
                    DoShelfPut(RunMode.NormalSingles);
                }
            }

            if (_gradedRefillRequested && !isRunningGraded &&
                (now - _gradedRequestTime) >= delay)
            {
                _gradedRefillRequested = false;
                _pendingGradedCompartments.Clear();
                LogHelper.LogDebug(
                    "[SinglesSlinger] Graded debounce elapsed — starting full pipeline.");
                DoShelfPut(RunMode.GradedCards);
            }
        }

        /// <summary>
        /// Immediately starts the full placement pipeline for the given mode.
        /// Clears any pending debounce request and pending compartments.
        /// </summary>
        internal static void DoShelfPut(RunMode mode)
        {
            if (mode == RunMode.NormalSingles)
            {
                _normalRefillRequested = false;
                _pendingNormalCompartments.Clear();

                if (isRunningNormal) return;
                isRunningNormal = true;
                try
                {
                    StaticCoroutine.Start(RunNormalPipeline());
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError(
                        "[SinglesSlinger] DoShelfPut (normal) failed:\r\n" + ex);
                    isRunningNormal = false;
                }
            }
            else
            {
                _gradedRefillRequested = false;
                _pendingGradedCompartments.Clear();

                if (isRunningGraded) return;
                isRunningGraded = true;
                try
                {
                    StaticCoroutine.Start(RunGradedPipeline());
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError(
                        "[SinglesSlinger] DoShelfPut (graded) failed:\r\n" + ex);
                    isRunningGraded = false;
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  Quick synchronous refill for normal cards
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Fast synchronous refill that places cards from the cached eligible
        /// card list into specifically-tracked emptied compartments. Performs
        /// no inventory scanning, no shelf iteration, and no coroutine
        /// overhead. Typically completes in well under 1 ms for 1–5 slots.
        /// Does NOT invalidate the BinderOverhaul cache.
        /// </summary>
        private static void RunQuickNormalRefill()
        {
            int keepQty = Plugin.KeepCardQty.Value;
            bool mostExpensive = Plugin.OnlyPlaceMostExpensive.Value;
            int placed = 0;

            // ── Validate pending compartments ──────────────────────────
            for (int i = _pendingNormalCompartments.Count - 1; i >= 0; i--)
            {
                InteractableCardCompartment cc = _pendingNormalCompartments[i];

                bool invalid = false;

                if (cc == null)
                {
                    invalid = true;
                }
                else if (cc.m_StoredCardList == null ||
                         cc.m_StoredCardList.Count > 0)
                {
                    invalid = true;
                }
                else if (cc.m_ItemNotForSale)
                {
                    invalid = true;
                }

                if (!invalid && Plugin.SkipVintageTables.Value)
                {
                    try
                    {
                        CardShelf shelf = cc.GetCardShelf();
                        if (shelf != null && ShelfUtility.IsVintageTable(shelf))
                            invalid = true;
                    }
                    catch
                    {
                        invalid = true;
                    }
                }

                if (invalid)
                    _pendingNormalCompartments.RemoveAt(i);
            }

            if (_pendingNormalCompartments.Count == 0)
            {
                LogHelper.LogDebug(
                    "[SinglesSlinger] Quick refill: no valid pending compartments.");
                return;
            }

            // ── Place cards from cache ─────────────────────────────────
            for (int ci = 0; ci < _pendingNormalCompartments.Count; ci++)
            {
                if (_normalCardCache.Count == 0)
                    break;

                InteractableCardCompartment cc = _pendingNormalCompartments[ci];
                if (cc == null ||
                    cc.m_StoredCardList == null ||
                    cc.m_StoredCardList.Count > 0)
                {
                    continue;
                }

                CardData cardData = null;
                int selectedIdx = -1;

                if (mostExpensive)
                {
                    // Cache is sorted most-expensive-first; pick from front
                    for (int j = 0; j < _normalCardCache.Count; j++)
                    {
                        CardData candidate = _normalCardCache[j];
                        if (candidate == null ||
                            candidate.monsterType == EMonsterType.None)
                        {
                            continue;
                        }

                        try
                        {
                            int owned = CPlayerData.GetCardAmount(candidate);
                            if (owned > keepQty)
                            {
                                cardData = candidate;
                                selectedIdx = j;
                                break;
                            }
                        }
                        catch { }
                    }
                }
                else
                {
                    // Random start, sequential scan (wraps around) to
                    // guarantee finding a card if any eligible one exists.
                    if (_normalCardCache.Count > 0)
                    {
                        int startIdx =
                            CardCollector.Rng.Next(_normalCardCache.Count);

                        for (int scan = 0;
                             scan < _normalCardCache.Count;
                             scan++)
                        {
                            int idx =
                                (startIdx + scan) % _normalCardCache.Count;
                            CardData candidate = _normalCardCache[idx];

                            if (candidate == null ||
                                candidate.monsterType == EMonsterType.None)
                            {
                                continue;
                            }

                            try
                            {
                                int owned =
                                    CPlayerData.GetCardAmount(candidate);
                                if (owned > keepQty)
                                {
                                    cardData = candidate;
                                    selectedIdx = idx;
                                    break;
                                }
                            }
                            catch { }
                        }
                    }
                }

                if (cardData == null)
                    break;

                try
                {
                    CPlayerData.ReduceCard(cardData, 1);
                    ShelfUtility.PlaceCardDirect(cc, cardData);
                    placed++;

                    // Update cache depending on mode
                    if (mostExpensive)
                    {
                        // Prevent duplicate types across compartments
                        if (selectedIdx >= 0 &&
                            selectedIdx < _normalCardCache.Count)
                        {
                            _normalCardCache.RemoveAt(selectedIdx);
                        }
                    }
                    else
                    {
                        // Only remove when card type is fully exhausted
                        try
                        {
                            int remaining =
                                CPlayerData.GetCardAmount(cardData);
                            if (remaining <= keepQty &&
                                selectedIdx >= 0 &&
                                selectedIdx < _normalCardCache.Count)
                            {
                                CardCollector.SwapRemoveAt(
                                    _normalCardCache, selectedIdx);
                            }
                        }
                        catch { }
                    }

                    if (Plugin.TryTriggerPriceSlinger.Value)
                        ShelfUtility.TryTellPriceSlinger(cc);
                }
                catch (Exception ex)
                {
                    try { CPlayerData.AddCard(cardData, 1); } catch { }
                    LogHelper.LogErrorThrottled("QuickRefill",
                        "[SinglesSlinger] Quick refill placement failed: " +
                        ex, 5f);
                }
            }

            _pendingNormalCompartments.Clear();

            if (placed > 0)
            {
                LogHelper.LogDebug(
                    "[SinglesSlinger] Quick refill placed " + placed +
                    " card(s).");
                ShelfUtility.ShowPopup(
                    placed + " card(s) placed (quick refill).");
            }
            else
            {
                LogHelper.LogDebug(
                    "[SinglesSlinger] Quick refill: no eligible cards in cache.");
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  Normal (ungraded) singles pipeline
        // ═══════════════════════════════════════════════════════════════
        private static IEnumerator RunNormalPipeline()
        {
            int batchSize = Plugin.CardBatchSize != null
                ? Plugin.CardBatchSize.Value : 20;

            // ── PHASE 1: SCAN ──────────────────────────────────────────
            var allCards = new List<CardData>();
            bool usedBridge = false;

            if (BinderOverhaulBridge.IsAvailable)
            {
                List<CardData> bridgeResult = null;
                try { bridgeResult = BinderOverhaulBridge.GetCompatibleCards(); }
                catch (Exception ex)
                {
                    Plugin.Log.LogError(
                        "[SinglesSlinger] Bridge GetCompatibleCards failed:\r\n" +
                        ex);
                }

                if (bridgeResult != null)
                {
                    allCards = bridgeResult;
                    usedBridge = true;
                    LogHelper.LogDebug(
                        "[SinglesSlinger] (BinderOverhaul) Got " +
                        allCards.Count + " cards from bridge.");
                }
            }

            if (!usedBridge)
            {
                foreach (var kvp in Plugin.EnabledExpansions)
                {
                    if (!kvp.Value.Value) continue;
                    yield return StaticCoroutine.Start(
                        CardCollector.ScanExpansionYielding(
                            kvp.Key, false, allCards, batchSize));
                }

                if (Plugin.EnabledExpansions.TryGetValue(
                        ECardExpansionType.Ghost, out var ghostEnabled) &&
                    ghostEnabled.Value)
                {
                    yield return StaticCoroutine.Start(
                        CardCollector.ScanExpansionYielding(
                            ECardExpansionType.Ghost, true, allCards, batchSize));
                }
            }

            yield return null;

            if (allCards.Count == 0)
            {
                isRunningNormal = false;
                _normalCardCache.Clear();
                ShelfUtility.ShowPopup(
                    "SinglesSlinger: No cards matching configured filters!");
                yield break;
            }

            // Sort or shuffle
            if (Plugin.OnlyPlaceMostExpensive.Value)
            {
                allCards.Sort((c, d) =>
                    CPlayerData.GetCardMarketPrice(d)
                        .CompareTo(CPlayerData.GetCardMarketPrice(c)));
            }
            else
            {
                CardCollector.ShuffleInPlace(allCards);
            }

            yield return null;

            // ── Populate card cache for future quick refills ────────────
            _normalCardCache.Clear();
            for (int ci = 0; ci < allCards.Count; ci++)
            {
                _normalCardCache.Add(allCards[ci]);
            }

            LogHelper.LogDebug(
                "[SinglesSlinger] Card cache populated with " +
                _normalCardCache.Count + " eligible cards.");

            // Count total placeable copies
            int totalPlaceable = 0;
            int keepQty = Plugin.KeepCardQty.Value;
            for (int c = 0; c < allCards.Count; c++)
            {
                try
                {
                    int owned = CPlayerData.GetCardAmount(allCards[c]);
                    if (owned > keepQty)
                        totalPlaceable += (owned - keepQty);
                }
                catch { }

                if ((c + 1) % batchSize == 0)
                    yield return null;
            }

            yield return null;

            // ── PHASE 2: ASSIGN ────────────────────────────────────────
            var assignCompartments = new List<InteractableCardCompartment>();
            var assignCards = new List<CardData>();
            int mostExpensiveIndex = 0;

            List<CardShelf> shelves =
                CSingleton<ShelfManager>.Instance.m_CardShelfList;

            foreach (CardShelf shelf in shelves)
            {
                if (shelf == null) continue;
                if (allCards.Count == 0) break;
                if (Plugin.SkipVintageTables.Value &&
                    ShelfUtility.IsVintageTable(shelf))
                    continue;
                if (shelf.GetIsBoxedUp()) continue;

                List<InteractableCardCompartment> compartments =
                    shelf.GetCardCompartmentList();
                if (compartments == null) continue;

                for (int i = 0; i < compartments.Count; i++)
                {
                    if (allCards.Count == 0) break;

                    InteractableCardCompartment cc = compartments[i];
                    if (cc == null) continue;
                    if (cc.m_StoredCardList.Count != 0 || cc.m_ItemNotForSale)
                        continue;

                    CardData cardData = null;
                    int selectedIdx = -1;

                    if (Plugin.OnlyPlaceMostExpensive.Value)
                    {
                        if (!CardCollector.TryGetNextMostExpensiveNoDuplicates(
                                allCards, ref mostExpensiveIndex,
                                out cardData, out selectedIdx))
                        {
                            cardData = null;
                        }
                    }
                    else
                    {
                        if (allCards.Count > 0)
                        {
                            selectedIdx = CardCollector.Rng.Next(allCards.Count);
                            cardData = allCards[selectedIdx];
                        }
                    }

                    if (cardData == null ||
                        cardData.monsterType == EMonsterType.None)
                        continue;

                    assignCompartments.Add(cc);
                    assignCards.Add(cardData);

                    // Claim from inventory during assignment
                    CPlayerData.ReduceCard(cardData, 1);

                    if (Plugin.OnlyPlaceMostExpensive.Value)
                    {
                        if (selectedIdx >= 0 && selectedIdx < allCards.Count)
                            allCards.RemoveAt(selectedIdx);
                    }
                    else
                    {
                        if (CPlayerData.GetCardAmount(cardData) == keepQty
                            && selectedIdx >= 0 && selectedIdx < allCards.Count)
                        {
                            CardCollector.SwapRemoveAt(allCards, selectedIdx);
                        }
                    }

                    if (mostExpensiveIndex >= allCards.Count)
                        mostExpensiveIndex = 0;

                    if (assignCompartments.Count % batchSize == 0)
                        yield return null;
                }
            }

            yield return null;

            // ── PHASE 3: SPAWN ─────────────────────────────────────────
            int placedCards = 0;

            for (int i = 0; i < assignCompartments.Count; i++)
            {
                try
                {
                    ShelfUtility.PlaceCardDirect(
                        assignCompartments[i], assignCards[i]);
                    placedCards++;

                    if (Plugin.TryTriggerPriceSlinger.Value)
                        ShelfUtility.TryTellPriceSlinger(assignCompartments[i]);
                }
                catch (Exception ex)
                {
                    try { CPlayerData.AddCard(assignCards[i], 1); } catch { }

                    LogHelper.LogErrorThrottled("BatchPlace",
                        "[SinglesSlinger] PlaceCardDirect failed at index " +
                        i + ": " + ex, 5f);
                }

                if ((i + 1) % batchSize == 0)
                    yield return null;
            }

            isRunningNormal = false;
            BinderOverhaulBridge.NotifyBatchComplete();

            ShelfUtility.ShowPopup(totalPlaceable == 0
                ? "SinglesSlinger: No cards matching configured filters!"
                : placedCards + " cards of " + totalPlaceable +
                  " possible matching cards placed.");
        }

        // ═══════════════════════════════════════════════════════════════
        //  Graded cards pipeline
        // ═══════════════════════════════════════════════════════════════
        private static IEnumerator RunGradedPipeline()
        {
            int batchSize = Plugin.CardBatchSize != null
                ? Plugin.CardBatchSize.Value : 20;
            bool requireVintage = Plugin.GradedOnlyToVintageTable.Value;

            List<CardShelf> shelves =
                CSingleton<ShelfManager>.Instance.m_CardShelfList;

            // Check for vintage tables first if required
            if (requireVintage)
            {
                bool anyVintage = false;
                for (int s = 0; s < shelves.Count; s++)
                {
                    if (shelves[s] != null &&
                        ShelfUtility.IsVintageTable(shelves[s]))
                    {
                        anyVintage = true;
                        break;
                    }
                }

                if (!anyVintage)
                {
                    isRunningGraded = false;
                    ShelfUtility.ShowPopup(
                        "SinglesSlinger: No vintage tables found, " +
                        "graded cards were not placed.");
                    yield break;
                }
            }

            // ── PHASE 1: SCAN ──────────────────────────────────────────
            var results = new List<CardData>();
            yield return StaticCoroutine.Start(
                CardCollector.ScanGradedCardsYielding(results, batchSize));

            yield return null;

            int totalMatching = results.Count;

            if (totalMatching == 0)
            {
                isRunningGraded = false;
                ShelfUtility.ShowPopup(
                    "SinglesSlinger: No graded cards matching configured filters!");
                yield break;
            }

            // ── PHASE 2: ASSIGN ────────────────────────────────────────
            var assignCompartments = new List<InteractableCardCompartment>();
            var assignCards = new List<CardData>();
            int resultIndex = 0;

            foreach (CardShelf shelf in shelves)
            {
                if (shelf == null) continue;
                if (resultIndex >= results.Count) break;

                bool isVintage = ShelfUtility.IsVintageTable(shelf);
                if (requireVintage && !isVintage) continue;
                if (shelf.GetIsBoxedUp()) continue;

                List<InteractableCardCompartment> compartments =
                    shelf.GetCardCompartmentList();
                if (compartments == null) continue;

                for (int i = 0; i < compartments.Count; i++)
                {
                    if (resultIndex >= results.Count) break;

                    InteractableCardCompartment cc = compartments[i];
                    if (cc == null) continue;
                    if (cc.m_StoredCardList.Count != 0 || cc.m_ItemNotForSale)
                        continue;

                    CardData cardData = results[resultIndex];
                    resultIndex++;

                    if (cardData == null ||
                        cardData.monsterType == EMonsterType.None ||
                        cardData.cardGrade <= 0)
                        continue;

                    assignCompartments.Add(cc);
                    assignCards.Add(cardData);

                    // Remove from graded inventory during assignment
                    if (!TryRemoveGradedCardFromAlbum(cardData))
                    {
                        LogHelper.LogWarningThrottled("Graded.RemoveFailed",
                            "[SinglesSlinger] Failed removing a graded card " +
                            "from album inventory.", 10f);
                    }

                    if (assignCompartments.Count % batchSize == 0)
                        yield return null;
                }
            }

            yield return null;

            // ── PHASE 3: SPAWN ─────────────────────────────────────────
            int placedCards = 0;

            for (int i = 0; i < assignCompartments.Count; i++)
            {
                try
                {
                    ShelfUtility.PlaceCardDirect(
                        assignCompartments[i], assignCards[i]);
                    placedCards++;

                    if (Plugin.TryTriggerPriceSlinger.Value)
                        ShelfUtility.TryTellPriceSlinger(assignCompartments[i]);
                }
                catch (Exception ex)
                {
                    LogHelper.LogErrorThrottled("BatchPlaceGraded",
                        "[SinglesSlinger] PlaceCardDirect (graded) failed at " +
                        "index " + i + ": " + ex, 5f);
                }

                if ((i + 1) % batchSize == 0)
                    yield return null;
            }

            isRunningGraded = false;
            BinderOverhaulBridge.NotifyBatchComplete();

            ShelfUtility.ShowPopup(placedCards + " graded cards of " +
                totalMatching + " possible matching graded cards placed.");
        }

        // ═══════════════════════════════════════════════════════════════
        //  Graded inventory removal (uses cached FieldInfo)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Removes a single graded card entry from
        /// CPlayerData.m_GradedCardInventoryList by matching the
        /// gradedCardIndex. Uses the shared cached FieldInfo from
        /// <see cref="ShelfUtility.GetGradedListField"/>.
        /// </summary>
        private static bool TryRemoveGradedCardFromAlbum(CardData placed)
        {
            try
            {
                if (placed == null) return false;

                FieldInfo listField = ShelfUtility.GetGradedListField();
                if (listField == null) return false;

                object rawListObj = listField.GetValue(null);
                if (rawListObj == null) return false;

                IList list = rawListObj as IList;
                if (list == null) return false;

                for (int i = 0; i < list.Count; i++)
                {
                    object compactObj = list[i];
                    if (compactObj == null) continue;

                    if (!(compactObj is CompactCardDataAmount compact))
                        continue;

                    if (compact.gradedCardIndex == placed.gradedCardIndex)
                    {
                        list.RemoveAt(i);
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                LogHelper.LogErrorThrottled("TryRemoveGraded",
                    "[SinglesSlinger] TryRemoveGradedCardFromAlbum exception: " +
                    ex.Message, 15f);
                return false;
            }
        }
    }
}