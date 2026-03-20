using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;

namespace CardsAndRelicsChooser;

internal static class EnchantmentPickerOverlay
{
    private const string OverlayName = "CardsAndRelicsChooserEnchantmentPicker";
    private static Control? _activeOverlay;

    public static async Task<EnchantmentModel?> Show(CardModel card, IReadOnlyList<EnchantmentModel> enchantments)
    {
        if (enchantments == null || enchantments.Count == 0)
        {
            return null;
        }

        var host = ResolveHost();
        if (host == null)
        {
            Log.Warn("Enchantment picker host unavailable.");
            return null;
        }

        CloseIfOpen();

        var completionSource = new TaskCompletionSource<EnchantmentModel?>();
        var overlay = BuildOverlay(card, enchantments, completionSource);
        _activeOverlay = overlay;

        host.AddChild(overlay);
        overlay.GrabFocus();

        try
        {
            return await completionSource.Task;
        }
        finally
        {
            if (ReferenceEquals(_activeOverlay, overlay))
            {
                _activeOverlay = null;
            }

            if (overlay.IsInsideTree())
            {
                overlay.QueueFree();
            }
        }
    }

    public static void CloseIfOpen()
    {
        var overlay = _activeOverlay;
        _activeOverlay = null;
        if (overlay != null && overlay.IsInsideTree())
        {
            overlay.QueueFree();
        }
    }

    private static Control? ResolveHost()
    {
        var globalUi = NRun.Instance?.GlobalUi;
        if (globalUi != null && !globalUi.IsQueuedForDeletion())
        {
            return globalUi;
        }

        var run = NRun.Instance;
        if (run != null && !run.IsQueuedForDeletion())
        {
            return run;
        }

        return null;
    }

    private static Control BuildOverlay(CardModel card, IReadOnlyList<EnchantmentModel> enchantments, TaskCompletionSource<EnchantmentModel?> completionSource)
    {
        var root = new Control
        {
            Name = OverlayName,
            MouseFilter = Control.MouseFilterEnum.Stop,
            FocusMode = Control.FocusModeEnum.All,
            ZIndex = 4000
        };

        root.AnchorLeft = 0f;
        root.AnchorRight = 1f;
        root.AnchorTop = 0f;
        root.AnchorBottom = 1f;
        root.OffsetLeft = 0f;
        root.OffsetRight = 0f;
        root.OffsetTop = 0f;
        root.OffsetBottom = 0f;

        var dim = new ColorRect
        {
            Name = "Dim",
            Color = new Color(0f, 0f, 0f, 0.62f),
            MouseFilter = Control.MouseFilterEnum.Stop
        };

        dim.AnchorLeft = 0f;
        dim.AnchorRight = 1f;
        dim.AnchorTop = 0f;
        dim.AnchorBottom = 1f;
        dim.OffsetLeft = 0f;
        dim.OffsetRight = 0f;
        dim.OffsetTop = 0f;
        dim.OffsetBottom = 0f;
        root.AddChild(dim);

        var panel = new PanelContainer
        {
            Name = "Panel",
            MouseFilter = Control.MouseFilterEnum.Stop,
            FocusMode = Control.FocusModeEnum.None,
            ZIndex = 1
        };

        panel.AnchorLeft = 0.5f;
        panel.AnchorRight = 0.5f;
        panel.AnchorTop = 0.5f;
        panel.AnchorBottom = 0.5f;
        panel.OffsetLeft = -460f;
        panel.OffsetRight = 460f;
        panel.OffsetTop = -250f;
        panel.OffsetBottom = 250f;
        panel.AddThemeStyleboxOverride("panel", CreatePanelStyle());

        root.AddChild(panel);

        var layout = new VBoxContainer
        {
            Name = "Layout"
        };

        layout.AnchorLeft = 0f;
        layout.AnchorRight = 1f;
        layout.AnchorTop = 0f;
        layout.AnchorBottom = 1f;
        layout.OffsetLeft = 18f;
        layout.OffsetRight = -18f;
        layout.OffsetTop = 18f;
        layout.OffsetBottom = -18f;
        layout.AddThemeConstantOverride("separation", 12);
        panel.AddChild(layout);

        var title = new Label
        {
            Text = $"为“{GetSafeCardTitle(card)}”选择附魔效果",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };

        title.AddThemeFontSizeOverride("font_size", 30);
        layout.AddChild(title);

        var tip = new Label
        {
            Text = "附魔完成后会返回选牌界面，可继续附魔",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };

        tip.AddThemeFontSizeOverride("font_size", 18);
        tip.Modulate = new Color(1f, 1f, 1f, 0.85f);
        layout.AddChild(tip);

        var body = new HBoxContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };

        body.AddThemeConstantOverride("separation", 16);
        layout.AddChild(body);

        var list = new ItemList
        {
            Name = "EnchantmentList",
            SelectMode = ItemList.SelectModeEnum.Single,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(360f, 300f)
        };

        for (var i = 0; i < enchantments.Count; i++)
        {
            list.AddItem(GetDisplayName(enchantments[i]));
        }

        body.AddChild(list);

        var right = new VBoxContainer
        {
            Name = "PreviewColumn",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };

        right.AddThemeConstantOverride("separation", 10);
        body.AddChild(right);

        var selectedTitle = new Label
        {
            Name = "SelectedTitle",
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };

        selectedTitle.AddThemeFontSizeOverride("font_size", 24);
        right.AddChild(selectedTitle);

        var icon = new TextureRect
        {
            Name = "Icon",
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            CustomMinimumSize = new Vector2(128f, 128f),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };

        right.AddChild(icon);

        var description = new Label
        {
            Name = "Description",
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };

        description.AddThemeFontSizeOverride("font_size", 20);
        right.AddChild(description);

        var buttons = new HBoxContainer
        {
            Name = "Buttons",
            Alignment = BoxContainer.AlignmentMode.End
        };

        buttons.AddThemeConstantOverride("separation", 10);
        layout.AddChild(buttons);

        var cancel = new Button
        {
            Name = "Cancel",
            Text = "取消",
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(130f, 42f)
        };

        var confirm = new Button
        {
            Name = "Confirm",
            Text = "确定",
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(130f, 42f)
        };

        StyleButton(cancel, false);
        StyleButton(confirm, true);

        buttons.AddChild(cancel);
        buttons.AddChild(confirm);

        void Complete(EnchantmentModel? result)
        {
            if (!completionSource.Task.IsCompleted)
            {
                completionSource.SetResult(result);
            }
        }

        void UpdatePreview(int index)
        {
            if (index < 0 || index >= enchantments.Count)
            {
                return;
            }

            var selected = enchantments[index];
            selectedTitle.Text = GetDisplayName(selected);
            icon.Texture = selected.Icon;
            description.Text = GetDescription(selected);
        }

        list.Connect(ItemList.SignalName.ItemSelected, Callable.From<long>(idx =>
        {
            UpdatePreview((int)idx);
        }), 0U);

        list.Connect(ItemList.SignalName.ItemActivated, Callable.From<long>(idx =>
        {
            var selectedIndex = (int)idx;
            if (selectedIndex >= 0 && selectedIndex < enchantments.Count)
            {
                Complete(enchantments[selectedIndex]);
            }
        }), 0U);

        confirm.Connect(BaseButton.SignalName.Pressed, Callable.From(() =>
        {
            var selected = list.GetSelectedItems();
            if (selected.Length == 0)
            {
                Complete(null);
                return;
            }

            var selectedIndex = selected[0];
            if (selectedIndex < 0 || selectedIndex >= enchantments.Count)
            {
                Complete(null);
                return;
            }

            Complete(enchantments[selectedIndex]);
        }), 0U);

        cancel.Connect(BaseButton.SignalName.Pressed, Callable.From(() =>
        {
            Complete(null);
        }), 0U);

        root.Connect(Control.SignalName.GuiInput, Callable.From<InputEvent>(evt =>
        {
            if (evt is InputEventKey key && key.Pressed && !key.Echo && key.Keycode == Key.Escape)
            {
                Complete(null);
                root.GetViewport()?.SetInputAsHandled();
            }
        }), 0U);

        list.Select(0);
        UpdatePreview(0);

        return root;
    }

    private static StyleBoxFlat CreatePanelStyle()
    {
        return new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.09f, 0.12f, 0.95f),
            BorderColor = new Color(0.94f, 0.84f, 0.42f, 0.9f),
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

    private static void StyleButton(Button button, bool primary)
    {
        var normal = primary
            ? new Color(0.17f, 0.28f, 0.20f, 0.95f)
            : new Color(0.19f, 0.20f, 0.24f, 0.95f);
        var hover = primary
            ? new Color(0.24f, 0.36f, 0.27f, 1f)
            : new Color(0.26f, 0.27f, 0.33f, 1f);
        var pressed = primary
            ? new Color(0.11f, 0.18f, 0.13f, 1f)
            : new Color(0.13f, 0.14f, 0.17f, 1f);

        button.AddThemeColorOverride("font_color", new Color(0.97f, 0.96f, 0.92f, 1f));
        button.AddThemeColorOverride("font_hover_color", Colors.White);
        button.AddThemeColorOverride("font_focus_color", Colors.White);
        button.AddThemeFontSizeOverride("font_size", 22);

        button.AddThemeStyleboxOverride("normal", CreateButtonStyle(normal));
        button.AddThemeStyleboxOverride("hover", CreateButtonStyle(hover));
        button.AddThemeStyleboxOverride("pressed", CreateButtonStyle(pressed));
        button.AddThemeStyleboxOverride("focus", CreateButtonStyle(hover));
    }

    private static StyleBoxFlat CreateButtonStyle(Color color)
    {
        return new StyleBoxFlat
        {
            BgColor = color,
            BorderColor = new Color(0f, 0f, 0f, 0f),
            BorderWidthTop = 0,
            BorderWidthBottom = 0,
            BorderWidthLeft = 0,
            BorderWidthRight = 0,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8
        };
    }

    private static string GetSafeCardTitle(CardModel card)
    {
        try
        {
            return string.IsNullOrWhiteSpace(card.Title) ? card.Id.Entry : card.Title;
        }
        catch
        {
            return card.Id.Entry;
        }
    }

    private static string GetDisplayName(EnchantmentModel enchantment)
    {
        try
        {
            return enchantment.Title.GetFormattedText();
        }
        catch
        {
            return enchantment.Id.Entry;
        }
    }

    private static string GetDescription(EnchantmentModel enchantment)
    {
        try
        {
            var preview = enchantment.ToMutable();
            preview.Amount = 1;
            preview.RecalculateValues();
            return StripRichText(preview.DynamicDescription.GetFormattedText());
        }
        catch
        {
            try
            {
                return StripRichText(enchantment.Description.GetFormattedText());
            }
            catch
            {
                return enchantment.Id.Entry;
            }
        }
    }

    private static string StripRichText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var text = raw
            .Replace("[br]", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("[/br]", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("[p]", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("[/p]", "\n", StringComparison.OrdinalIgnoreCase);

        // Remove bbcode-style tags first.
        text = Regex.Replace(text, "\\[[^\\]]+\\]", string.Empty);

        // Convert known icon resources into readable text.
        text = Regex.Replace(
            text,
            @"res://images/packed/sprite_fonts/[a-z_]*energy_icon\.png",
            "1点能量",
            RegexOptions.IgnoreCase);

        // Remove any leftover resource paths.
        text = Regex.Replace(text, @"res://\S+", string.Empty, RegexOptions.IgnoreCase);

        text = Regex.Replace(text, "[\\t ]+", " ");
        text = Regex.Replace(text, "\\n{3,}", "\\n\\n");
        text = Regex.Replace(text, "\\s+([，。！？；：,.;!?])", "$1");
        return text.Trim();
    }
}






