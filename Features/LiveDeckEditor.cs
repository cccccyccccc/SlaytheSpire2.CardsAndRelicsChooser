using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.RelicCollection;
using MegaCrit.Sts2.Core.Runs;

namespace CardsAndRelicsChooser;

internal static class LiveDeckEditor
{
    private static bool _isBusy;
    private static bool _awaitingCardLibraryPick;
    private static bool _awaitingRelicCollectionPick;
    private static bool _navigationUiForcedVisible;
    private static bool _skipNextLibraryClosedCleanup;
    private static bool _pendingRestoreAfterDeckView;
    private static RunState? _pendingSelectionState;
    private static ulong _pendingSelectionPlayerNetId;
    private static bool _isRelicMultiSelectActive;
    private static NChooseARelicSelection? _activeRelicMultiSelectScreen;
    private static readonly HashSet<ulong> _selectedRelicHolderIds = new();
    private static readonly Dictionary<ulong, RelicWheelScrollState> _relicWheelScrollByScreenId = new();
    private static readonly HashSet<ulong> _removeCardSelectionScreenIds = new();
    private static readonly HashSet<ulong> _enchantCardSelectionScreenIds = new();
    private const string RelicSelectionBannerText = "选择你要删除的遗物";
    private const string RelicWheelHintText = "滚轮滑动可左右查看";
    private const string RelicWheelHintNodeName = "StartHandPickerRelicWheelHint";
    private const string RelicBackButtonNodeName = "StartHandPickerRelicBackButton";

    public static void OpenAddCardSelector()
    {
        if (_isBusy)
        {
            Log.Warn("Editor is busy; ignoring add-card request.");
            return;
        }

        if (!TryGetLiveRunAndPlayer(out var state, out var player))
        {
            return;
        }

        _isBusy = true;
        BeginSelectionContext(state, player);
        _awaitingCardLibraryPick = true;
        _awaitingRelicCollectionPick = false;

        if (!TryOpenCardLibraryPicker(state))
        {
            ClearSelectionContext("open_card_library_failed");
        }
    }

    public static void OpenRemoveCardSelector()
    {
        if (_isBusy)
        {
            Log.Warn("Editor is busy; ignoring remove-card request.");
            return;
        }

        ClosePauseMenu();
        TaskHelper.RunSafely(RemoveCardFlow());
    }

    public static void OpenEnchantCardSelector()
    {
        if (_isBusy)
        {
            Log.Warn("Editor is busy; ignoring enchant-card request.");
            return;
        }

        ClosePauseMenu();
        TaskHelper.RunSafely(EnchantCardFlow());
    }

    public static void OpenAddRelicSelector()
    {
        if (_isBusy)
        {
            Log.Warn("Editor is busy; ignoring add-relic request.");
            return;
        }

        if (!TryGetLiveRunAndPlayer(out var state, out var player))
        {
            return;
        }

        _isBusy = true;
        BeginSelectionContext(state, player);
        _awaitingCardLibraryPick = false;
        _awaitingRelicCollectionPick = true;

        if (!TryOpenRelicCollectionPicker(state))
        {
            ClearSelectionContext("open_relic_collection_failed");
        }
    }

    public static void OpenRemoveRelicSelector()
    {
        if (_isBusy)
        {
            Log.Warn("Editor is busy; ignoring remove-relic request.");
            return;
        }

        ClosePauseMenu();
        TaskHelper.RunSafely(RemoveRelicFlow());
    }

    public static bool TryHandleCardLibrarySelection(NCardLibrary? library, NCardHolder? holder)
    {
        if (!_awaitingCardLibraryPick)
        {
            return false;
        }

        if (holder?.CardModel == null)
        {
            Log.Warn("Card library selection was empty while add-picker was active.");
            ClearSelectionContext("empty_card_selection");
            return true;
        }

        if (!TryResolvePendingSelectionContext(out var state, out var player))
        {
            Log.Warn("Card library add-picker lost run/player context.");
            ClearSelectionContext("missing_card_context");
            return true;
        }

        var addUpgraded = IsAddUpgradedEnabled(library);

        try
        {
            var selected = holder.CardModel;
            var created = state.CreateCard(selected, player);
            if (addUpgraded && !created.IsUpgraded)
            {
                created.UpgradeInternal();
            }

            created.FloorAddedToDeck = Math.Max(1, state.TotalFloor);
            player.Deck.AddInternal(created, -1, false);
            player.Deck.InvokeCardAddFinished();

            Log.Info($"Added card via card library: {selected.Id.Entry}, upgraded={addUpgraded}, player={player.NetId}, deckCount={player.Deck.Cards.Count}");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to add selected card from card library: {ex.Message}");
        }

        return true;
    }

    public static bool TryHandleRelicCollectionSelection(NRelicCollectionEntry? entry)
    {
        if (!_awaitingRelicCollectionPick)
        {
            return false;
        }

        if (entry?.relic == null)
        {
            Log.Warn("Relic collection selection was empty while add-picker was active.");
            return true;
        }

        if (!TryResolvePendingSelectionContext(out _, out var player))
        {
            Log.Warn("Relic collection add-picker lost run/player context.");
            ClearSelectionContext("missing_relic_context");
            return true;
        }

        var relic = entry.relic.CanonicalInstance;
        TaskHelper.RunSafely(AddRelicFromCollection(relic, player));
        return true;
    }

    public static void NotifyCardLibraryOpened()
    {
        if (!_awaitingCardLibraryPick)
        {
            return;
        }

        ShowNavigationUiForSelection();
    }

    public static void NotifyCardLibraryClosed()
    {
        if (!_awaitingCardLibraryPick)
        {
            return;
        }

        if (_skipNextLibraryClosedCleanup)
        {
            _skipNextLibraryClosedCleanup = false;
            Log.Info("Card library closed while switching to deck view; preserving add-picker context.");
            return;
        }

        _pendingRestoreAfterDeckView = false;
        Log.Info("Card library closed; exiting continuous add-card mode.");
        ClearSelectionContext("card_library_closed");
        CloseCapstonePickerIfPresent("card_library_closed_return_to_run");
    }

    public static void NotifyRelicCollectionOpened()
    {
        if (!_awaitingRelicCollectionPick)
        {
            return;
        }

        ShowNavigationUiForSelection();
    }

    public static void NotifyRelicCollectionClosed()
    {
        if (!_awaitingRelicCollectionPick)
        {
            return;
        }

        Log.Info("Relic collection closed; exiting continuous add-relic mode.");
        ClearSelectionContext("relic_collection_closed");
        CloseCapstonePickerIfPresent("relic_collection_closed_return_to_run");
    }

    public static void NotifyDeckViewOpenRequested()
    {
        if (!_awaitingCardLibraryPick)
        {
            return;
        }

        if (NCapstoneContainer.Instance?.CurrentCapstoneScreen is not NCapstoneSubmenuStack)
        {
            return;
        }

        _skipNextLibraryClosedCleanup = true;
        _pendingRestoreAfterDeckView = true;
        Log.Info("Deck view opened from add-picker; will restore add-picker on deck close.");
    }

    public static void NotifyDeckViewClosed()
    {
        if (!_pendingRestoreAfterDeckView)
        {
            return;
        }

        _pendingRestoreAfterDeckView = false;

        if (!_awaitingCardLibraryPick)
        {
            Log.Warn("Deck view closed but add-picker context is no longer active.");
            return;
        }

        if (!TryResolvePendingSelectionContext(out var state, out _))
        {
            Log.Warn("Deck view closed but add-picker context is invalid.");
            ClearSelectionContext("deck_view_closed_missing_context");
            return;
        }

        if (TryOpenCardLibraryPicker(state))
        {
            Log.Info("Restored add-picker after closing deck view.");
            return;
        }

        Log.Warn("Failed to restore add-picker after closing deck view.");
        ClearSelectionContext("deck_view_restore_failed");
    }

    private static async Task EnchantCardFlow()
    {
        if (!TryGetLiveRunAndPlayer(out _, out var player))
        {
            return;
        }

        _isBusy = true;
        try
        {
            await Task.Yield();

            while (true)
            {
                if (!TryGetLiveRunAndPlayer(out _, out player))
                {
                    Log.Warn("Run state became unavailable during enchant-card flow.");
                    return;
                }

                var deckCards = player.Deck.Cards.ToList();
                if (deckCards.Count == 0)
                {
                    Log.Warn("Deck is empty; enchant-card action skipped.");
                    return;
                }

                var prefs = new CardSelectorPrefs(CardSelectorPrefs.EnchantSelectionPrompt, 1, 1)
                {
                    Cancelable = true,
                    RequireManualConfirmation = true
                };

                NDeckCardSelectScreen? screen = null;
                CardModel? selectedCard;
                try
                {
                    screen = NDeckCardSelectScreen.Create(deckCards, prefs);
                    RegisterEnchantCardSelectionScreen(screen);

                    var overlayStack = NOverlayStack.Instance;
                    if (overlayStack == null)
                    {
                        Log.Warn("Overlay stack unavailable; enchant-card action skipped.");
                        return;
                    }

                    overlayStack.Push(screen);

                    selectedCard = (await screen.CardsSelected())
                        .Where(c => c != null)
                        .FirstOrDefault();
                }
                finally
                {
                    UnregisterEnchantCardSelectionScreen(screen);
                }

                if (selectedCard == null)
                {
                    Log.Info("Enchant-card canceled.");
                    return;
                }

                var availableEnchantments = GetAvailableEnchantments(selectedCard);
                if (availableEnchantments.Count == 0)
                {
                    Log.Warn($"No applicable enchantments for card {selectedCard.Id.Entry}; returning to card picker.");
                    continue;
                }

                var selectedEnchantment = await EnchantmentPickerOverlay.Show(selectedCard, availableEnchantments);
                if (selectedEnchantment == null)
                {
                    Log.Info("Enchant effect selection canceled; returning to card picker.");
                    continue;
                }

                try
                {
                    var applied = CardCmd.Enchant(selectedEnchantment.ToMutable(), selectedCard, 1m);
                    Log.Info($"Enchanted card: card={selectedCard.Id.Entry}, enchantment={(applied?.Id.Entry ?? selectedEnchantment.Id.Entry)}, player={player.NetId}");
                }
                catch (Exception enchantEx)
                {
                    Log.Warn($"Failed to enchant selected card {selectedCard.Id.Entry} with {selectedEnchantment.Id.Entry}: {enchantEx.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Enchant-card flow failed: {ex.Message}");
        }
        finally
        {
            EnchantmentPickerOverlay.CloseIfOpen();
            _isBusy = false;
        }
    }

    private static List<EnchantmentModel> GetAvailableEnchantments(CardModel card)
    {
        var candidates = new List<EnchantmentModel>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var enchantment in ModelDb.DebugEnchantments)
        {
            if (enchantment == null)
            {
                continue;
            }

            var type = enchantment.GetType();
            if (type.Namespace?.Contains(".Mocks", StringComparison.OrdinalIgnoreCase) == true)
            {
                continue;
            }

            if (string.Equals(type.Name, "DeprecatedEnchantment", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!enchantment.CanEnchant(card))
            {
                continue;
            }

            if (!seenIds.Add(enchantment.Id.Entry))
            {
                continue;
            }

            candidates.Add(enchantment);
        }

        candidates.Sort(static (a, b) => string.Compare(GetEnchantmentDisplayName(a), GetEnchantmentDisplayName(b), StringComparison.OrdinalIgnoreCase));
        return candidates;
    }

    private static string GetEnchantmentDisplayName(EnchantmentModel enchantment)
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

    private static async Task RemoveCardFlow()
    {
        if (!TryGetLiveRunAndPlayer(out var state, out var player))
        {
            return;
        }

        _isBusy = true;
        NDeckCardSelectScreen? screen = null;
        try
        {
            await Task.Yield();

            var deckCards = player.Deck.Cards.ToList();
            if (deckCards.Count == 0)
            {
                Log.Warn("Deck is empty; remove-card action skipped.");
                return;
            }

            var prefs = new CardSelectorPrefs(CardSelectorPrefs.RemoveSelectionPrompt, 1, deckCards.Count)
            {
                Cancelable = true,
                RequireManualConfirmation = true
            };

            screen = NDeckCardSelectScreen.Create(deckCards, prefs);
            RegisterRemoveCardSelectionScreen(screen);
            var overlayStack = NOverlayStack.Instance;
            if (overlayStack == null)
            {
                Log.Warn("Overlay stack unavailable; remove-card action skipped.");
                return;
            }

            overlayStack.Push(screen);

            var selectedCards = (await screen.CardsSelected())
                .Where(c => c != null)
                .Distinct()
                .ToList();

            if (selectedCards.Count == 0)
            {
                Log.Info("Remove-card canceled.");
                return;
            }

            try
            {
                await CardPileCmd.RemoveFromDeck(selectedCards, showPreview: true);
                Log.Info($"Removed {selectedCards.Count} card(s) from live deck with visuals, player={player.NetId}, deckCount={player.Deck.Cards.Count}");
            }
            catch (Exception visualEx)
            {
                Log.Warn($"Visual remove-card failed, falling back to direct removal: {visualEx.Message}");
                var removed = 0;
                foreach (var selected in selectedCards)
                {
                    selected.RemoveFromState();
                    if (state.ContainsCard(selected))
                    {
                        state.RemoveCard(selected);
                    }

                    removed++;
                }

                Log.Info($"Removed {removed} card(s) from live deck via fallback, player={player.NetId}, deckCount={player.Deck.Cards.Count}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Remove-card flow failed: {ex.Message}");
        }
        finally
        {
            UnregisterRemoveCardSelectionScreen(screen);
            _isBusy = false;
        }
    }

    private static async Task RemoveRelicFlow()
    {
        if (!TryGetLiveRunAndPlayer(out _, out var player))
        {
            return;
        }

        _isBusy = true;
        try
        {
            await Task.Yield();

            var relics = player.Relics.ToList();
            if (relics.Count == 0)
            {
                Log.Warn("No relics to remove; remove-relic action skipped.");
                return;
            }

            var screen = NChooseARelicSelection.ShowScreen(relics);
            if (screen == null)
            {
                Log.Warn("Failed to create relic selection screen for remove action.");
                return;
            }

            BeginRelicMultiSelectSession(screen);
            TrySetupRelicMultiSelectUi(screen);

            var selectedRelics = (await screen.RelicsSelected())
                .Where(r => r != null)
                .ToList();

            if (selectedRelics.Count == 0)
            {
                Log.Info("Remove-relic canceled.");
                return;
            }

            var removed = 0;
            foreach (var selected in selectedRelics)
            {
                var owned = player.Relics.FirstOrDefault(r => ReferenceEquals(r, selected))
                    ?? player.GetRelicById(selected.Id);

                if (owned == null)
                {
                    Log.Warn($"Selected relic not found in player inventory: {selected.Id.Entry}");
                    continue;
                }

                try
                {
                    await RelicCmd.Remove(owned);
                    removed++;
                    Log.Info($"Removed relic: {owned.Id.Entry}, player={player.NetId}, relicCount={player.Relics.Count}");
                }
                catch (Exception removeEx)
                {
                    Log.Warn($"Failed to remove selected relic {owned.Id.Entry}: {removeEx.Message}");
                }
            }

            Log.Info($"Remove-relic flow completed. removed={removed}, selected={selectedRelics.Count}, player={player.NetId}, relicCount={player.Relics.Count}");
        }
        catch (Exception ex)
        {
            Log.Error($"Remove-relic flow failed: {ex.Message}");
        }
        finally
        {
            EndRelicMultiSelectSession();
            _isBusy = false;
        }
    }

    private static async Task AddRelicFromCollection(RelicModel relic, Player player)
    {
        try
        {
            await RelicCmd.Obtain(relic.ToMutable(), player, -1);
            Log.Info($"Added relic via relic collection: {relic.Id.Entry}, player={player.NetId}, relicCount={player.Relics.Count}");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to add selected relic from collection: {ex.Message}");
        }
    }

    private static bool TryGetLiveRunAndPlayer(out RunState state, out Player player)
    {
        var currentState = RunManager.Instance.DebugOnlyGetState();
        if (currentState == null)
        {
            Log.Warn("Run state unavailable; editor action ignored.");
            state = null!;
            player = null!;
            return false;
        }

        state = currentState;
        player = LocalContext.GetMe(state.Players) ?? state.Players.FirstOrDefault()!;
        if (player == null)
        {
            Log.Warn("No player found in run state; editor action ignored.");
            return false;
        }

        return true;
    }

    private static void BeginSelectionContext(RunState state, Player player)
    {
        _pendingSelectionState = state;
        _pendingSelectionPlayerNetId = player.NetId;
    }

    private static bool TryOpenCardLibraryPicker(RunState state)
    {
        try
        {
            var globalUi = NRun.Instance?.GlobalUi;
            var capstoneSubmenuStack = globalUi?.SubmenuStack;
            if (capstoneSubmenuStack == null)
            {
                Log.Warn("Submenu stack unavailable; cannot open card library picker.");
                return false;
            }

            if (NCapstoneContainer.Instance?.CurrentCapstoneScreen != capstoneSubmenuStack)
            {
                if (capstoneSubmenuStack.ShowScreen(CapstoneSubmenuType.PauseMenu) is NPauseMenu pauseMenu)
                {
                    pauseMenu.Initialize(state);
                }
            }

            var stack = capstoneSubmenuStack.Stack;
            if (stack == null)
            {
                Log.Warn("Run submenu stack unavailable; cannot open card library picker.");
                return false;
            }

            if (stack.Peek() is not NPauseMenu)
            {
                var pauseMenu = stack.GetSubmenuType<NPauseMenu>();
                pauseMenu.Initialize(state);
                stack.Push(pauseMenu);
            }

            var cardLibrary = stack.GetSubmenuType<NCardLibrary>();
            cardLibrary.Initialize(state);
            stack.Push(cardLibrary);

            ShowNavigationUiForSelection();
            Log.Info("Opened add-card picker using in-run card library.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to open card library add-picker: {ex.Message}");
            return false;
        }
    }

    private static bool TryOpenRelicCollectionPicker(RunState state)
    {
        try
        {
            var globalUi = NRun.Instance?.GlobalUi;
            var capstoneSubmenuStack = globalUi?.SubmenuStack;
            if (capstoneSubmenuStack == null)
            {
                Log.Warn("Submenu stack unavailable; cannot open relic collection picker.");
                return false;
            }

            if (NCapstoneContainer.Instance?.CurrentCapstoneScreen != capstoneSubmenuStack)
            {
                if (capstoneSubmenuStack.ShowScreen(CapstoneSubmenuType.PauseMenu) is NPauseMenu pauseMenu)
                {
                    pauseMenu.Initialize(state);
                }
            }

            var stack = capstoneSubmenuStack.Stack;
            if (stack == null)
            {
                Log.Warn("Run submenu stack unavailable; cannot open relic collection picker.");
                return false;
            }

            if (stack.Peek() is not NPauseMenu)
            {
                var pauseMenu = stack.GetSubmenuType<NPauseMenu>();
                pauseMenu.Initialize(state);
                stack.Push(pauseMenu);
            }

            var relicCollection = stack.GetSubmenuType<NRelicCollection>();
            stack.Push(relicCollection);

            ShowNavigationUiForSelection();
            Log.Info("Opened add-relic picker directly from pause menu using in-run relic collection.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to open relic collection add-picker: {ex.Message}");
            return false;
        }
    }

    private static bool TryResolvePendingSelectionContext(out RunState state, out Player player)
    {
        state = _pendingSelectionState ?? RunManager.Instance.DebugOnlyGetState()!;
        if (state == null)
        {
            player = null!;
            return false;
        }

        player = state.Players.FirstOrDefault(p => p.NetId == _pendingSelectionPlayerNetId)
            ?? LocalContext.GetMe(state.Players)
            ?? state.Players.FirstOrDefault()!;

        return player != null;
    }

    private static void ClearSelectionContext(string reason)
    {
        if (_awaitingCardLibraryPick || _awaitingRelicCollectionPick)
        {
            Log.Info($"Cleared selection context: {reason}");
        }

        HideNavigationUiForSelection();
        _skipNextLibraryClosedCleanup = false;
        _pendingRestoreAfterDeckView = false;
        _awaitingCardLibraryPick = false;
        _awaitingRelicCollectionPick = false;
        _pendingSelectionState = null;
        _pendingSelectionPlayerNetId = 0;
        _isBusy = false;
    }


    internal static void RegisterRemoveCardSelectionScreen(NDeckCardSelectScreen screen)
    {
        var screenId = screen.GetInstanceId();
        if (_removeCardSelectionScreenIds.Add(screenId))
        {
            Log.Info($"Registered remove-card selector screen: id={screenId}");
        }
    }

    internal static bool IsRemoveCardSelectionScreen(NDeckCardSelectScreen? screen)
    {
        return screen != null && _removeCardSelectionScreenIds.Contains(screen.GetInstanceId());
    }

    internal static void UnregisterRemoveCardSelectionScreen(NDeckCardSelectScreen? screen)
    {
        if (screen == null)
        {
            return;
        }

        var screenId = screen.GetInstanceId();
        if (_removeCardSelectionScreenIds.Remove(screenId))
        {
            Log.Info($"Unregistered remove-card selector screen: id={screenId}");
        }
    }

    internal static void RegisterEnchantCardSelectionScreen(NDeckCardSelectScreen screen)
    {
        var screenId = screen.GetInstanceId();
        if (_enchantCardSelectionScreenIds.Add(screenId))
        {
            Log.Info($"Registered enchant-card selector screen: id={screenId}");
        }
    }

    internal static bool IsEnchantCardSelectionScreen(NDeckCardSelectScreen? screen)
    {
        return screen != null && _enchantCardSelectionScreenIds.Contains(screen.GetInstanceId());
    }

    internal static void UnregisterEnchantCardSelectionScreen(NDeckCardSelectScreen? screen)
    {
        if (screen == null)
        {
            return;
        }

        var screenId = screen.GetInstanceId();
        if (_enchantCardSelectionScreenIds.Remove(screenId))
        {
            Log.Info($"Unregistered enchant-card selector screen: id={screenId}");
        }
    }

    internal static void TryHideRemoveCardPeekButton(NDeckCardSelectScreen? screen)
    {
        if (!IsRemoveCardSelectionScreen(screen) && !IsEnchantCardSelectionScreen(screen))
        {
            return;
        }

        var peekButton = screen!.GetNodeOrNull<Control>("%PeekButton");
        if (peekButton == null)
        {
            Log.Warn("Card selector peek button not found.");
            return;
        }

        peekButton.Visible = false;
        peekButton.MouseFilter = Control.MouseFilterEnum.Ignore;
        peekButton.FocusMode = Control.FocusModeEnum.None;
    }

    internal static bool IsRelicRemovalMultiSelectScreen(NChooseARelicSelection? screen)
    {
        return _isRelicMultiSelectActive
            && screen != null
            && ReferenceEquals(screen, _activeRelicMultiSelectScreen);
    }

    internal static void BeginRelicMultiSelectSession(NChooseARelicSelection screen)
    {
        _isRelicMultiSelectActive = true;
        _activeRelicMultiSelectScreen = screen;
        _selectedRelicHolderIds.Clear();
        Log.Info("Started remove-relic multi-select session.");
    }

    internal static void EndRelicMultiSelectSession()
    {
        if (_activeRelicMultiSelectScreen != null)
        {
            _relicWheelScrollByScreenId.Remove(_activeRelicMultiSelectScreen.GetInstanceId());
        }

        _selectedRelicHolderIds.Clear();
        _activeRelicMultiSelectScreen = null;
        _isRelicMultiSelectActive = false;
    }

    internal static void TrySetupRelicMultiSelectUi(NChooseARelicSelection screen)
    {
        if (!IsRelicRemovalMultiSelectScreen(screen))
        {
            return;
        }

        try
        {
            var skipButton = screen.GetNodeOrNull<NChoiceSelectionSkipButton>("SkipButton");
            if (skipButton == null)
            {
                return;
            }

            Traverse.Create(skipButton).Field<string>("_optionName").Value = "确定";
            var labelNode = skipButton.GetNodeOrNull<Node>("Label")
                ?? skipButton.GetNodeOrNull<Node>("%Label")
                ?? Traverse.Create(skipButton).Field<Node>("_label").Value;
            labelNode?.Call("SetTextAutoSize", "确定");

            ConfigureRemoveRelicSelectionTexts(screen);
            EnsureRelicWheelInputConnected(screen);
            EnsureRelicBackButton(screen);
            Log.Info("Configured remove-relic selector UI texts.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to configure remove-relic selector UI: {ex.Message}");
        }
    }

    internal static bool TryToggleRelicMultiSelection(NChooseARelicSelection screen, NRelicBasicHolder holder)
    {
        if (!IsRelicRemovalMultiSelectScreen(screen))
        {
            return false;
        }

        var holderId = holder.GetInstanceId();
        var isSelected = _selectedRelicHolderIds.Add(holderId);
        if (!isSelected)
        {
            _selectedRelicHolderIds.Remove(holderId);
        }

        ApplyRelicHolderSelectionVisual(holder, isSelected);
        Log.Info($"Toggle remove-relic selection: relic={(holder.Relic?.Model?.Id.Entry ?? "unknown")}, selected={isSelected}, total={_selectedRelicHolderIds.Count}");
        return true;
    }

    internal static bool TryConfirmRelicMultiSelection(NChooseARelicSelection screen)
    {
        if (!IsRelicRemovalMultiSelectScreen(screen))
        {
            return false;
        }

        try
        {
            var selectedRelics = CollectSelectedRelicsFromScreen(screen).ToList();
            var traverse = Traverse.Create(screen);
            var completionSource = traverse.Field<TaskCompletionSource<IEnumerable<RelicModel>>>("_completionSource").Value;
            if (completionSource == null)
            {
                Log.Warn("Relic multi-select completion source was null; using default skip behavior.");
                EndRelicMultiSelectSession();
                return false;
            }

            traverse.Field<bool>("_screenComplete").Value = true;
            traverse.Field<bool>("_relicSelected").Value = selectedRelics.Count > 0;

            if (!completionSource.Task.IsCompleted)
            {
                completionSource.SetResult(selectedRelics);
            }

            Log.Info($"Confirmed remove-relic multi-select: selected={selectedRelics.Count}");
            EndRelicMultiSelectSession();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to confirm remove-relic multi-select: {ex.Message}");
            EndRelicMultiSelectSession();
            return false;
        }
    }

    internal static bool TryCancelRelicMultiSelection(NChooseARelicSelection screen)
    {
        if (!IsRelicRemovalMultiSelectScreen(screen))
        {
            return false;
        }

        try
        {
            var traverse = Traverse.Create(screen);
            var completionSource = traverse.Field<TaskCompletionSource<IEnumerable<RelicModel>>>("_completionSource").Value;
            if (completionSource == null)
            {
                Log.Warn("Relic multi-select completion source was null while canceling; fallback to default behavior.");
                EndRelicMultiSelectSession();
                return false;
            }

            traverse.Field<bool>("_screenComplete").Value = true;
            traverse.Field<bool>("_relicSelected").Value = false;
            _selectedRelicHolderIds.Clear();

            if (!completionSource.Task.IsCompleted)
            {
                completionSource.SetResult(Array.Empty<RelicModel>());
            }

            Log.Info("Canceled remove-relic multi-select via custom back button.");
            EndRelicMultiSelectSession();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to cancel remove-relic multi-select: {ex.Message}");
            EndRelicMultiSelectSession();
            return false;
        }
    }
    private static IEnumerable<RelicModel> CollectSelectedRelicsFromScreen(NChooseARelicSelection screen)
    {
        var relicRow = screen.GetNodeOrNull<Control>("RelicRow");
        if (relicRow == null)
        {
            yield break;
        }

        foreach (var holder in relicRow.GetChildren(false).OfType<NRelicBasicHolder>())
        {
            if (!_selectedRelicHolderIds.Contains(holder.GetInstanceId()))
            {
                continue;
            }

            var model = holder.Relic?.Model;
            if (model != null)
            {
                yield return model;
            }
        }
    }

    private static void ApplyRelicHolderSelectionVisual(NRelicBasicHolder holder, bool isSelected)
    {
        holder.Modulate = isSelected ? new Color(0.72f, 1f, 0.72f, 1f) : Colors.White;
    }


    private static void ConfigureRemoveRelicSelectionTexts(NChooseARelicSelection screen)
    {
        var banner = Traverse.Create(screen).Field<NCommonBanner>("_banner").Value
            ?? screen.GetNodeOrNull<NCommonBanner>("Banner");

        banner?.label?.SetTextAutoSize(RelicSelectionBannerText);

        var bannerNode = banner as Control ?? screen.GetNodeOrNull<Control>("Banner");
        var bannerLabelNode = bannerNode?.GetNodeOrNull<Node>("Label")
            ?? bannerNode?.GetNodeOrNull<Node>("%Label");
        bannerLabelNode?.Call("SetTextAutoSize", RelicSelectionBannerText);

        var hintLabel = screen.GetNodeOrNull<Label>(RelicWheelHintNodeName);
        if (hintLabel == null)
        {
            hintLabel = new Label
            {
                Name = RelicWheelHintNodeName,
                Text = RelicWheelHintText,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Modulate = new Color(1f, 1f, 1f, 0.78f),
                ZIndex = 1000
            };

            hintLabel.AddThemeFontSizeOverride("font_size", 20);
            screen.AddChild(hintLabel);
        }
        else
        {
            hintLabel.Text = RelicWheelHintText;
            hintLabel.ZIndex = 1000;
        }

        var bannerBottom = 280f;
        if (bannerNode != null)
        {
            var bannerHeight = Math.Max(64f, bannerNode.Size.Y);
            bannerBottom = bannerNode.Position.Y + bannerHeight;
        }

        var top = bannerBottom + 10f;
        hintLabel.AnchorLeft = 0.5f;
        hintLabel.AnchorRight = 0.5f;
        hintLabel.AnchorTop = 0f;
        hintLabel.AnchorBottom = 0f;
        hintLabel.OffsetLeft = -220f;
        hintLabel.OffsetRight = 220f;
        hintLabel.OffsetTop = top;
        hintLabel.OffsetBottom = top + 30f;
    }
    private static void EnsureRelicBackButton(NChooseARelicSelection screen)
    {
        RemoveLegacyRelicBackButton(screen);

        var backButton = screen.GetNodeOrNull<NBackButton>(RelicBackButtonNodeName);
        if (backButton == null)
        {
            var template = FindBackButtonTemplate(screen);
            if (template == null)
            {
                Log.Warn("Could not find native NBackButton template for remove-relic selector.");
                return;
            }

            var duplicatedNode = template.Duplicate((int)(Node.DuplicateFlags.Groups | Node.DuplicateFlags.Scripts | Node.DuplicateFlags.UseInstantiation));
            backButton = duplicatedNode as NBackButton;
            if (backButton == null)
            {
                Log.Warn("Failed to clone native NBackButton for remove-relic selector.");
                return;
            }

            backButton.Name = RelicBackButtonNodeName;
            backButton.ZIndex = 1000;
            backButton.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ =>
            {
                TryCancelRelicMultiSelection(screen);
            }), 0U);
            screen.AddChild(backButton);
        }

        backButton.ZIndex = 1000;
        NormalizeBackButtonOffset(backButton);
        backButton.Call("OnWindowChange");
        backButton.Call("MoveToHidePosition");
        backButton.ReleaseFocus();
        backButton.Disable();
        backButton.Enable();
    }

    private static void RemoveLegacyRelicBackButton(NChooseARelicSelection screen)
    {
        var legacyButton = screen.GetNodeOrNull<NChoiceSelectionSkipButton>(RelicBackButtonNodeName);
        legacyButton?.QueueFree();
    }


    private static void NormalizeBackButtonOffset(NBackButton backButton)
    {
        try
        {
            var traverse = Traverse.Create(backButton);
            var posOffset = traverse.Field<Vector2>("_posOffset").Value;
            const float defaultX = 40f;
            if (Math.Abs(posOffset.X - defaultX) > 0.01f)
            {
                traverse.Field<Vector2>("_posOffset").Value = new Vector2(defaultX, posOffset.Y);
                Log.Info($"Normalized remove-relic back button offset X: {posOffset.X:0.##} -> {defaultX:0.##}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to normalize remove-relic back button offset: {ex.Message}");
        }
    }
    private static NBackButton? FindBackButtonTemplate(NChooseARelicSelection screen)
    {
        var root = screen.GetTree()?.Root;
        if (root == null)
        {
            return null;
        }

        NBackButton? preferredVisible = null;
        var preferredVisibleX = float.MaxValue;
        NBackButton? preferredAny = null;
        var preferredAnyX = float.MaxValue;
        NBackButton? fallbackVisible = null;
        var fallbackVisibleX = float.MaxValue;
        NBackButton? fallbackAny = null;
        var fallbackAnyX = float.MaxValue;

        var stack = new Stack<Node>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is NBackButton backButton && !backButton.IsQueuedForDeletion())
            {
                if (!screen.IsAncestorOf(backButton) && HasBackButtonVisualChildren(backButton))
                {
                    var nodeName = backButton.Name.ToString();
                    var isPreferred = string.Equals(nodeName, "BackButton", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(nodeName, "Back", StringComparison.OrdinalIgnoreCase);
                    var isInvalid = nodeName.Contains("Cancel", StringComparison.OrdinalIgnoreCase)
                        || nodeName.Contains("Close", StringComparison.OrdinalIgnoreCase)
                        || nodeName.Contains("Unready", StringComparison.OrdinalIgnoreCase);

                    if (!isInvalid)
                    {
                        var x = backButton.GlobalPosition.X;
                        if (isPreferred)
                        {
                            if (x < preferredAnyX)
                            {
                                preferredAny = backButton;
                                preferredAnyX = x;
                            }

                            if (backButton.IsVisibleInTree() && x < preferredVisibleX)
                            {
                                preferredVisible = backButton;
                                preferredVisibleX = x;
                            }
                        }
                        else
                        {
                            if (x < fallbackAnyX)
                            {
                                fallbackAny = backButton;
                                fallbackAnyX = x;
                            }

                            if (backButton.IsVisibleInTree() && x < fallbackVisibleX)
                            {
                                fallbackVisible = backButton;
                                fallbackVisibleX = x;
                            }
                        }
                    }
                }
            }

            for (var i = current.GetChildCount() - 1; i >= 0; i--)
            {
                var child = current.GetChild(i);
                if (child == null)
                {
                    continue;
                }

                stack.Push(child);
            }
        }

        var chosen = preferredVisible
            ?? preferredAny
            ?? fallbackVisible
            ?? fallbackAny;

        if (chosen != null)
        {
            Log.Info($"Using native back-button template: name={chosen.Name}, x={chosen.GlobalPosition.X:0.##}");
        }

        return chosen;
    }

    private static bool HasBackButtonVisualChildren(NBackButton backButton)
    {
        return backButton.GetNodeOrNull<Control>("Outline") != null
            && backButton.GetNodeOrNull<Control>("Image") != null;
    }

    private static void EnsureRelicWheelInputConnected(NChooseARelicSelection screen)
    {
        if (!IsRelicRemovalMultiSelectScreen(screen))
        {
            return;
        }

        var screenId = screen.GetInstanceId();
        if (_relicWheelScrollByScreenId.ContainsKey(screenId))
        {
            return;
        }

        var relicRow = screen.GetNodeOrNull<Control>("RelicRow");
        if (relicRow == null)
        {
            return;
        }

        _relicWheelScrollByScreenId[screenId] = new RelicWheelScrollState(relicRow.Position.X);

        try
        {
            ConnectWheelHandler(screen, screen);
            ConnectWheelHandler(screen, relicRow);
            foreach (var holder in relicRow.GetChildren(false).OfType<NRelicBasicHolder>())
            {
                ConnectWheelHandler(screen, holder);
            }

            Log.Info("Enabled wheel scrolling for remove-relic selector.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to enable wheel scrolling for remove-relic selector: {ex.Message}");
        }
    }

    private static void ConnectWheelHandler(NChooseARelicSelection screen, Control source)
    {
        source.Connect(Control.SignalName.GuiInput, Callable.From<InputEvent>(evt => TryHandleRelicWheelInput(screen, evt)), 0U);
    }

    internal static void TryHandleRelicWheelInput(NChooseARelicSelection screen, InputEvent evt)
    {
        if (!IsRelicRemovalMultiSelectScreen(screen))
        {
            return;
        }

        if (evt is not InputEventMouseButton mouse || !mouse.Pressed)
        {
            return;
        }

        var direction = mouse.ButtonIndex switch
        {
            MouseButton.WheelUp => 1,
            MouseButton.WheelDown => -1,
            _ => 0
        };

        if (direction == 0)
        {
            return;
        }

        if (TryScrollRelicRow(screen, direction))
        {
            screen.AcceptEvent();
            screen.GetViewport()?.SetInputAsHandled();
        }
    }

    private static bool TryScrollRelicRow(NChooseARelicSelection screen, int direction)
    {
        var screenId = screen.GetInstanceId();
        if (!_relicWheelScrollByScreenId.TryGetValue(screenId, out var state))
        {
            return false;
        }

        var relicRow = screen.GetNodeOrNull<Control>("RelicRow");
        if (relicRow == null)
        {
            return false;
        }

        var holders = relicRow.GetChildren(false).OfType<NRelicBasicHolder>().ToList();
        if (holders.Count == 0)
        {
            return false;
        }

        var minX = float.MaxValue;
        var maxX = float.MinValue;
        var maxWidth = 0f;
        foreach (var holder in holders)
        {
            minX = Math.Min(minX, holder.Position.X);
            maxX = Math.Max(maxX, holder.Position.X);
            maxWidth = Math.Max(maxWidth, holder.Size.X * holder.Scale.X);
        }

        if (maxWidth <= 1f)
        {
            maxWidth = 180f;
        }

        var contentWidth = (maxX - minX) + maxWidth;
        var viewportWidth = screen.GetViewportRect().Size.X;
        var padding = 160f;
        var visibleWidth = Math.Max(240f, viewportWidth - padding * 2f);
        var maxOffsetAbs = Math.Max(0f, (contentWidth - visibleWidth) * 0.5f);

        if (maxOffsetAbs <= 1f)
        {
            state.Offset = 0f;
            relicRow.Position = new Vector2(state.BaseRowX, relicRow.Position.Y);
            return false;
        }

        const float scrollStep = 170f;
        state.Offset = Mathf.Clamp(state.Offset + direction * scrollStep, -maxOffsetAbs, maxOffsetAbs);
        relicRow.Position = new Vector2(state.BaseRowX + state.Offset, relicRow.Position.Y);
        return true;
    }

    private sealed class RelicWheelScrollState
    {
        public RelicWheelScrollState(float baseRowX)
        {
            BaseRowX = baseRowX;
            Offset = 0f;
        }

        public float BaseRowX { get; }

        public float Offset { get; set; }
    }
    private static bool IsAddUpgradedEnabled(NCardLibrary? library)
    {
        try
        {
            return library?.GetNodeOrNull<NLibraryStatTickbox>("%Upgrades")?.IsTicked ?? false;
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to read 'add upgraded' toggle state: {ex.Message}");
            return false;
        }
    }

    private static void ShowNavigationUiForSelection()
    {
        if (_navigationUiForcedVisible)
        {
            return;
        }

        try
        {
            var globalUi = NRun.Instance?.GlobalUi;
            globalUi?.TopBar?.AnimShow();

            if (_awaitingCardLibraryPick)
            {
                globalUi?.RelicInventory?.AnimHide();
            }
            else
            {
                globalUi?.RelicInventory?.AnimShow();
            }

            globalUi?.MultiplayerPlayerContainer?.AnimShow();
            _navigationUiForcedVisible = true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to show navigation UI for selection: {ex.Message}");
        }
    }

    private static void HideNavigationUiForSelection()
    {
        if (!_navigationUiForcedVisible)
        {
            return;
        }

        try
        {
            var globalUi = NRun.Instance?.GlobalUi;
            globalUi?.TopBar?.AnimHide();
            globalUi?.RelicInventory?.AnimHide();
            globalUi?.MultiplayerPlayerContainer?.AnimHide();
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to restore navigation UI after selection: {ex.Message}");
        }
        finally
        {
            _navigationUiForcedVisible = false;
        }
    }

    private static void CloseCapstonePickerIfPresent(string reason)
    {
        try
        {
            if (NCapstoneContainer.Instance?.CurrentCapstoneScreen is NCapstoneSubmenuStack)
            {
                NCapstoneContainer.Instance.Close();
                Log.Info($"Closed capstone picker after selection flow: {reason}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to close capstone picker after selection flow: {ex.Message}");
        }
    }
    private static void ClosePauseMenu()
    {
        try
        {
            NCapstoneContainer.Instance?.Close();
            NRun.Instance?.GlobalUi?.TopBar?.Pause?.ToggleAnimState();
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to close pause menu before action: {ex.Message}");
        }
    }
}

















































