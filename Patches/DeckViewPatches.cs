using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.TopBar;

namespace CardsAndRelicsChooser;

[HarmonyPatch(typeof(NTopBarDeckButton), "OnRelease")]
internal static class TopBarDeckButtonOnReleasePatch
{
    public static void Prefix()
    {
        LiveDeckEditor.NotifyDeckViewOpenRequested();
    }
}

[HarmonyPatch(typeof(NDeckViewScreen), nameof(NDeckViewScreen.AfterCapstoneClosed))]
internal static class DeckViewAfterCapstoneClosedPatch
{
    public static void Postfix()
    {
        LiveDeckEditor.NotifyDeckViewClosed();
    }
}

