using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.Core.Runs;

namespace StartHandPickerMod;

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

        ConfigStore.Load();

        _harmony = new Harmony("starthandpickermod.harmony");
        _harmony.PatchAll();

        Log.Info("StartHandPickerMod initialized.");
    }
}

public sealed class StartHandConfig
{
    public bool Enabled { get; set; } = true;
    public List<CardPick> Picks { get; set; } = new();
}

public sealed class CardPick
{
    public string Key { get; set; } = string.Empty;
    public int Count { get; set; } = 1;
}

internal static class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static StartHandConfig _config = new();

    public static StartHandConfig Current => _config;

    public static string ConfigPath
    {
        get
        {
            var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrWhiteSpace(dir))
            {
                dir = AppContext.BaseDirectory;
            }

            return Path.Combine(dir, "start_hand_picker_config.json");
        }
    }

    public static void Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                _config = CreateDefault();
                Save();
                return;
            }

            var raw = File.ReadAllText(ConfigPath, Encoding.UTF8);
            _config = JsonSerializer.Deserialize<StartHandConfig>(raw, JsonOptions) ?? CreateDefault();
            NormalizeInPlace(_config);
        }
        catch (Exception ex)
        {
            _config = CreateDefault();
            Log.Warn($"Failed to load config, using defaults: {ex.Message}");
        }
    }

    public static void Save()
    {
        try
        {
            NormalizeInPlace(_config);
            var json = JsonSerializer.Serialize(_config, JsonOptions);
            File.WriteAllText(ConfigPath, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to save config: {ex.Message}");
        }
    }

    public static void Set(StartHandConfig config)
    {
        _config = config ?? CreateDefault();
        NormalizeInPlace(_config);
        Save();
    }

    private static StartHandConfig CreateDefault()
    {
        return new StartHandConfig
        {
            Enabled = true,
            Picks = new List<CardPick>
            {
                new() { Key = "StrikeIronclad", Count = 3 },
                new() { Key = "DefendIronclad", Count = 2 }
            }
        };
    }

    private static void NormalizeInPlace(StartHandConfig config)
    {
        config.Picks ??= new List<CardPick>();

        foreach (var pick in config.Picks)
        {
            pick.Key = pick.Key?.Trim() ?? string.Empty;
            if (pick.Count < 1)
            {
                pick.Count = 1;
            }

            if (pick.Count > 99)
            {
                pick.Count = 99;
            }
        }

        config.Picks = config.Picks.Where(p => !string.IsNullOrWhiteSpace(p.Key)).ToList();
    }
}

internal static class RuntimeState
{
    private static bool _pendingForCurrentRun;

    public static void MarkRunStarted(string source)
    {
        _pendingForCurrentRun = ConfigStore.Current.Enabled && ConfigStore.Current.Picks.Count > 0;
        Log.Info($"Run started via {source}; pending start-hand apply = {_pendingForCurrentRun}");
    }

    public static bool HasPending()
    {
        return _pendingForCurrentRun;
    }

    public static void ClearPending(string reason)
    {
        if (_pendingForCurrentRun)
        {
            Log.Info($"Clearing pending start hand. reason={reason}");
        }

        _pendingForCurrentRun = false;
    }
}

internal static class CardResolver
{
    private static readonly Dictionary<string, CardModel> ByKey = new(StringComparer.OrdinalIgnoreCase);
    private static bool _initialized;

    public static IEnumerable<(string DisplayText, string Key)> BuildCatalog()
    {
        EnsureInitialized();

        var cards = ModelDb.AllCards
            .Where(c => c != null)
            .Select(c => new
            {
                Card = c,
                Display = $"{c.Title} ({c.Id.Entry})",
                Key = c.Id.Entry
            })
            .OrderBy(c => c.Display, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return cards.Select(c => (c.Display, c.Key));
    }

    public static bool TryResolve(string rawKey, out CardModel card)
    {
        EnsureInitialized();

        var key = Normalize(rawKey);
        if (ByKey.TryGetValue(key, out card!))
        {
            return true;
        }

        card = null!;
        return false;
    }

    private static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        try
        {
            ByKey.Clear();

            foreach (var card in ModelDb.AllCards)
            {
                AddKey(card.Id.Entry, card);
                AddKey($"{card.Id.Category}.{card.Id.Entry}", card);
                AddKey(card.Title, card);
            }

            _initialized = true;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to initialize card resolver: {ex.Message}");
        }
    }

    private static void AddKey(string key, CardModel card)
    {
        var normalized = Normalize(key);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        ByKey.TryAdd(normalized, card);
    }

    private static string Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var noSpaces = new string(raw.Where(c => !char.IsWhiteSpace(c)).ToArray());
        return noSpaces.ToUpperInvariant();
    }
}

internal static class StartHandApplier
{
    public static bool TryApplyToRun(RunState? state, string source)
    {
        if (!RuntimeState.HasPending())
        {
            return false;
        }

        if (!ConfigStore.Current.Enabled || ConfigStore.Current.Picks.Count == 0)
        {
            RuntimeState.ClearPending($"{source}: disabled or empty config");
            return false;
        }

        if (state == null || state.Players.Count == 0)
        {
            Log.Warn($"{source}: run state unavailable or has no players.");
            return false;
        }

        var resolved = new List<(CardModel Canonical, int Count)>();
        var unresolved = new List<string>();

        foreach (var pick in ConfigStore.Current.Picks)
        {
            if (!CardResolver.TryResolve(pick.Key, out var canonical))
            {
                unresolved.Add(pick.Key);
                continue;
            }

            resolved.Add((canonical, Math.Clamp(pick.Count, 1, 99)));
        }

        if (resolved.Count == 0)
        {
            RuntimeState.ClearPending($"{source}: no valid cards to apply");
            Log.Warn("No valid cards found in config. Deck was not changed.");
            if (unresolved.Count > 0)
            {
                Log.Warn($"Unresolved keys: {string.Join(", ", unresolved)}");
            }
            return false;
        }

        var totalAddedAllPlayers = 0;

        foreach (var player in state.Players)
        {
            totalAddedAllPlayers += RebuildPlayerDeck(state, player, resolved);
        }

        RuntimeState.ClearPending($"{source}: deck rebuild completed");

        Log.Info($"Applied start deck after run start via {source}. totalAdded={totalAddedAllPlayers}, players={state.Players.Count}");
        if (unresolved.Count > 0)
        {
            Log.Warn($"Some configured keys were not found: {string.Join(", ", unresolved)}");
        }

        return true;
    }

    private static int RebuildPlayerDeck(RunState state, Player player, IReadOnlyList<(CardModel Canonical, int Count)> resolved)
    {
        var oldCards = player.Deck.Cards.ToList();
        Log.Info($"Rebuilding deck for player netId={player.NetId}. oldCount={oldCards.Count}");

        foreach (var oldCard in oldCards)
        {
            try
            {
                oldCard.RemoveFromState();
                if (state.ContainsCard(oldCard))
                {
                    state.RemoveCard(oldCard);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed removing old deck card {oldCard.Id.Entry}: {ex.Message}");
            }
        }

        player.Deck.Clear(true);

        var added = 0;
        foreach (var entry in resolved)
        {
            for (var i = 0; i < entry.Count; i++)
            {
                var card = state.CreateCard(entry.Canonical, player);
                card.FloorAddedToDeck = 1;
                player.Deck.AddInternal(card, -1, true);
                added++;
            }
        }

        player.Deck.InvokeContentsChanged();
        Log.Info($"Deck rebuild finished for player netId={player.NetId}. newCount={added}");
        return added;
    }
}

internal static class MainMenuUi
{
    private const string OpenButtonName = "StartHandPickerOpenButton";
    private const string PanelName = "StartHandPickerPanel";

    private static TextEdit? _entryTextEdit;
    private static OptionButton? _cardSelector;
    private static SpinBox? _countSpin;
    private static CheckBox? _enabledToggle;
    private static Label? _statusLabel;

    public static void Attach(NMainMenu mainMenu)
    {
        DeckPickerPanel.Attach(mainMenu);
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
        panel.AnchorLeft = 0.2f;
        panel.AnchorTop = 0.12f;
        panel.AnchorRight = 0.8f;
        panel.AnchorBottom = 0.9f;

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 18);
        margin.AddThemeConstantOverride("margin_right", 18);
        margin.AddThemeConstantOverride("margin_top", 18);
        margin.AddThemeConstantOverride("margin_bottom", 18);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 10);

        var title = new Label
        {
            Text = "开局牌库配置（每行: 卡牌ID,数量）",
            HorizontalAlignment = HorizontalAlignment.Left
        };

        var help = new Label
        {
            Text = "新开局创建后会重建牌库。你可以从下拉框添加，也可以手输。示例: StrikeIronclad,3",
            AutowrapMode = TextServer.AutowrapMode.Word
        };

        _enabledToggle = new CheckBox { Text = "启用本MOD（新开局生效）", ButtonPressed = ConfigStore.Current.Enabled };

        var pickerRow = new HBoxContainer();
        pickerRow.AddThemeConstantOverride("separation", 8);

        _cardSelector = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _countSpin = new SpinBox
        {
            MinValue = 1,
            MaxValue = 99,
            Step = 1,
            Value = 1,
            CustomMinimumSize = new Vector2(90, 0)
        };

        var addButton = new Button { Text = "添加到列表" };
        addButton.Pressed += AddSelectedCardToText;

        pickerRow.AddChild(_cardSelector);
        pickerRow.AddChild(_countSpin);
        pickerRow.AddChild(addButton);

        _entryTextEdit = new TextEdit
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 260)
        };

        var bottomRow = new HBoxContainer();
        bottomRow.AddThemeConstantOverride("separation", 8);

        var applyButton = new Button { Text = "保存" };
        applyButton.Pressed += () => SaveFromUi(mainMenu);

        var closeButton = new Button { Text = "关闭" };
        closeButton.Pressed += () => TogglePanel(mainMenu, false);

        _statusLabel = new Label
        {
            Text = string.Empty,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.Word
        };

        bottomRow.AddChild(applyButton);
        bottomRow.AddChild(closeButton);
        bottomRow.AddChild(_statusLabel);

        root.AddChild(title);
        root.AddChild(help);
        root.AddChild(_enabledToggle);
        root.AddChild(pickerRow);
        root.AddChild(_entryTextEdit);
        root.AddChild(bottomRow);

        margin.AddChild(root);
        panel.AddChild(margin);
        mainMenu.AddChild(panel);

        RebuildCardSelector();
        RefreshEditorFromConfig();
    }

    private static void RebuildCardSelector()
    {
        if (_cardSelector == null)
        {
            return;
        }

        _cardSelector.Clear();

        try
        {
            foreach (var item in CardResolver.BuildCatalog())
            {
                var index = _cardSelector.ItemCount;
                _cardSelector.AddItem(item.DisplayText);
                _cardSelector.SetItemMetadata(index, item.Key);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to build card list for UI: {ex.Message}");
        }
    }

    private static void RefreshEditorFromConfig()
    {
        if (_entryTextEdit == null)
        {
            return;
        }

        _enabledToggle!.ButtonPressed = ConfigStore.Current.Enabled;
        _entryTextEdit.Text = ToEditorText(ConfigStore.Current.Picks);
        _statusLabel!.Text = $"配置文件: {ConfigStore.ConfigPath}\n日志文件: {Log.FilePath}";
    }

    private static void AddSelectedCardToText()
    {
        if (_entryTextEdit == null || _cardSelector == null || _countSpin == null)
        {
            return;
        }

        if (_cardSelector.ItemCount == 0)
        {
            return;
        }

        var idx = _cardSelector.Selected;
        if (idx < 0)
        {
            idx = 0;
        }

        var key = _cardSelector.GetItemMetadata(idx).AsString();
        var count = (int)Math.Clamp(_countSpin.Value, 1, 99);

        var picks = ParsePicks(_entryTextEdit.Text, mergeDuplicates: true);
        var normalized = NormalizePickKey(key);
        var existing = picks.FirstOrDefault(p => NormalizePickKey(p.Key) == normalized);

        if (existing == null)
        {
            picks.Add(new CardPick { Key = key, Count = count });
        }
        else
        {
            existing.Count = Math.Clamp(existing.Count + count, 1, 99);
        }

        _entryTextEdit.Text = ToEditorText(picks);
        if (_statusLabel != null)
        {
            _statusLabel.Text = $"已添加 {key} x{count}（重复项会自动合并）";
        }
    }

    private static void SaveFromUi(NMainMenu mainMenu)
    {
        if (_entryTextEdit == null || _enabledToggle == null || _statusLabel == null)
        {
            return;
        }

        var config = new StartHandConfig
        {
            Enabled = _enabledToggle.ButtonPressed,
            Picks = ParsePicks(_entryTextEdit.Text, mergeDuplicates: true)
        };

        ConfigStore.Set(config);
        _entryTextEdit.Text = ToEditorText(config.Picks);
        _statusLabel.Text = $"已保存。下次开局将使用 {config.Picks.Count} 条配置。\n日志文件: {Log.FilePath}";

        TogglePanel(mainMenu, false);
    }

    private static List<CardPick> ParsePicks(string raw, bool mergeDuplicates = false)
    {
        var output = new List<CardPick>();

        if (string.IsNullOrWhiteSpace(raw))
        {
            return output;
        }

        var lines = raw.Replace("\r", string.Empty).Split('\n');
        foreach (var lineRaw in lines)
        {
            var line = lineRaw.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = line.Split(',', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var key = parts.Length > 0 ? parts[0].Trim() : string.Empty;
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var count = 1;
            if (parts.Length > 1 && int.TryParse(parts[1], out var parsed))
            {
                count = Math.Clamp(parsed, 1, 99);
            }

            output.Add(new CardPick { Key = key, Count = count });
        }

        return mergeDuplicates ? MergeDuplicatePicks(output) : output;
    }

    private static List<CardPick> MergeDuplicatePicks(IEnumerable<CardPick> picks)
    {
        var output = new List<CardPick>();
        var indexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var pick in picks)
        {
            var key = pick.Key?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var normalized = NormalizePickKey(key);
            var count = Math.Clamp(pick.Count, 1, 99);

            if (indexByKey.TryGetValue(normalized, out var idx))
            {
                output[idx].Count = Math.Clamp(output[idx].Count + count, 1, 99);
            }
            else
            {
                indexByKey[normalized] = output.Count;
                output.Add(new CardPick { Key = key, Count = count });
            }
        }

        return output;
    }

    private static string NormalizePickKey(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        return new string(raw.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
    }

    private static string ToEditorText(IEnumerable<CardPick> picks)
    {
        return string.Join('\n', picks.Select(p => $"{p.Key},{Math.Clamp(p.Count, 1, 99)}"));
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
            RefreshEditorFromConfig();
        }

        panel.Visible = visible;
    }
}

[HarmonyPatch(typeof(NPauseMenu), "_Ready")]
internal static class PauseMenuPatch
{
    private const string PauseMenuEntryName = "StartHandPickerPauseMenuEntry";

    public static void Postfix(NPauseMenu __instance)
    {
        try
        {
            DeckPickerPanel.Attach(__instance, includeOpenButton: false);
            AttachPauseMenuEntry(__instance);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to attach in-run StartHandPicker UI: {ex.Message}");
        }
    }

    private static void AttachPauseMenuEntry(NPauseMenu pauseMenu)
    {
        var buttonContainer = pauseMenu.GetNodeOrNull<Control>("%ButtonContainer");
        if (buttonContainer == null)
        {
            return;
        }

        if (buttonContainer.GetNodeOrNull<Button>(PauseMenuEntryName) != null)
        {
            return;
        }

        var openButton = new Button
        {
            Name = PauseMenuEntryName,
            Text = "开局牌库配置",
            FocusMode = Control.FocusModeEnum.All,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 46)
        };

        openButton.Pressed += () => DeckPickerPanel.Open(pauseMenu);
        buttonContainer.AddChild(openButton);
    }
}

internal static class RunStartHooks
{
    public static void Mark(string source)
    {
        RuntimeState.MarkRunStarted(source);
    }
}

[HarmonyPatch(typeof(RunManager), "InitializeNewRun")]
internal static class RunManagerInitializeNewRunPatch
{
    public static void Postfix()
    {
        RunStartHooks.Mark("InitializeNewRun");
    }
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.SetUpNewSinglePlayer))]
internal static class RunManagerSetUpNewSinglePlayerPatch
{
    public static void Postfix(RunState state)
    {
        RunStartHooks.Mark(nameof(RunManager.SetUpNewSinglePlayer));
        StartHandApplier.TryApplyToRun(state, nameof(RunManager.SetUpNewSinglePlayer));
    }
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.SetUpNewMultiPlayer))]
internal static class RunManagerSetUpNewMultiPlayerPatch
{
    public static void Postfix(RunState state)
    {
        RunStartHooks.Mark(nameof(RunManager.SetUpNewMultiPlayer));
        StartHandApplier.TryApplyToRun(state, nameof(RunManager.SetUpNewMultiPlayer));
    }
}

[HarmonyPatch(typeof(RunManager), "InitializeSavedRun")]
internal static class RunManagerInitializeSavedRunPatch
{
    public static void Postfix()
    {
        RuntimeState.ClearPending("InitializeSavedRun");
    }
}

internal static class Log
{
    private static readonly object Sync = new();
    private static string? _filePath;

    public static string FilePath
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_filePath))
            {
                return _filePath;
            }

            var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrWhiteSpace(dir))
            {
                dir = AppContext.BaseDirectory;
            }

            var logDir = Path.Combine(dir, "logs");
            _filePath = Path.Combine(logDir, "start_hand_picker.log");
            return _filePath;
        }
    }

    public static void Info(string message) => Write("INFO", message, isError: false);

    public static void Warn(string message) => Write("WARN", message, isError: false);

    public static void Error(string message) => Write("ERROR", message, isError: true);

    private static void Write(string level, string message, bool isError)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";

        if (isError)
        {
            GD.PrintErr($"[StartHandPickerMod][{level}] {message}");
        }
        else
        {
            GD.Print($"[StartHandPickerMod][{level}] {message}");
        }

        try
        {
            var folder = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                Directory.CreateDirectory(folder);
            }

            lock (Sync)
            {
                File.AppendAllText(FilePath, line + System.Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Ignore file logging failures to keep gameplay unaffected.
        }
    }
}



















