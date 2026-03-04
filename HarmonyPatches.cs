using System;
using HarmonyLib;

namespace SinglesSlinger
{
    /// <summary>
    /// Harmony postfix on <c>Customer.TakeCardFromShelf</c> (private method).
    /// When a customer removes the last card from a shelf compartment, triggers
    /// automatic refill based on the configured triggers.
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
                    ShelfPlacer.DoShelfPut(ShelfPlacer.RunMode.NormalSingles);

                if (Plugin.GradedTriggerOnCustomerCardPickup.Value)
                    ShelfPlacer.DoShelfPut(ShelfPlacer.RunMode.GradedCards);
            }
        }
    }

    /// <summary>
    /// Harmony postfix on <c>PriceChangeManager.OnDayStarted</c> (private/protected).
    /// Fires at the start of a new in-game day.
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
    /// Polls for keyboard shortcuts each frame.
    /// </summary>
    [HarmonyPatch(typeof(CGameManager), "Update")]
    internal static class CGameManager_Update_Patch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            try
            {
                if (Plugin.SetOutCardsKey.Value.IsDown())
                    ShelfPlacer.DoShelfPut(ShelfPlacer.RunMode.NormalSingles);

                if (Plugin.SetOutGradedCardsKey.Value.IsDown())
                    ShelfPlacer.DoShelfPut(ShelfPlacer.RunMode.GradedCards);
            }
            catch (Exception ex)
            {
                LogHelper.LogErrorThrottled("UpdateHotkeyCheckFailed",
                    "[SinglesSlinger] Update hotkey check failed:\r\n" + ex, 15f);
            }
        }
    }
}