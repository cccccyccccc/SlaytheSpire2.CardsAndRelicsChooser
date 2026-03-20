using System;
using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;

namespace CardsAndRelicsChooser;

[HarmonyPatch(typeof(NGlobalUi), "_Ready")]
internal static class RunSidebarGlobalUiReadyPatch
{
    public static void Postfix(NGlobalUi __instance)
    {
        RunSidebarUi.Attach(__instance);
    }
}

[HarmonyPatch(typeof(NGlobalUi), "_ExitTree")]
internal static class RunSidebarGlobalUiExitTreePatch
{
    public static void Postfix(NGlobalUi __instance)
    {
        RunSidebarUi.Detach(__instance);
    }
}

[HarmonyPatch(typeof(NPauseMenu), "_Ready")]
internal static class RunSidebarPauseMenuReadyPatch
{
    public static void Postfix(NPauseMenu __instance)
    {
        RunSidebarUi.OnPauseMenuReady(__instance);
    }
}

internal static class RunSidebarUi
{
    private const string RootName = "CardsAndRelicsChooserSidebarRoot";
    private const string ToggleName = "CardsAndRelicsChooserSidebarToggle";
    private const string PanelName = "CardsAndRelicsChooserSidebarPanel";
    private const string ToggleStoneBgName = "CardsAndRelicsChooserToggleStoneBg";
    private const string AddCardBtnName = "CardsAndRelicsChooserAddCardBtn";
    private const string EnchantCardBtnName = "CardsAndRelicsChooserEnchantCardBtn";
    private const string RemoveCardBtnName = "CardsAndRelicsChooserRemoveCardBtn";
    private const string AddRelicBtnName = "CardsAndRelicsChooserAddRelicBtn";
    private const string RemoveRelicBtnName = "CardsAndRelicsChooserRemoveRelicBtn";

    private static readonly string NativePopupScenePath = SceneHelper.GetScenePath("ui/vertical_popup");

    private static NGlobalUi? _globalUi;

    private static StyleBox? _cachedPausePanelStyle;
    private static Texture2D? _cachedPauseButtonTexture;
    private static bool _loggedPauseUiAssets;

    private static StyleBox? _cachedPopupPanelStyle;
    private static bool _triedPopupPanelStyle;

    internal static void Attach(NGlobalUi globalUi)
    {
        try
        {
            if (globalUi.GetNodeOrNull<Control>(RootName) != null)
            {
                _globalUi = globalUi;
                RefreshSidebarVisuals();
                return;
            }

            var root = new Control
            {
                Name = RootName,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                ZIndex = 1200
            };

            root.AnchorLeft = 0f;
            root.AnchorRight = 0f;
            root.AnchorTop = 0.5f;
            root.AnchorBottom = 0.5f;
            root.OffsetLeft = 14f;
            root.OffsetRight = 360f;
            root.OffsetTop = -210f;
            root.OffsetBottom = 210f;

            var toggle = CreateToggleButton();
            var panel = CreateActionPanel();

            root.AddChild(panel);
            root.AddChild(toggle);

            WireToggle(toggle, panel);
            WireActionButtons(panel, toggle);

            globalUi.AddChild(root);
            _globalUi = globalUi;

            TryWarmPauseMenuAssets();
            RefreshSidebarVisuals();

            Log.Info("Attached run sidebar launcher UI.");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to attach run sidebar launcher: {ex.Message}");
        }
    }

    internal static void Detach(NGlobalUi globalUi)
    {
        try
        {
            var existing = globalUi.GetNodeOrNull<Control>(RootName);
            existing?.QueueFree();

            if (_globalUi == globalUi)
            {
                _globalUi = null;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to detach run sidebar launcher: {ex.Message}");
        }
    }

    internal static void OnPauseMenuReady(NPauseMenu pauseMenu)
    {
        try
        {
            CachePauseMenuAssets(pauseMenu);
            RefreshSidebarVisuals();
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to process pause-menu assets for sidebar: {ex.Message}");
        }
    }

    private static Button CreateToggleButton()
    {
        var toggle = new Button
        {
            Name = ToggleName,
            Text = "▶",
            MouseFilter = Control.MouseFilterEnum.Stop,
            FocusMode = Control.FocusModeEnum.None,
            TooltipText = "展开卡牌/遗物编辑菜单",
            ClipContents = true
        };

        toggle.CustomMinimumSize = new Vector2(92f, 92f);
        toggle.AnchorLeft = 0f;
        toggle.AnchorRight = 0f;
        toggle.AnchorTop = 0.5f;
        toggle.AnchorBottom = 0.5f;
        toggle.OffsetLeft = 0f;
        toggle.OffsetRight = 92f;
        toggle.OffsetTop = -46f;
        toggle.OffsetBottom = 46f;

        StyleArrowButton(toggle);
        return toggle;
    }

    private static PanelContainer CreateActionPanel()
    {
        var panel = new PanelContainer
        {
            Name = PanelName,
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Stop,
            FocusMode = Control.FocusModeEnum.None
        };

        panel.AnchorLeft = 0f;
        panel.AnchorRight = 0f;
        panel.AnchorTop = 0.5f;
        panel.AnchorBottom = 0.5f;
        panel.OffsetLeft = 104f;
        panel.OffsetRight = 352f;
        panel.OffsetTop = -156f;
        panel.OffsetBottom = 156f;

        ApplyPanelBackground(panel);

        var container = new VBoxContainer
        {
            Name = "ButtonContainer",
            MouseFilter = Control.MouseFilterEnum.Ignore
        };

        container.AnchorLeft = 0f;
        container.AnchorRight = 1f;
        container.AnchorTop = 0f;
        container.AnchorBottom = 1f;
        container.OffsetLeft = 12f;
        container.OffsetRight = -12f;
        container.OffsetTop = 12f;
        container.OffsetBottom = -12f;
        container.AddThemeConstantOverride("separation", 8);

        container.AddChild(CreateActionButton(AddCardBtnName, "添加卡牌"));
        container.AddChild(CreateActionButton(EnchantCardBtnName, "附魔卡牌"));
        container.AddChild(CreateActionButton(RemoveCardBtnName, "删除卡牌"));
        container.AddChild(CreateActionButton(AddRelicBtnName, "添加遗物"));
        container.AddChild(CreateActionButton(RemoveRelicBtnName, "删除遗物"));

        panel.AddChild(container);
        return panel;
    }

    private static Button CreateActionButton(string name, string text)
    {
        var button = new Button
        {
            Name = name,
            Text = text,
            MouseFilter = Control.MouseFilterEnum.Stop,
            FocusMode = Control.FocusModeEnum.None,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(200f, 44f)
        };

        StyleActionButton(button);
        return button;
    }

    private static void ApplyPanelBackground(PanelContainer panel)
    {
        panel.AddThemeStyleboxOverride("panel", GetPreferredPanelStyle());
    }

    private static StyleBox GetPreferredPanelStyle()
    {
        return CloneStyleBox(_cachedPausePanelStyle)
            ?? TryGetPopupPanelStyle()
            ?? CreateFallbackPanelStyle();
    }

    private static StyleBox? TryGetPopupPanelStyle()
    {
        if (_triedPopupPanelStyle)
        {
            return CloneStyleBox(_cachedPopupPanelStyle);
        }

        _triedPopupPanelStyle = true;

        try
        {
            var popupScene = PreloadManager.Cache.GetScene(NativePopupScenePath);
            if (popupScene == null)
            {
                return null;
            }

            var popupRoot = popupScene.Instantiate<Control>(PackedScene.GenEditState.Disabled);
            try
            {
                _cachedPopupPanelStyle = ExtractPanelStyle(popupRoot);
                if (_cachedPopupPanelStyle != null)
                {
                    Log.Info($"Using fallback popup panel style from '{NativePopupScenePath}' ({_cachedPopupPanelStyle.GetClass()}).");
                }
            }
            finally
            {
                popupRoot.Free();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to load popup panel style fallback: {ex.Message}");
        }

        return CloneStyleBox(_cachedPopupPanelStyle);
    }

    private static void TryWarmPauseMenuAssets()
    {
        try
        {
            var pauseMenu = _globalUi?.SubmenuStack?.Stack?.GetSubmenuType<NPauseMenu>();
            if (!IsAlive(pauseMenu))
            {
                return;
            }

            CachePauseMenuAssets(pauseMenu!);
        }
        catch (Exception ex)
        {
            Log.Warn($"Warm pause-menu asset fetch skipped: {ex.Message}");
        }
    }

    private static void CachePauseMenuAssets(NPauseMenu pauseMenu)
    {
        if (!IsAlive(pauseMenu))
        {
            return;
        }

        var buttonContainer = pauseMenu.GetNodeOrNull<Control>("%ButtonContainer")
            ?? pauseMenu.GetNodeOrNull<Control>("ButtonContainer");

        PanelContainer? panelContainer = null;
        if (buttonContainer != null)
        {
            panelContainer = FindAncestorPanelContainer(buttonContainer);

            var resumeButton = buttonContainer.GetNodeOrNull<NPauseMenuButton>("Resume");
            var buttonImage = resumeButton?.GetNodeOrNull<TextureRect>("ButtonImage");
            if (buttonImage?.Texture != null)
            {
                _cachedPauseButtonTexture = buttonImage.Texture;
            }
        }

        panelContainer ??= pauseMenu.GetNodeOrNull<PanelContainer>("PanelContainer");
        if (panelContainer != null)
        {
            var pauseStyle = CloneStyleBox(panelContainer.GetThemeStylebox("panel"));
            if (pauseStyle != null && pauseStyle is not StyleBoxEmpty)
            {
                _cachedPausePanelStyle = pauseStyle;
            }
        }

        if (!_loggedPauseUiAssets)
        {
            _loggedPauseUiAssets = true;
            Log.Info($"Pause UI stone sources: panelStyleClass={_cachedPausePanelStyle?.GetClass() ?? "null"}, buttonTexturePath='{_cachedPauseButtonTexture?.ResourcePath ?? ""}'");
        }
    }

    private static PanelContainer? FindAncestorPanelContainer(Node start)
    {
        Node? current = start;
        while (current != null)
        {
            if (current is PanelContainer panelContainer)
            {
                return panelContainer;
            }

            current = current.GetParent();
        }

        return null;
    }

    private static void RefreshSidebarVisuals()
    {
        var globalUi = _globalUi;
        if (!IsAlive(globalUi))
        {
            return;
        }

        var root = globalUi!.GetNodeOrNull<Control>(RootName);
        if (!IsAlive(root))
        {
            return;
        }

        var panel = root!.GetNodeOrNull<PanelContainer>(PanelName);
        if (IsAlive(panel))
        {
            ApplyPanelBackground(panel!);
        }

        var toggle = root.GetNodeOrNull<Button>(ToggleName);
        if (IsAlive(toggle))
        {
            StyleArrowButton(toggle!);
        }
    }

    private static StyleBox? ExtractPanelStyle(Node root)
    {
        var stack = new Stack<Node>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is PanelContainer panel)
            {
                var style = CloneStyleBox(panel.GetThemeStylebox("panel"));
                if (style != null && style is not StyleBoxEmpty)
                {
                    return style;
                }
            }
            else if (node is Control control)
            {
                var style = CloneStyleBox(control.GetThemeStylebox("panel"));
                if (style != null && style is not StyleBoxEmpty)
                {
                    return style;
                }
            }

            for (var i = node.GetChildCount() - 1; i >= 0; i--)
            {
                var child = node.GetChild(i);
                if (child != null)
                {
                    stack.Push(child);
                }
            }
        }

        return null;
    }

    private static StyleBox? CloneStyleBox(StyleBox? source)
    {
        return source?.Duplicate() as StyleBox ?? source;
    }

    private static bool IsAlive(Node? node)
    {
        return node != null && !node.IsQueuedForDeletion();
    }

    private static void WireToggle(Button toggle, PanelContainer panel)
    {
        toggle.Connect(BaseButton.SignalName.Pressed, Callable.From(() =>
        {
            var visible = !panel.Visible;
            panel.Visible = visible;
            toggle.Text = visible ? "◀" : "▶";
        }), 0U);
    }

    private static void WireActionButtons(PanelContainer panel, Button toggle)
    {
        ConnectAction(panel, AddCardBtnName, () => LiveDeckEditor.OpenAddCardSelector(), toggle);
        ConnectAction(panel, EnchantCardBtnName, () => LiveDeckEditor.OpenEnchantCardSelector(), toggle);
        ConnectAction(panel, RemoveCardBtnName, () => LiveDeckEditor.OpenRemoveCardSelector(), toggle);
        ConnectAction(panel, AddRelicBtnName, () => LiveDeckEditor.OpenAddRelicSelector(), toggle);
        ConnectAction(panel, RemoveRelicBtnName, () => LiveDeckEditor.OpenRemoveRelicSelector(), toggle);
    }

    private static void ConnectAction(PanelContainer panel, string buttonName, Action action, Button toggle)
    {
        var button = panel.GetNodeOrNull<Button>($"ButtonContainer/{buttonName}");
        if (button == null)
        {
            return;
        }

        button.Connect(BaseButton.SignalName.Pressed, Callable.From(() =>
        {
            panel.Visible = false;
            toggle.Text = "▶";
            action();
        }), 0U);
    }

    private static void StyleArrowButton(Button button)
    {
        EnsureToggleStoneBackground(button);

        var hasStone = _cachedPauseButtonTexture != null;

        button.AddThemeFontSizeOverride("font_size", 52);
        button.AddThemeColorOverride("font_color", new Color(0.95f, 0.82f, 0.32f, 1f));
        button.AddThemeColorOverride("font_focus_color", new Color(1f, 0.9f, 0.45f, 1f));
        button.AddThemeColorOverride("font_hover_color", new Color(1f, 0.9f, 0.45f, 1f));

        var normalBg = hasStone ? new Color(0f, 0f, 0f, 0.22f) : new Color(0.08f, 0.11f, 0.16f, 0.82f);
        var hoverBg = hasStone ? new Color(0f, 0f, 0f, 0.12f) : new Color(0.12f, 0.16f, 0.22f, 0.9f);
        var pressedBg = hasStone ? new Color(0f, 0f, 0f, 0.30f) : new Color(0.05f, 0.08f, 0.12f, 0.96f);

        button.AddThemeStyleboxOverride("normal", CreateButtonStyle(normalBg, 13));
        button.AddThemeStyleboxOverride("hover", CreateButtonStyle(hoverBg, 13));
        button.AddThemeStyleboxOverride("pressed", CreateButtonStyle(pressedBg, 13));
        button.AddThemeStyleboxOverride("focus", CreateButtonStyle(hoverBg, 13));
    }

    private static void EnsureToggleStoneBackground(Button button)
    {
        var existing = button.GetNodeOrNull<TextureRect>(ToggleStoneBgName);
        if (_cachedPauseButtonTexture == null)
        {
            existing?.QueueFree();
            return;
        }

        if (existing == null)
        {
            existing = new TextureRect
            {
                Name = ToggleStoneBgName,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                StretchMode = TextureRect.StretchModeEnum.Scale,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                ZIndex = -5
            };

            existing.AnchorLeft = 0f;
            existing.AnchorRight = 1f;
            existing.AnchorTop = 0f;
            existing.AnchorBottom = 1f;
            existing.OffsetLeft = 0f;
            existing.OffsetRight = 0f;
            existing.OffsetTop = 0f;
            existing.OffsetBottom = 0f;

            button.AddChild(existing);
            button.MoveChild(existing, 0);
        }

        existing.Texture = _cachedPauseButtonTexture;
        existing.SelfModulate = new Color(1f, 1f, 1f, 0.95f);
    }

    private static void StyleActionButton(Button button)
    {
        button.AddThemeFontSizeOverride("font_size", 26);
        button.AddThemeColorOverride("font_color", new Color(0.95f, 0.94f, 0.88f, 1f));
        button.AddThemeColorOverride("font_focus_color", new Color(1f, 1f, 1f, 1f));
        button.AddThemeColorOverride("font_hover_color", new Color(1f, 1f, 1f, 1f));

        button.AddThemeStyleboxOverride("normal", CreateButtonStyle(new Color(0.13f, 0.18f, 0.24f, 0.9f), 9));
        button.AddThemeStyleboxOverride("hover", CreateButtonStyle(new Color(0.18f, 0.24f, 0.31f, 0.95f), 9));
        button.AddThemeStyleboxOverride("pressed", CreateButtonStyle(new Color(0.09f, 0.13f, 0.18f, 1f), 9));
        button.AddThemeStyleboxOverride("focus", CreateButtonStyle(new Color(0.18f, 0.24f, 0.31f, 0.95f), 9));
    }

    private static StyleBoxFlat CreateButtonStyle(Color bgColor, int radius)
    {
        return new StyleBoxFlat
        {
            BgColor = bgColor,
            BorderColor = new Color(0f, 0f, 0f, 0f),
            BorderWidthTop = 0,
            BorderWidthBottom = 0,
            BorderWidthLeft = 0,
            BorderWidthRight = 0,
            CornerRadiusTopLeft = radius,
            CornerRadiusTopRight = radius,
            CornerRadiusBottomLeft = radius,
            CornerRadiusBottomRight = radius
        };
    }

    private static StyleBoxFlat CreateFallbackPanelStyle()
    {
        return new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.09f, 0.13f, 0.9f),
            BorderColor = new Color(0.89f, 0.74f, 0.25f, 0.88f),
            BorderWidthTop = 2,
            BorderWidthBottom = 2,
            BorderWidthLeft = 2,
            BorderWidthRight = 2,
            CornerRadiusTopLeft = 10,
            CornerRadiusTopRight = 10,
            CornerRadiusBottomLeft = 10,
            CornerRadiusBottomRight = 10
        };
    }
}




