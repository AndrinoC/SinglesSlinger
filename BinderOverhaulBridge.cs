using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace SinglesSlinger
{
    /// <summary>
    /// Reflection-based bridge to the BinderOverhaul mod
    /// (<c>TCGCardShopSimulator.BinderOverhaul</c>). When detected, provides a
    /// fast path for scanning all cards via the mod's pre-built cache instead of
    /// iterating every expansion individually.
    /// <para>
    /// All reflection targets are resolved once during detection. If the mod is
    /// not installed or any required member is missing, the bridge disables itself
    /// and SinglesSlinger falls back to vanilla card storage scanning.
    /// </para>
    /// </summary>
    internal static class BinderOverhaulBridge
    {
        // ── detection state ─────────────────────────────────────────────
        private static bool _checked;
        private static bool _available;

        // ── reflected types ─────────────────────────────────────────────
        private static Type _tAllView;   // AllExpansionView
        private static Type _tEntry;     // AllCardEntry (struct)

        // ── reflected members ───────────────────────────────────────────
        private static MethodInfo _mGetOrBuildCache;   // static List<AllCardEntry>
        private static MethodInfo _mInvalidateCache;   // static void

        private static FieldInfo _fCard;       // AllCardEntry.Card       (CardData)
        private static FieldInfo _fOwnedCount; // AllCardEntry.OwnedCount (int)

        // ── public API ──────────────────────────────────────────────────

        /// <summary>
        /// <c>true</c> when BinderOverhaul is loaded and all reflection targets resolved.
        /// </summary>
        internal static bool IsAvailable
        {
            get
            {
                if (!_checked) Detect();
                return _available;
            }
        }

        /// <summary>
        /// Reads the BinderOverhaul cache and returns eligible ungraded cards
        /// matching the current plugin filters. Returns <c>null</c> on failure
        /// (signals the caller to use the vanilla scan fallback).
        /// </summary>
        internal static List<CardData> GetCompatibleCards()
        {
            if (!IsAvailable) return null;

            try
            {
                IList cache = _mGetOrBuildCache.Invoke(null, null) as IList;
                if (cache == null || cache.Count == 0)
                    return new List<CardData>();

                var results = new List<CardData>();
                int keepQty = Plugin.KeepCardQty.Value;
                float minMP = Plugin.SellOnlyGreaterThanMP.Value;
                float maxMP = Plugin.SellOnlyLessThanMP.Value;

                for (int i = 0; i < cache.Count; i++)
                {
                    try
                    {
                        object entry = cache[i];
                        if (entry == null) continue;

                        CardData cd = _fCard.GetValue(entry) as CardData;
                        int owned = (int)_fOwnedCount.GetValue(entry);

                        if (cd == null || cd.monsterType == EMonsterType.None) continue;
                        if (cd.cardGrade > 0) continue;   // graded cards handled separately
                        if (owned <= keepQty) continue;

                        // Expansion toggle
                        if (!Plugin.EnabledExpansions.TryGetValue(
                                cd.expansionType, out var cfgOn) || !cfgOn.Value)
                            continue;

                        float mp = CPlayerData.GetCardMarketPrice(cd);
                        if (mp > minMP && mp < maxMP)
                        {
                            CardData copy = new CardData();
                            copy.CopyData(cd);
                            results.Add(copy);
                        }
                    }
                    catch { }
                }

                Plugin.Log.LogInfo(
                    "[BinderOverhaulBridge] " + results.Count +
                    " compatible cards read from BinderOverhaul cache (" +
                    cache.Count + " total cache entries).");
                return results;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError(
                    "[BinderOverhaulBridge] GetCompatibleCards failed:\r\n" + ex);
                return null;   // signal caller to use vanilla fallback
            }
        }

        /// <summary>
        /// Invalidates the BinderOverhaul cache after a batch placement completes,
        /// so the mod rebuilds accurate counts on next access.
        /// Safe to call even if the bridge is not available.
        /// </summary>
        internal static void NotifyBatchComplete()
        {
            if (!IsAvailable) return;
            try
            {
                if (_mInvalidateCache != null)
                {
                    _mInvalidateCache.Invoke(null, null);
                    Plugin.Log.LogInfo(
                        "[BinderOverhaulBridge] Cache invalidated after batch.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning(
                    "[BinderOverhaulBridge] NotifyBatchComplete failed: " + ex.Message);
            }
        }

        // ── detection logic ─────────────────────────────────────────────
        private static void Detect()
        {
            _checked = true;
            _available = false;

            try
            {
                _tAllView = AccessTools.TypeByName(
                    "TCGCardShopSimulator.BinderOverhaul.AllExpansionView");

                if (_tAllView == null)
                {
                    Plugin.Log.LogInfo(
                        "[BinderOverhaulBridge] BinderOverhaul not detected — " +
                        "using vanilla card storage.");
                    return;
                }

                _tEntry = AccessTools.TypeByName(
                    "TCGCardShopSimulator.BinderOverhaul.AllCardEntry");

                if (_tEntry == null)
                {
                    Plugin.Log.LogWarning(
                        "[BinderOverhaulBridge] AllCardEntry struct not found — " +
                        "bridge disabled.");
                    return;
                }

                // Static methods on AllExpansionView
                _mGetOrBuildCache = AccessTools.Method(_tAllView, "GetOrBuildCache");
                _mInvalidateCache = AccessTools.Method(_tAllView, "InvalidateCache");

                // Public fields on the AllCardEntry struct
                _fCard = _tEntry.GetField("Card");
                _fOwnedCount = _tEntry.GetField("OwnedCount");

                if (_mGetOrBuildCache == null || _fCard == null || _fOwnedCount == null)
                {
                    Plugin.Log.LogWarning(
                        "[BinderOverhaulBridge] One or more required members not found — " +
                        "bridge disabled.  GetOrBuildCache=" +
                        (_mGetOrBuildCache != null) + "  Card=" +
                        (_fCard != null) + "  OwnedCount=" +
                        (_fOwnedCount != null));
                    return;
                }

                _available = true;
                Plugin.Log.LogInfo(
                    "[BinderOverhaulBridge] BinderOverhaul detected — bridge is ACTIVE.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError(
                    "[BinderOverhaulBridge] Detection failed:\r\n" + ex);
                _available = false;
            }
        }
    }
}