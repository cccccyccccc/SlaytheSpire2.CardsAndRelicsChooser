using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens;

namespace CardsAndRelicsChooser;

[HarmonyPatch(typeof(NChooseARelicSelection), "_Ready")]
internal static class ChooseRelicSelectionReadyPatch
{
    public static void Postfix(NChooseARelicSelection __instance)
    {
        LiveDeckEditor.TrySetupRelicMultiSelectUi(__instance);
    }
}

[HarmonyPatch(typeof(NChooseARelicSelection), nameof(NChooseARelicSelection.AfterOverlayOpened))]
internal static class ChooseRelicSelectionAfterOverlayOpenedPatch
{
    public static void Postfix(NChooseARelicSelection __instance)
    {
        LiveDeckEditor.TrySetupRelicMultiSelectUi(__instance);
    }
}

[HarmonyPatch(typeof(NChooseARelicSelection), "SelectHolder")]
internal static class ChooseRelicSelectionSelectHolderPatch
{
    public static bool Prefix(NChooseARelicSelection __instance, NRelicBasicHolder relicHolder)
    {
        if (!LiveDeckEditor.IsRelicRemovalMultiSelectScreen(__instance))
        {
            return true;
        }

        if (relicHolder == null)
        {
            return false;
        }

        LiveDeckEditor.TryToggleRelicMultiSelection(__instance, relicHolder);
        return false;
    }
}

[HarmonyPatch(typeof(NChooseARelicSelection), "OnSkipButtonReleased")]
internal static class ChooseRelicSelectionConfirmPatch
{
    public static bool Prefix(NChooseARelicSelection __instance)
    {
        if (!LiveDeckEditor.IsRelicRemovalMultiSelectScreen(__instance))
        {
            return true;
        }

        var handled = LiveDeckEditor.TryConfirmRelicMultiSelection(__instance);
        return !handled;
    }
}

[HarmonyPatch(typeof(NChooseARelicSelection), "_ExitTree")]
internal static class ChooseRelicSelectionExitTreePatch
{
    public static void Postfix(NChooseARelicSelection __instance)
    {
        if (LiveDeckEditor.IsRelicRemovalMultiSelectScreen(__instance))
        {
            LiveDeckEditor.EndRelicMultiSelectSession();
        }
    }
}


