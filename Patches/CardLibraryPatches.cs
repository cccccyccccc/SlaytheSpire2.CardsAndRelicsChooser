using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;

namespace CardsAndRelicsChooser;

[HarmonyPatch(typeof(NCardLibrary), "_Ready")]
internal static class CardLibraryReadyPatch
{
    public static void Postfix(NCardLibrary __instance)
    {
        try
        {
            __instance.GetNodeOrNull<NLibraryStatTickbox>("%Upgrades")?.SetLabel("加入升级版");
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to relabel card library upgrades toggle: {ex.Message}");
        }
    }
}

[HarmonyPatch(typeof(NCardLibrary), "ShowCardDetail")]
internal static class CardLibraryShowCardDetailPatch
{
    public static bool Prefix(NCardLibrary __instance, NCardHolder holder)
    {
        return !LiveDeckEditor.TryHandleCardLibrarySelection(__instance, holder);
    }
}

[HarmonyPatch(typeof(NCardLibrary), nameof(NCardLibrary.OnSubmenuOpened))]
internal static class CardLibraryOnSubmenuOpenedPatch
{
    public static void Postfix()
    {
        LiveDeckEditor.NotifyCardLibraryOpened();
    }
}

[HarmonyPatch(typeof(NCardLibrary), nameof(NCardLibrary.OnSubmenuClosed))]
internal static class CardLibraryOnSubmenuClosedPatch
{
    public static void Postfix()
    {
        LiveDeckEditor.NotifyCardLibraryClosed();
    }
}

