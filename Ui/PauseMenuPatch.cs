using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;

namespace CardsAndRelicsChooser;

[HarmonyPatch(typeof(NPauseMenu), "_Ready")]
internal static class PauseMenuPatch
{
    private const string AddCardEntryName = "StartHandPickerAddDeckCardEntry";
    private const string RemoveCardEntryName = "StartHandPickerRemoveDeckCardEntry";
    private const string AddRelicEntryName = "StartHandPickerAddRelicEntry";
    private const string RemoveRelicEntryName = "StartHandPickerRemoveRelicEntry";

    public static void Postfix(NPauseMenu __instance)
    {
        try
        {
            AttachPauseMenuEntry(__instance, AddCardEntryName, "添加卡牌", LiveDeckEditor.OpenAddCardSelector);
            AttachPauseMenuEntry(__instance, RemoveCardEntryName, "删除卡牌", LiveDeckEditor.OpenRemoveCardSelector);
            AttachPauseMenuEntry(__instance, AddRelicEntryName, "添加遗物", LiveDeckEditor.OpenAddRelicSelector);
            AttachPauseMenuEntry(__instance, RemoveRelicEntryName, "删除遗物", LiveDeckEditor.OpenRemoveRelicSelector);
            ApplyButtonLayout(__instance);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to attach in-run editor UI: {ex.Message}");
        }
    }

    public static void ApplyButtonLayout(NPauseMenu pauseMenu)
    {
        var buttonContainer = pauseMenu.GetNodeOrNull<Control>("%ButtonContainer");
        if (buttonContainer == null)
        {
            return;
        }

        var desiredOrder = new[]
        {
            "Resume",
            "Settings",
            "Compendium",
            AddCardEntryName,
            RemoveCardEntryName,
            AddRelicEntryName,
            RemoveRelicEntryName,
            "Disconnect",
            "GiveUp",
            "SaveAndQuit"
        };

        var targetIndex = 0;
        foreach (var name in desiredOrder)
        {
            var button = buttonContainer.GetNodeOrNull<NPauseMenuButton>(name);
            if (button == null)
            {
                continue;
            }

            buttonContainer.MoveChild(button, Math.Min(targetIndex, buttonContainer.GetChildCount(false) - 1));
            targetIndex++;
        }

        foreach (var button in buttonContainer.GetChildren().OfType<NPauseMenuButton>().ToList())
        {
            var buttonName = button.Name.ToString();
            if (desiredOrder.Contains(buttonName, StringComparer.Ordinal))
            {
                continue;
            }

            buttonContainer.MoveChild(button, Math.Min(targetIndex, buttonContainer.GetChildCount(false) - 1));
            targetIndex++;
        }

        RebuildFocusNeighbors(buttonContainer);
    }

    private static void AttachPauseMenuEntry(NPauseMenu pauseMenu, string nodeName, string text, Action onReleased)
    {
        var buttonContainer = pauseMenu.GetNodeOrNull<Control>("%ButtonContainer");
        if (buttonContainer == null)
        {
            return;
        }

        if (buttonContainer.GetNodeOrNull<NPauseMenuButton>(nodeName) != null)
        {
            return;
        }

        var template = buttonContainer.GetNodeOrNull<NPauseMenuButton>("Resume")
            ?? buttonContainer.GetNodeOrNull<NPauseMenuButton>("Settings")
            ?? buttonContainer.GetChildren().OfType<NPauseMenuButton>().FirstOrDefault();

        if (template == null)
        {
            Log.Warn("Pause menu template button not found; cannot create styled editor entry.");
            return;
        }

        var flags = Node.DuplicateFlags.Groups | Node.DuplicateFlags.Scripts | Node.DuplicateFlags.UseInstantiation;
        if (template.Duplicate((int)flags) is not NPauseMenuButton newButton)
        {
            Log.Warn("Failed to duplicate pause menu button template for editor entry.");
            return;
        }

        newButton.Name = nodeName;
        newButton.FocusMode = Control.FocusModeEnum.All;
        var labelNode = newButton.GetNodeOrNull<Node>("Label");
        labelNode?.Call("SetTextAutoSize", text);

        newButton.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => onReleased()), 0U);
        buttonContainer.AddChild(newButton);
    }

    private static void RebuildFocusNeighbors(Control buttonContainer)
    {
        var buttons = buttonContainer
            .GetChildren()
            .OfType<NPauseMenuButton>()
            .Where(b => b.Visible)
            .ToList();

        for (var i = 0; i < buttons.Count; i++)
        {
            var button = buttons[i];
            button.FocusNeighborLeft = button.GetPath();
            button.FocusNeighborRight = button.GetPath();
            button.FocusNeighborTop = (i > 0) ? buttons[i - 1].GetPath() : button.GetPath();
            button.FocusNeighborBottom = (i < buttons.Count - 1) ? buttons[i + 1].GetPath() : button.GetPath();
        }
    }
}

[HarmonyPatch(typeof(NPauseMenu), nameof(NPauseMenu.Initialize))]
internal static class PauseMenuInitializePatch
{
    public static void Postfix(NPauseMenu __instance)
    {
        PauseMenuPatch.ApplyButtonLayout(__instance);
    }
}

