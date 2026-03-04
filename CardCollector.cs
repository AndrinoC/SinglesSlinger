using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace SinglesSlinger
{
    /// <summary>
    /// Scans the player's card inventory and provides filtered lists of cards
    /// eligible for shelf placement. All public methods must be called on the
    /// Unity main thread. Scan methods yield to spread work across frames.
    /// </summary>
    internal static class CardCollector
    {
        /// <summary>Shared RNG for shuffling and random card selection.</summary>
        internal static readonly System.Random Rng = new System.Random();

        /// <summary>Fisher-Yates in-place shuffle.</summary>
        internal static void ShuffleInPlace<T>(List<T> list)
        {
            if (list == null || list.Count < 2) return;
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = Rng.Next(i + 1);
                T tmp = list[i];
                list[i] = list[j];
                list[j] = tmp;
            }
        }

        /// <summary>
        /// O(1) removal by swapping the target element with the last element
        /// and then removing the last. Does not preserve order.
        /// </summary>
        internal static void SwapRemoveAt<T>(List<T> list, int index)
        {
            int last = list.Count - 1;
            if (index < last)
                list[index] = list[last];
            list.RemoveAt(last);
        }

        /// <summary>
        /// Yielding coroutine that scans a single expansion for eligible ungraded cards.
        /// Results are appended to the <paramref name="results"/> list.
        /// Yields every <paramref name="batchSize"/> cards to avoid frame spikes.
        /// </summary>
        internal static IEnumerator ScanExpansionYielding(
            ECardExpansionType expansionType,
            bool findGhostDimensionCards,
            List<CardData> results,
            int batchSize)
        {
            int perCardErrorCount = 0;
            int keepQty = Plugin.KeepCardQty.Value;
            float minMP = Plugin.SellOnlyGreaterThanMP.Value;
            float maxMP = Plugin.SellOnlyLessThanMP.Value;

            List<int> collected = null;
            try
            {
                collected = CPlayerData.GetCardCollectedList(expansionType, findGhostDimensionCards);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[SinglesSlinger] GetCardCollectedList failed for " +
                    expansionType + " (ghostDim=" + findGhostDimensionCards + ")\r\n" + ex);
                yield break;
            }

            if (collected == null || collected.Count == 0)
                yield break;

            for (int i = 0; i < collected.Count; i++)
            {
                if (collected[i] <= keepQty)
                {
                    if ((i + 1) % batchSize == 0) yield return null;
                    continue;
                }

                try
                {
                    CardData cardData = CPlayerData.GetCardData(
                        i, expansionType, findGhostDimensionCards);

                    if (cardData == null || cardData.monsterType == EMonsterType.None)
                        continue;

                    float mp = CPlayerData.GetCardMarketPrice(cardData);
                    if (mp > minMP && mp < maxMP)
                        results.Add(cardData);
                }
                catch
                {
                    perCardErrorCount++;
                }

                if ((i + 1) % batchSize == 0)
                    yield return null;
            }

            if (perCardErrorCount > 0)
            {
                LogHelper.LogWarningThrottled(
                    "ScanExpansion.Errors." + expansionType + "." +
                        (findGhostDimensionCards ? "GhostDim" : "Normal"),
                    "[SinglesSlinger] Scan saw " + perCardErrorCount +
                    " per-card errors for " + expansionType);
            }
        }

        /// <summary>
        /// Yielding coroutine that scans the graded card inventory.
        /// Results are appended to <paramref name="results"/> and sorted most-expensive-first
        /// after all entries have been scanned.
        /// </summary>
        internal static IEnumerator ScanGradedCardsYielding(
            List<CardData> results, int batchSize)
        {
            int keepQty = Plugin.GradedKeepCardQty.Value;

            FieldInfo listField = AccessTools.Field(typeof(CPlayerData), "m_GradedCardInventoryList");
            if (listField == null)
            {
                LogHelper.LogErrorThrottled("Graded.ListFieldMissing",
                    "[SinglesSlinger] Could not find m_GradedCardInventoryList.");
                yield break;
            }

            object rawListObj = listField.GetValue(null);
            if (rawListObj == null)
                yield break;

            IEnumerable rawList = rawListObj as IEnumerable;
            if (rawList == null)
            {
                LogHelper.LogErrorThrottled("Graded.ListNotEnumerable",
                    "[SinglesSlinger] m_GradedCardInventoryList not enumerable.");
                yield break;
            }

            var seenEligibleCopies = new Dictionary<string, int>();
            int scanned = 0;

            foreach (object compactObj in rawList)
            {
                scanned++;

                if (compactObj == null)
                {
                    if (scanned % batchSize == 0) yield return null;
                    continue;
                }

                try
                {
                    if (!(compactObj is CompactCardDataAmount compact))
                        continue;

                    CardData cd = CPlayerData.GetGradedCardData(compact);
                    if (cd == null || cd.monsterType == EMonsterType.None || cd.cardGrade <= 0)
                        continue;

                    GradeDecoder.DecodeCardGrade(cd.cardGrade, out int actualGrade, out string gradingCompany);
                    if (actualGrade <= 0) continue;

                    if (gradingCompany == "PSA" && !Plugin.GradedAllowPSA.Value) continue;
                    if (gradingCompany == "Beckett" && !Plugin.GradedAllowBeckett.Value) continue;
                    if (gradingCompany == "Cardinals" && !Plugin.GradedAllowCardinals.Value) continue;

                    if (!Plugin.EnabledExpansions.TryGetValue(cd.expansionType, out var enabled) ||
                        !enabled.Value)
                        continue;

                    float mp = CPlayerData.GetCardMarketPrice(cd);
                    if (mp <= Plugin.GradedSellOnlyGreaterThanMP.Value ||
                        mp >= Plugin.GradedSellOnlyLessThanMP.Value)
                        continue;

                    string key = cd.expansionType + "|" + cd.monsterType + "|" + cd.cardGrade;
                    if (!seenEligibleCopies.TryGetValue(key, out int seen))
                        seen = 0;

                    seen++;
                    if (seen <= keepQty)
                    {
                        seenEligibleCopies[key] = seen;
                        continue;
                    }

                    seenEligibleCopies[key] = seen;
                    results.Add(cd);
                }
                catch { }

                if (scanned % batchSize == 0)
                    yield return null;
            }

            // Sort most expensive first
            results.Sort((a, b) =>
                CPlayerData.GetCardMarketPrice(b).CompareTo(CPlayerData.GetCardMarketPrice(a)));
        }

        /// <summary>
        /// Starting from <paramref name="index"/>, finds the next card whose
        /// owned count exceeds the keep quantity. Wraps around once.
        /// Used in OnlyPlaceMostExpensive mode.
        /// </summary>
        internal static bool TryGetNextMostExpensiveNoDuplicates(
            List<CardData> sortedDistinct, ref int index,
            out CardData cardData, out int usedIndex)
        {
            cardData = null;
            usedIndex = -1;

            if (sortedDistinct == null || sortedDistinct.Count == 0)
                return false;

            int keepQty = Plugin.KeepCardQty.Value;
            if (index < 0) index = 0;
            if (index >= sortedDistinct.Count) index = 0;

            int start = index;

            while (true)
            {
                if (sortedDistinct.Count == 0)
                    return false;

                if (index >= sortedDistinct.Count)
                    index = 0;

                CardData candidate = sortedDistinct[index];
                if (candidate != null && candidate.monsterType != EMonsterType.None)
                {
                    int owned = CPlayerData.GetCardAmount(candidate);
                    if (owned > keepQty)
                    {
                        cardData = candidate;
                        usedIndex = index;
                        return true;
                    }
                }

                index++;
                if (index >= sortedDistinct.Count)
                    index = 0;

                if (index == start)
                    break;
            }

            return false;
        }
    }
}