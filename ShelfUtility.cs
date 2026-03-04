using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SinglesSlinger
{
    /// <summary>
    /// Utility methods for shelf/table identification, in-game popup messages,
    /// direct card placement, and PriceSlinger mod integration.
    /// </summary>
    internal static class ShelfUtility
    {
        // ── PriceSlinger reflection cache ──
        private static bool _priceSlingerCached;
        private static MethodInfo _priceSlingerMethod;

        /// <summary>
        /// Determines whether the given shelf object is a Vintage Card Table.
        /// </summary>
        internal static bool IsVintageTable(object shelf)
        {
            try
            {
                if (shelf == null) return false;
                Type t = shelf.GetType();

                FieldInfo field =
                    AccessTools.Field(t, "m_ObjectType") ??
                    AccessTools.Field(t, "objectType") ??
                    AccessTools.Field(t, "ObjectType");

                if (field != null)
                {
                    object val = field.GetValue(shelf);
                    if (val is EObjectType ot && ot == EObjectType.VintageCardTable) return true;
                    if (val != null && val.ToString() == "VintageCardTable") return true;
                }

                const BindingFlags flags =
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                PropertyInfo prop =
                    t.GetProperty("m_ObjectType", flags) ??
                    t.GetProperty("ObjectType", flags) ??
                    t.GetProperty("objectType", flags);

                if (prop != null)
                {
                    object val = prop.GetValue(shelf, null);
                    if (val is EObjectType ot && ot == EObjectType.VintageCardTable) return true;
                    if (val != null && val.ToString() == "VintageCardTable") return true;
                }

                PropertyInfo nameProp = t.GetProperty("name", flags);
                if (nameProp != null)
                {
                    string n = nameProp.GetValue(shelf, null) as string;
                    if (!string.IsNullOrEmpty(n) &&
                        n.IndexOf("vintage", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// Shows a text popup using the game's NotEnoughResourceTextPopup system.
        /// Respects <see cref="Plugin.ShowProgressPopUp"/> — does nothing when disabled.
        /// </summary>
        internal static void ShowPopup(string text)
        {
            if (Plugin.ShowProgressPopUp == null || !Plugin.ShowProgressPopUp.Value)
                return;

            try
            {
                var popup = CSingleton<NotEnoughResourceTextPopup>.Instance;
                if (popup == null) return;

                for (int j = 0; j < popup.m_ShowTextGameObjectList.Count; j++)
                {
                    if (popup.m_ShowTextGameObjectList[j] != null &&
                        !popup.m_ShowTextGameObjectList[j].activeSelf)
                    {
                        popup.m_ShowTextList[j].text = text;
                        popup.m_ShowTextGameObjectList[j].gameObject.SetActive(true);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                LogHelper.LogErrorThrottled("ShowPopup",
                    "[SinglesSlinger] ShowPopup failed: " + ex.Message, 15f);
            }
        }

        /// <summary>
        /// Places a card directly onto a compartment without animation or lerp.
        /// Sets the price tag, stored card list, and electronic listener.
        /// </summary>
        internal static void PlaceCardDirect(
            InteractableCardCompartment compartment, CardData cardData)
        {
            Card3dUIGroup cardUI = CSingleton<Card3dUISpawner>.Instance.GetCardUI();
            InteractableCard3d card3d = ShelfManager
                .SpawnInteractableObject(EObjectType.Card3d)
                .GetComponent<InteractableCard3d>();

            cardUI.m_IgnoreCulling = true;
            cardUI.m_CardUI.SetFoilCullListVisibility(true);
            cardUI.SetSimplifyCardDistanceCull(false);
            cardUI.m_CardUI.ResetFarDistanceCull();
            cardUI.m_CardUI.SetCardUI(cardData);

            // Choose the appropriate placement transform (alt location for non-graded if set)
            Transform targetLoc;
            if (compartment.m_NoneGradedCardUseAltCardLocation
                && cardData.cardGrade == 0
                && compartment.m_PutCardLocationAlt != null)
            {
                targetLoc = compartment.m_PutCardLocationAlt;
            }
            else
            {
                targetLoc = compartment.m_PutCardLocation;
            }

            // Defensive fallback if no location transform is assigned
            if (targetLoc == null)
                targetLoc = compartment.transform;

            // Parent and position the 3D card object
            if (compartment.m_StoredItemListGrp != null)
                card3d.transform.parent = compartment.m_StoredItemListGrp;

            card3d.transform.position = targetLoc.position;
            card3d.transform.rotation = targetLoc.rotation;
            card3d.transform.localScale = targetLoc.localScale;

            cardUI.transform.position = targetLoc.position;
            cardUI.transform.rotation = targetLoc.rotation;

            card3d.SetCardUIFollow(cardUI);
            card3d.SetEnableCollision(false);

            cardUI.m_IgnoreCulling = false;
            card3d.SetIsDisplayedOnShelf(true);

            if (cardUI.m_ScaleGrp != null)
                cardUI.m_ScaleGrp.localRotation = Quaternion.identity;

            // Update compartment state
            compartment.SetPriceTagCardData(cardData);
            compartment.SetPriceTagItemPriceText(cardData);
            compartment.m_StoredCardList.Add(card3d);
            compartment.SetPriceTagVisibility(true);

            // Notify electronic card display if present
            CardShelf shelf = compartment.GetCardShelf();
            if (shelf != null && shelf.m_ElectronicCardListener != null)
                shelf.m_ElectronicCardListener.UpdateCardUI(cardData);
        }

        /// <summary>
        /// If PriceSlinger mod is installed, asks it to price the compartment.
        /// Caches the reflection lookup so it only happens once.
        /// </summary>
        internal static void TryTellPriceSlinger(InteractableCardCompartment cardCompart)
        {
            try
            {
                if (!_priceSlingerCached)
                {
                    _priceSlingerCached = true;
                    Type t = AccessTools.TypeByName("PriceSlinger.Pricer");
                    if (t != null)
                        _priceSlingerMethod = AccessTools.Method(t, "PriceCompartment");

                    if (_priceSlingerMethod != null)
                        LogHelper.LogDebug("[SinglesSlinger] PriceSlinger.Pricer.PriceCompartment cached.");
                    else
                        LogHelper.LogDebug("[SinglesSlinger] PriceSlinger not found — integration disabled.");
                }

                if (_priceSlingerMethod == null) return;

                _priceSlingerMethod.Invoke(null, new object[] { cardCompart });
            }
            catch (Exception ex)
            {
                LogHelper.LogErrorThrottled("PriceSlingerHookFailed",
                    "[SinglesSlinger] PriceSlinger hook failed:\r\n" + ex, 15f);
            }
        }
    }
}