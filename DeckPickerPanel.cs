using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace StartHandPickerMod;

internal static class DeckPickerPanel
{
    private const string OpenButtonName = "StartDeckPickerOpenButton";
    private const string PanelName = "StartDeckPickerPanel";
    private const int MaxRenderedCards = 160;

    private static CheckBox? _enabledToggle;
    private static LineEdit? _searchEdit;
    private static OptionButton? _characterFilter;
    private static OptionButton? _rarityFilter;
    private static OptionButton? _typeFilter;
    private static Label? _resultLabel;
    private static GridContainer? _cardGrid;
    private static Label? _selectedCardLabel;
    private static Label? _selectedCountLabel;
    private static Button? _minusButton;
    private static Button? _plusButton;
    private static Label? _statusLabel;

    private static readonly List<CatalogCard> Catalog = new();
    private static readonly Dictionary<string, CatalogCard> CatalogByKey = new(StringComparer.OrdinalIgnoreCase);
    private static bool _catalogInitialized;

    private static readonly Dictionary<string, int> PickCounts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, CardCell> Cells = new(StringComparer.OrdinalIgnoreCase);

    private static string? _selectedKey;

    private sealed class CatalogCard
    {
        public CatalogCard(CardModel card)
        {
            Card = card;
            Key = card.Id.Entry;
            Title = card.Title;
            SearchText = $"{Title} {Key}".ToUpperInvariant();
            PoolTag = ResolvePoolTag(card);
        }

        public CardModel Card { get; }
        public string Key { get; }
        public string Title { get; }
        public string SearchText { get; }
        public string PoolTag { get; }
        public CardRarity Rarity => Card.Rarity;
        public CardType Type => Card.Type;
        public bool IsColorless => Card.Pool is ColorlessCardPool;

        private static string ResolvePoolTag(CardModel card)
        {
            if (card.Pool is IroncladCardPool)
            {
                return "ironclad";
            }

            if (card.Pool is SilentCardPool)
            {
                return "silent";
            }

            if (card.Pool is DefectCardPool)
            {
                return "defect";
            }

            if (card.Pool is RegentCardPool)
            {
                return "regent";
            }

            if (card.Pool is NecrobinderCardPool)
            {
                return "necrobinder";
            }

            if (card.Pool is ColorlessCardPool)
            {
                return "colorless";
            }

            var poolName = card.Pool?.GetType().Name ?? string.Empty;
            if (poolName.Contains("Ancient", StringComparison.OrdinalIgnoreCase) || card.Rarity == CardRarity.Ancient)
            {
                return "ancient";
            }

            return "other";
        }
    }

    private sealed class CardCell
    {
        public Button Button { get; init; } = null!;
        public Label CountLabel { get; init; } = null!;
        public Panel SelectedOutline { get; init; } = null!;
    }

    public static void Attach(NMainMenu mainMenu)
    {
        AttachOpenButton(mainMenu);
        AttachPanel(mainMenu);
    }

    private static void AttachOpenButton(NMainMenu mainMenu)
    {
        if (mainMenu.GetNodeOrNull<Button>(OpenButtonName) != null)
        {
            return;
        }

        var button = new Button
        {
            Name = OpenButtonName,
            Text = "开局牌库",
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(140, 38)
        };

        button.AnchorLeft = 0;
        button.AnchorTop = 0;
        button.AnchorRight = 0;
        button.AnchorBottom = 0;
        button.OffsetLeft = 20;
        button.OffsetTop = 20;
        button.OffsetRight = 160;
        button.OffsetBottom = 58;

        button.Pressed += () => TogglePanel(mainMenu, true);
        mainMenu.AddChild(button);
    }

    private static void AttachPanel(NMainMenu mainMenu)
    {
        if (mainMenu.GetNodeOrNull<PanelContainer>(PanelName) != null)
        {
            return;
        }

        var panel = new PanelContainer { Name = PanelName, Visible = false };
        panel.AnchorLeft = 0.04f;
        panel.AnchorTop = 0.06f;
        panel.AnchorRight = 0.96f;
        panel.AnchorBottom = 0.95f;

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 16);
        margin.AddThemeConstantOverride("margin_right", 16);
        margin.AddThemeConstantOverride("margin_top", 16);
        margin.AddThemeConstantOverride("margin_bottom", 16);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 8);

        var title = new Label
        {
            Text = "开局牌库配置（卡牌网格选择）",
            HorizontalAlignment = HorizontalAlignment.Left
        };

        var help = new Label
        {
            Text = "点击卡牌后在下方用 - 1 + 调整数量。筛选支持角色、稀有度、类型；保存后新开局会重建牌库。",
            AutowrapMode = TextServer.AutowrapMode.Word
        };

        _enabledToggle = new CheckBox
        {
            Text = "启用本 MOD（新开局生效）",
            ButtonPressed = ConfigStore.Current.Enabled
        };

        var filterRow = new HBoxContainer();
        filterRow.AddThemeConstantOverride("separation", 8);

        _searchEdit = new LineEdit
        {
            PlaceholderText = "搜索牌名或ID（例如 StrikeIronclad）",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        _searchEdit.TextChanged += _ => RebuildCardGrid();

        _characterFilter = new OptionButton { CustomMinimumSize = new Vector2(160, 0) };
        _characterFilter.AddItem("角色: 全部");
        _characterFilter.AddItem("角色: Ironclad");
        _characterFilter.AddItem("角色: Silent");
        _characterFilter.AddItem("角色: Defect");
        _characterFilter.AddItem("角色: Regent");
        _characterFilter.AddItem("角色: Necrobinder");
        _characterFilter.AddItem("角色: 无色");
        _characterFilter.AddItem("角色: 远古");
        _characterFilter.AddItem("角色: 其他");
        _characterFilter.ItemSelected += _ => RebuildCardGrid();

        _rarityFilter = new OptionButton { CustomMinimumSize = new Vector2(160, 0) };
        _rarityFilter.AddItem("稀有度: 全部");
        _rarityFilter.AddItem("稀有度: 白卡");
        _rarityFilter.AddItem("稀有度: 蓝卡");
        _rarityFilter.AddItem("稀有度: 金卡");
        _rarityFilter.AddItem("稀有度: 其他");
        _rarityFilter.AddItem("稀有度: 无色");
        _rarityFilter.ItemSelected += _ => RebuildCardGrid();

        _typeFilter = new OptionButton { CustomMinimumSize = new Vector2(150, 0) };
        _typeFilter.AddItem("类型: 全部");
        _typeFilter.AddItem("类型: 攻击");
        _typeFilter.AddItem("类型: 技能");
        _typeFilter.AddItem("类型: 能力");
        _typeFilter.AddItem("类型: 其他");
        _typeFilter.ItemSelected += _ => RebuildCardGrid();

        filterRow.AddChild(_searchEdit);
        filterRow.AddChild(_characterFilter);
        filterRow.AddChild(_rarityFilter);
        filterRow.AddChild(_typeFilter);

        _resultLabel = new Label
        {
            Text = string.Empty,
            AutowrapMode = TextServer.AutowrapMode.Word
        };

        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 420)
        };

        _cardGrid = new GridContainer
        {
            Columns = 4,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        _cardGrid.AddThemeConstantOverride("h_separation", 18);
        _cardGrid.AddThemeConstantOverride("v_separation", 18);
        scroll.AddChild(_cardGrid);

        var selectedRow = new HBoxContainer();
        selectedRow.AddThemeConstantOverride("separation", 8);

        _selectedCardLabel = new Label
        {
            Text = "未选择卡牌",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.Word
        };

        _minusButton = new Button
        {
            Text = "-",
            CustomMinimumSize = new Vector2(44, 32),
            FocusMode = Control.FocusModeEnum.None
        };

        _selectedCountLabel = new Label
        {
            Text = "0",
            CustomMinimumSize = new Vector2(54, 32),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        _plusButton = new Button
        {
            Text = "+",
            CustomMinimumSize = new Vector2(44, 32),
            FocusMode = Control.FocusModeEnum.None
        };

        _minusButton.Pressed += () => ChangeSelectedCount(-1);
        _plusButton.Pressed += () => ChangeSelectedCount(1);

        selectedRow.AddChild(_selectedCardLabel);
        selectedRow.AddChild(_minusButton);
        selectedRow.AddChild(_selectedCountLabel);
        selectedRow.AddChild(_plusButton);

        var bottomRow = new HBoxContainer();
        bottomRow.AddThemeConstantOverride("separation", 8);

        var saveButton = new Button { Text = "保存" };
        saveButton.Pressed += SaveFromUi;

        var closeButton = new Button { Text = "关闭" };
        closeButton.Pressed += () => TogglePanel(mainMenu, false);

        _statusLabel = new Label
        {
            Text = string.Empty,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.Word
        };

        bottomRow.AddChild(saveButton);
        bottomRow.AddChild(closeButton);
        bottomRow.AddChild(_statusLabel);

        root.AddChild(title);
        root.AddChild(help);
        root.AddChild(_enabledToggle);
        root.AddChild(filterRow);
        root.AddChild(_resultLabel);
        root.AddChild(scroll);
        root.AddChild(selectedRow);
        root.AddChild(bottomRow);

        margin.AddChild(root);
        panel.AddChild(margin);
        mainMenu.AddChild(panel);

        panel.Resized += () => UpdateGridColumns(panel);

        EnsureCatalog();
        LoadFromConfig();
        RebuildCardGrid();
        UpdateGridColumns(panel);
    }

    private static void UpdateGridColumns(Control panel)
    {
        if (_cardGrid == null)
        {
            return;
        }

        var cols = Math.Clamp((int)(panel.Size.X / 250f), 2, 7);
        _cardGrid.Columns = cols;
    }

    private static void EnsureCatalog()
    {
        if (_catalogInitialized)
        {
            return;
        }

        Catalog.Clear();
        CatalogByKey.Clear();

        foreach (var card in ModelDb.AllCards)
        {
            if (card == null)
            {
                continue;
            }

            var key = card.Id.Entry;
            if (CatalogByKey.ContainsKey(key))
            {
                continue;
            }

            var entry = new CatalogCard(card);
            Catalog.Add(entry);
            CatalogByKey[key] = entry;
        }

        Catalog.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
        _catalogInitialized = true;
    }

    private static void LoadFromConfig()
    {
        PickCounts.Clear();

        var unresolved = new List<string>();
        foreach (var pick in ConfigStore.Current.Picks)
        {
            if (!CardResolver.TryResolve(pick.Key, out var canonical))
            {
                unresolved.Add(pick.Key);
                continue;
            }

            var key = canonical.Id.Entry;
            var add = Math.Clamp(pick.Count, 1, 99);
            PickCounts[key] = Math.Clamp((PickCounts.TryGetValue(key, out var oldCount) ? oldCount : 0) + add, 1, 99);
        }

        _enabledToggle!.ButtonPressed = ConfigStore.Current.Enabled;
        _selectedKey = PickCounts.Keys.FirstOrDefault();

        if (_statusLabel != null)
        {
            _statusLabel.Text = unresolved.Count == 0
                ? $"配置文件: {ConfigStore.ConfigPath}\n日志文件: {Log.FilePath}"
                : $"以下配置项无法解析，已忽略: {string.Join(", ", unresolved)}\n日志文件: {Log.FilePath}";
        }
    }

    private static void RebuildCardGrid()
    {
        if (_cardGrid == null)
        {
            return;
        }

        EnsureCatalog();

        foreach (var child in _cardGrid.GetChildren())
        {
            child.QueueFree();
        }

        Cells.Clear();

        var matched = Catalog.Where(PassesCurrentFilters).ToList();
        var visible = matched.Take(MaxRenderedCards).ToList();

        foreach (var entry in visible)
        {
            _cardGrid.AddChild(CreateCardCell(entry));
        }

        if (_selectedKey == null || !Cells.ContainsKey(_selectedKey))
        {
            _selectedKey = visible.FirstOrDefault()?.Key;
        }

        if (_resultLabel != null)
        {
            _resultLabel.Text = matched.Count > MaxRenderedCards
                ? $"匹配到 {matched.Count} 张卡牌，当前仅渲染前 {MaxRenderedCards} 张，请继续筛选或搜索。"
                : $"匹配到 {matched.Count} 张卡牌。";
        }

        UpdateCellStates();
        UpdateSelectedRow();
    }

    private static bool PassesCurrentFilters(CatalogCard entry)
    {
        if (_searchEdit != null)
        {
            var search = _searchEdit.Text?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(search) && entry.SearchText.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }
        }

        var characterIdx = _characterFilter?.Selected ?? 0;
        if (!PassCharacterFilter(entry, characterIdx))
        {
            return false;
        }

        var rarityIdx = _rarityFilter?.Selected ?? 0;
        if (!PassRarityFilter(entry, rarityIdx))
        {
            return false;
        }

        var typeIdx = _typeFilter?.Selected ?? 0;
        return PassTypeFilter(entry, typeIdx);
    }

    private static bool PassCharacterFilter(CatalogCard entry, int idx)
    {
        return idx switch
        {
            0 => true,
            1 => entry.PoolTag == "ironclad",
            2 => entry.PoolTag == "silent",
            3 => entry.PoolTag == "defect",
            4 => entry.PoolTag == "regent",
            5 => entry.PoolTag == "necrobinder",
            6 => entry.PoolTag == "colorless",
            7 => entry.PoolTag == "ancient",
            8 => entry.PoolTag == "other",
            _ => true
        };
    }

    private static bool PassRarityFilter(CatalogCard entry, int idx)
    {
        return idx switch
        {
            0 => true,
            1 => entry.Rarity == CardRarity.Common,
            2 => entry.Rarity == CardRarity.Uncommon,
            3 => entry.Rarity == CardRarity.Rare,
            4 => entry.Rarity != CardRarity.Common && entry.Rarity != CardRarity.Uncommon && entry.Rarity != CardRarity.Rare,
            5 => entry.IsColorless,
            _ => true
        };
    }

    private static bool PassTypeFilter(CatalogCard entry, int idx)
    {
        return idx switch
        {
            0 => true,
            1 => entry.Type == CardType.Attack,
            2 => entry.Type == CardType.Skill,
            3 => entry.Type == CardType.Power,
            4 => entry.Type != CardType.Attack && entry.Type != CardType.Skill && entry.Type != CardType.Power,
            _ => true
        };
    }

    private static Control CreateCardCell(CatalogCard entry)
    {
        var cell = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(248, 360),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        cell.AddThemeConstantOverride("separation", 6);

        var button = new Button
        {
            Flat = true,
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(236, 316),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClipContents = true,
            Text = string.Empty
        };

        var buttonStyle = new StyleBoxEmpty();
        button.AddThemeStyleboxOverride("normal", buttonStyle);
        button.AddThemeStyleboxOverride("hover", buttonStyle);
        button.AddThemeStyleboxOverride("pressed", buttonStyle);
        button.AddThemeStyleboxOverride("focus", buttonStyle);

        var center = new CenterContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            AnchorLeft = 0,
            AnchorTop = 0,
            AnchorRight = 1,
            AnchorBottom = 1,
            OffsetLeft = 0,
            OffsetTop = 0,
            OffsetRight = 0,
            OffsetBottom = 0
        };

        center.AddChild(CreateCardVisual(entry.Card));
        button.AddChild(center);

        var outline = new Panel
        {
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            AnchorLeft = 0,
            AnchorTop = 0,
            AnchorRight = 1,
            AnchorBottom = 1,
            OffsetLeft = 0,
            OffsetTop = 0,
            OffsetRight = 0,
            OffsetBottom = 0
        };

        var style = new StyleBoxFlat
        {
            BgColor = new Color(0f, 0f, 0f, 0f),
            BorderColor = new Color(1f, 0.82f, 0.24f, 1f),
            BorderWidthLeft = 3,
            BorderWidthTop = 3,
            BorderWidthRight = 3,
            BorderWidthBottom = 3,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8
        };
        outline.AddThemeStyleboxOverride("panel", style);
        button.AddChild(outline);

        button.Pressed += () =>
        {
            _selectedKey = entry.Key;
            UpdateCellStates();
            UpdateSelectedRow();
        };

        var countLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.Off,
            Text = "未选择"
        };

        cell.AddChild(button);
        cell.AddChild(countLabel);

        Cells[entry.Key] = new CardCell
        {
            Button = button,
            CountLabel = countLabel,
            SelectedOutline = outline
        };

        return cell;
    }

    private static Control CreateCardVisual(CardModel canonical)
    {
        try
        {
            var cardNode = NCard.Create(canonical, ModelVisibility.Visible);
            if (cardNode != null)
            {
                cardNode.Ready += () =>
                {
                    try
                    {
                        cardNode.UpdateVisuals(PileType.None, CardPreviewMode.Normal);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"UpdateVisuals failed for {canonical.Id.Entry}: {ex.Message}");
                    }
                };

                cardNode.Scale = new Vector2(0.36f, 0.36f);
                SetMouseFilterRecursive(cardNode, Control.MouseFilterEnum.Ignore);
                return cardNode;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"CreateCardVisual failed for {canonical.Id.Entry}: {ex.Message}");
        }

        var fallback = new PanelContainer { CustomMinimumSize = new Vector2(200, 280) };
        var label = new Label
        {
            Text = $"{canonical.Title}\n({canonical.Id.Entry})",
            AutowrapMode = TextServer.AutowrapMode.Word,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        fallback.AddChild(label);
        return fallback;
    }

    private static void SetMouseFilterRecursive(Node node, Control.MouseFilterEnum mouseFilter)
    {
        if (node is Control control)
        {
            control.MouseFilter = mouseFilter;
        }

        foreach (var child in node.GetChildren())
        {
            SetMouseFilterRecursive(child, mouseFilter);
        }
    }

    private static void ChangeSelectedCount(int delta)
    {
        if (string.IsNullOrWhiteSpace(_selectedKey))
        {
            return;
        }

        var current = PickCounts.TryGetValue(_selectedKey, out var oldCount) ? oldCount : 0;
        var next = Math.Clamp(current + delta, 0, 99);

        if (next <= 0)
        {
            PickCounts.Remove(_selectedKey);
        }
        else
        {
            PickCounts[_selectedKey] = next;
        }

        UpdateCellStates();
        UpdateSelectedRow();
    }

    private static void UpdateCellStates()
    {
        foreach (var pair in Cells)
        {
            var key = pair.Key;
            var cell = pair.Value;
            var count = PickCounts.TryGetValue(key, out var value) ? value : 0;

            cell.CountLabel.Text = count > 0 ? $"已选 x{count}" : "未选择";
            cell.CountLabel.Modulate = count > 0 ? new Color(1f, 0.9f, 0.55f, 1f) : new Color(0.72f, 0.72f, 0.72f, 1f);
            cell.SelectedOutline.Visible = string.Equals(key, _selectedKey, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void UpdateSelectedRow()
    {
        if (_selectedCardLabel == null || _selectedCountLabel == null || _minusButton == null || _plusButton == null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_selectedKey) || !CatalogByKey.TryGetValue(_selectedKey, out var entry))
        {
            _selectedCardLabel.Text = "未选择卡牌";
            _selectedCountLabel.Text = "0";
            _minusButton.Disabled = true;
            _plusButton.Disabled = true;
            return;
        }

        var count = PickCounts.TryGetValue(_selectedKey, out var existing) ? existing : 0;

        _selectedCardLabel.Text = $"{entry.Title} ({entry.Key})";
        _selectedCountLabel.Text = count.ToString();
        _minusButton.Disabled = count <= 0;
        _plusButton.Disabled = count >= 99;
    }

    private static void SaveFromUi()
    {
        var picks = PickCounts
            .Where(p => p.Value > 0)
            .OrderBy(p => CatalogByKey.TryGetValue(p.Key, out var card) ? card.Title : p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => new CardPick { Key = p.Key, Count = p.Value })
            .ToList();

        ConfigStore.Set(new StartHandConfig
        {
            Enabled = _enabledToggle?.ButtonPressed ?? true,
            Picks = picks
        });

        if (_statusLabel != null)
        {
            var total = picks.Sum(p => p.Count);
            _statusLabel.Text = $"已保存：{picks.Count} 种卡牌，共 {total} 张。\n日志文件: {Log.FilePath}";
        }
    }

    private static void TogglePanel(NMainMenu mainMenu, bool visible)
    {
        var panel = mainMenu.GetNodeOrNull<PanelContainer>(PanelName);
        if (panel == null)
        {
            return;
        }

        if (visible)
        {
            LoadFromConfig();
            RebuildCardGrid();
        }

        panel.Visible = visible;
    }
}





