using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace CardsAndRelicsChooser;

[ModInitializer(nameof(Initialize))]
public static class Plugin
{
    private static Harmony? _harmony;

    public static void Initialize()
    {
        if (_harmony != null)
        {
            return;
        }

        _harmony = new Harmony("cardsandrelicschooser.harmony");
        _harmony.PatchAll();

        Log.Info("CardsAndRelicsChooser initialized.");
    }
}

