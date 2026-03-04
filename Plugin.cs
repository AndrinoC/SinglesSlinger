using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SinglesSlinger
{
    /// <summary>
    /// SinglesSlinger BepInEx plugin entry point.
    /// Automatically places ungraded and graded singles from the player's album
    /// onto card shelves based on configurable filters, triggers, and hotkeys.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        private const string PluginGuid = "com.kritterbizkit.singlesslinger";
        private const string PluginName = "SinglesSlinger";
        private const string PluginVersion = "1.9.3";

        /// <summary>Shared logger accessible from all mod files.</summary>
        internal static ManualLogSource Log;

        private readonly Harmony harmony = new Harmony(PluginGuid);

        // ───────────── General ─────────────
        internal static ConfigEntry<float> SellOnlyGreaterThanMP;
        internal static ConfigEntry<float> SellOnlyLessThanMP;
        internal static ConfigEntry<KeyboardShortcut> SetOutCardsKey;
        internal static ConfigEntry<int> KeepCardQty;
        internal static ConfigEntry<bool> ShowProgressPopUp;

        // ───────────── Placement ─────────────
        internal static ConfigEntry<bool> OnlyPlaceMostExpensive;
        internal static ConfigEntry<bool> SkipVintageTables;

        // ───────────── Graded ─────────────
        internal static ConfigEntry<float> GradedSellOnlyGreaterThanMP;
        internal static ConfigEntry<float> GradedSellOnlyLessThanMP;
        internal static ConfigEntry<KeyboardShortcut> SetOutGradedCardsKey;
        internal static ConfigEntry<int> GradedKeepCardQty;
        internal static ConfigEntry<bool> GradedOnlyToVintageTable;

        // ───────────── Graded Company Filters ─────────────
        internal static ConfigEntry<bool> GradedAllowCardinals;
        internal static ConfigEntry<bool> GradedAllowPSA;
        internal static ConfigEntry<bool> GradedAllowBeckett;

        // ───────────── Triggers ─────────────
        internal static ConfigEntry<bool> TriggerOnCustomerCardPickup;
        internal static ConfigEntry<bool> TriggerOnDayStart;
        internal static ConfigEntry<bool> GradedTriggerOnCustomerCardPickup;
        internal static ConfigEntry<bool> GradedTriggerOnDayStart;

        // ───────────── Mod Integration ─────────────
        internal static ConfigEntry<bool> TryTriggerPriceSlinger;

        // ───────────── Expansion Filters ─────────────
        internal static Dictionary<ECardExpansionType, ConfigEntry<bool>> EnabledExpansions;

        // ───────────── Performance ─────────────
        internal static ConfigEntry<int> CardBatchSize;

        // ───────────── Debug ─────────────
        internal static ConfigEntry<bool> DebugLogging;

        private void Awake()
        {
            Log = base.Logger;
            InitConfig();
            harmony.PatchAll();
            Log.LogInfo("SinglesSlinger loaded!");
        }

        private void OnDestroy()
        {
            StaticCoroutine.StopAll();
            harmony.UnpatchSelf();
            Log.LogInfo("SinglesSlinger unloaded!");
        }

        private void InitConfig()
        {
            // ── Placement ──
            OnlyPlaceMostExpensive = Config.Bind(
                "Placement", "Only Place Most Expensive", false,
                "If enabled, always place the highest market-price eligible card available.");

            SkipVintageTables = Config.Bind(
                "Placement", "Skip Vintage Tables", true,
                "If enabled, ungraded cards will not be placed on vintage card tables.");

            GradedOnlyToVintageTable = Config.Bind(
                "Placement", "Graded Only To Vintage Table", true,
                "If enabled, graded cards will only be placed onto vintage card tables.");

            // ── General ──
            SellOnlyGreaterThanMP = Config.Bind(
                "General", "SellOnlyGreaterThan", 0.5f,
                "Ignore ungraded cards with a market value below this threshold.");

            SellOnlyLessThanMP = Config.Bind(
                "General", "SellOnlyLessThan", 100f,
                "Ignore ungraded cards with a market value above this threshold.");

            SetOutCardsKey = Config.Bind(
                "General", "SetOutCardsKey", new KeyboardShortcut(KeyCode.F9),
                "Keyboard shortcut to manually set out ungraded cards.");

            KeepCardQty = Config.Bind(
                "General", "KeepCardQty", 0,
                "Keep at least this many duplicates of each ungraded card in the album.");

            ShowProgressPopUp = Config.Bind(
                "General", "ShowPopUpForNumCardsSet", false,
                "Show a popup indicating how many cards were matched and placed.");

            // ── Graded ──
            GradedSellOnlyGreaterThanMP = Config.Bind(
                "Graded", "SellOnlyGreaterThan", 0.5f,
                "Ignore graded cards with a market value below this threshold.");

            GradedSellOnlyLessThanMP = Config.Bind(
                "Graded", "SellOnlyLessThan", 2000f,
                "Ignore graded cards with a market value above this threshold.");

            SetOutGradedCardsKey = Config.Bind(
                "General", "SetOutGradedCardsKey", new KeyboardShortcut(KeyCode.F10),
                "Keyboard shortcut to manually set out graded cards.");

            GradedKeepCardQty = Config.Bind(
                "Graded", "KeepCardQty", 0,
                "Keep at least this many duplicates of each graded card (separate from ungraded KeepCardQty).");

            // ── Graded Company Filters ──
            GradedAllowCardinals = Config.Bind(
                "Graded - Company Filters", "Allow Cardinals", true,
                "Allow Cardinals-graded cards to be placed on shelves.");

            GradedAllowPSA = Config.Bind(
                "Graded - Company Filters", "Allow PSA", true,
                "Allow PSA-graded cards to be placed (requires GradingOverhaul mod).");

            GradedAllowBeckett = Config.Bind(
                "Graded - Company Filters", "Allow Beckett", true,
                "Allow Beckett-graded cards to be placed (requires GradingOverhaul mod).");

            // ── Expansion Filters ──
            EnabledExpansions = new Dictionary<ECardExpansionType, ConfigEntry<bool>>();
            foreach (ECardExpansionType expansion in Enum.GetValues(typeof(ECardExpansionType)))
            {
                if (expansion == ECardExpansionType.None)
                    continue;

                EnabledExpansions[expansion] = Config.Bind(
                    "Filters - Expansions",
                    $"Enable {expansion} Cards",
                    true,
                    $"Allow SinglesSlinger to place cards from the {expansion} expansion.");
            }

            // ── Triggers ──
            TriggerOnCustomerCardPickup = Config.Bind(
                "Triggers", "ShouldTriggerOnCardPickup", false,
                "Automatically fill shelves with ungraded cards when a customer picks up a card.");

            TriggerOnDayStart = Config.Bind(
                "Triggers", "ShouldTriggerOnDayStart", true,
                "Automatically fill shelves with ungraded cards when the day begins.");

            GradedTriggerOnCustomerCardPickup = Config.Bind(
                "Triggers", "GradedShouldTriggerOnCardPickup", false,
                "Automatically fill shelves with graded cards when a customer picks up a card.");

            GradedTriggerOnDayStart = Config.Bind(
                "Triggers", "GradedShouldTriggerOnDayStart", false,
                "Automatically fill shelves with graded cards when the day begins.");

            // ── Mod Integration ──
            TryTriggerPriceSlinger = Config.Bind(
                "Mod_Integration", "ShouldTriggerPriceSlinger", true,
                "If PriceSlinger mod is installed, ask it to price cards placed on shelves.");

            // ── Performance ──
            CardBatchSize = Config.Bind(
                "Performance", "CardBatchSize", 20,
                "Number of cards to process per frame during scan/placement. " +
                "Higher = faster but may cause frame drops on slower hardware.");

            // ── Debug ──
            DebugLogging = Config.Bind(
                "Debug", "DebugLogging", false,
                "Enable verbose debug logging to the console.");
        }
    }
}