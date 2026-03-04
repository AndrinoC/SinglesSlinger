using System;
using HarmonyLib;

namespace SinglesSlinger
{
    /// <summary>
    /// Harmony postfix on <c>Customer.TakeCardFromShelf</c> (private method).
    /// When a customer removes the last card from a shelf compartment, passes
    /// the emptied compartment reference to <see cref="ShelfPlacer.RequestRefill"/>
    /// for targeted quick refill instead of a full pipeline rescan.
    /// </summary>
    [HarmonyPatch(typeof(Customer), "TakeCardFromShelf")]
    internal static class Customer_TakeCardFromShelf_Patch
    {
        [HarmonyPostfix]
        private static void Postfix(
            Customer __instance,
            InteractableCardCompartment ___m_CurrentCardCompartment)
        {
            if (___m_CurrentCardCompartment == null)
                return;

            if (___m_CurrentCardCompartment.m_StoredCardList.Count < 1)
            {
                if (Plugin.TriggerOnCustomerCardPickup.Value)
                {
                    ShelfPlacer.RequestRefill(
                        ShelfPlacer.RunMode.NormalSingles,
                        ___m_CurrentCardCompartment);
                }

                if (Plugin.GradedTriggerOnCustomerCardPickup.Value)
                {
                    ShelfPlacer.RequestRefill(
                        ShelfPlacer.RunMode.GradedCards,
                        ___m_CurrentCardCompartment);
                }
            }
        }
    }

    /// <summary>
    /// Harmony postfix on <c>PriceChangeManager.OnDayStarted</c> (private/protected).
    /// Fires at the start of a new in-game day. Immediate — no debounce.
    /// Runs the full pipeline which also repopulates the card cache for
    /// subsequent quick refills during the day.
    /// </summary>
    [HarmonyPatch(typeof(PriceChangeManager), "OnDayStarted")]
    internal static class PriceChangeManager_OnDayStarted_Patch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            if (Plugin.TriggerOnDayStart.Value)
                ShelfPlacer.DoShelfPut(ShelfPlacer.RunMode.NormalSingles);

            if (Plugin.GradedTriggerOnDayStart.Value)
                ShelfPlacer.DoShelfPut(ShelfPlacer.RunMode.GradedCards);
        }
    }

    /// <summary>
    /// Harmony postfix on <c>CGameManager.Update</c>.
    /// Processes debounced refill requests and polls for keyboard shortcuts
    /// each frame.
    /// </summary>
    [HarmonyPatch(typeof(CGameManager), "Update")]
    internal static class CGameManager_Update_Patch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            try
            {
                ShelfPlacer.ProcessPendingRefills();

                if (Plugin.SetOutCardsKey.Value.IsDown())
                    ShelfPlacer.DoShelfPut(ShelfPlacer.RunMode.NormalSingles);

                if (Plugin.SetOutGradedCardsKey.Value.IsDown())
                    ShelfPlacer.DoShelfPut(ShelfPlacer.RunMode.GradedCards);
            }
            catch (Exception ex)
            {
                LogHelper.LogErrorThrottled("UpdateCheckFailed",
                    "[SinglesSlinger] Update check failed:\r\n" + ex, 15f);
            }
        }
    }
}