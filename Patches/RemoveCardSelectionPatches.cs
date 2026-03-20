using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace CardsAndRelicsChooser;

[HarmonyPatch(typeof(NDeckCardSelectScreen), "_Ready")]
internal static class DeckCardSelectScreenReadyPatch
{
    public static void Postfix(NDeckCardSelectScreen __instance)
    {
        LiveDeckEditor.TryHideRemoveCardPeekButton(__instance);
    }
}

[HarmonyPatch(typeof(NDeckCardSelectScreen), nameof(NDeckCardSelectScreen.AfterOverlayShown))]
internal static class DeckCardSelectScreenAfterOverlayShownPatch
{
    public static void Postfix(NDeckCardSelectScreen __instance)
    {
        LiveDeckEditor.TryHideRemoveCardPeekButton(__instance);
    }
}
