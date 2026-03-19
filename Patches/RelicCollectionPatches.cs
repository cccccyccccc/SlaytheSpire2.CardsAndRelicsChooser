using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.RelicCollection;

namespace CardsAndRelicsChooser;

[HarmonyPatch(typeof(NRelicCollectionCategory), "OnRelicEntryPressed")]
internal static class RelicCollectionEntryPressedPatch
{
    public static bool Prefix(NRelicCollectionEntry entry)
    {
        return !LiveDeckEditor.TryHandleRelicCollectionSelection(entry);
    }
}

[HarmonyPatch(typeof(NRelicCollection), nameof(NRelicCollection.OnSubmenuOpened))]
internal static class RelicCollectionOnSubmenuOpenedPatch
{
    public static void Postfix()
    {
        LiveDeckEditor.NotifyRelicCollectionOpened();
    }
}

[HarmonyPatch(typeof(NRelicCollection), nameof(NRelicCollection.OnSubmenuClosed))]
internal static class RelicCollectionOnSubmenuClosedPatch
{
    public static void Postfix()
    {
        LiveDeckEditor.NotifyRelicCollectionClosed();
    }
}

