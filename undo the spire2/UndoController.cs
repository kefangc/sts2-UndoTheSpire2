using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Orbs;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace UndoTheSpire2;

public sealed class UndoController
{
    private static readonly PileType[] CombatPileOrder =
    [
        PileType.Hand,
        PileType.Draw,
        PileType.Discard,
        PileType.Exhaust,
        PileType.Play
    ];

    private static readonly MethodInfo? NotifyCombatStateChangedMethod =
        typeof(CombatStateTracker).GetMethod("NotifyCombatStateChanged", BindingFlags.Instance | BindingFlags.NonPublic);

    private const int MaxSnapshots = 50;
    private readonly LinkedList<UndoSnapshot> _pastSnapshots = [];
    private readonly LinkedList<UndoSnapshot> _futureSnapshots = [];
    private long _nextSequenceId = 1;
    private UndoCombatReplayState? _combatReplay;

    public event Action? StateChanged;

    public bool IsRestoring { get; private set; }

    public bool HasUndo => _pastSnapshots.Count > 0;

    public bool HasRedo => _futureSnapshots.Count > 0;

    public string UndoLabel => _pastSnapshots.First?.Value.ActionLabel ?? string.Empty;

    public string RedoLabel => _futureSnapshots.First?.Value.ActionLabel ?? string.Empty;

    public void OnCombatUiActivated(NCombatUi combatUi, CombatState combatState)
    {
        NotifyStateChanged();
    }

    public void OnCombatUiDeactivated(NCombatUi combatUi)
    {
        NotifyStateChanged();
    }

    public void OnCombatReplayInitialized(SerializableRun initialRun)
    {
        if (!RunManager.Instance.IsSinglePlayerOrFakeMultiplayer)
            return;

        if (IsRestoring)
        {
            MainFile.Logger.Info("Ignoring combat replay reinitialization during restore.");
            return;
        }

        _combatReplay = new UndoCombatReplayState(
            CloneRun(initialRun),
            RunManager.Instance.ActionQueueSet.NextActionId,
            RunManager.Instance.ActionQueueSynchronizer.NextHookId,
            RunManager.Instance.ChecksumTracker.NextId,
            [.. RunManager.Instance.PlayerChoiceSynchronizer.ChoiceIds]);

        _pastSnapshots.Clear();
        _futureSnapshots.Clear();
        _nextSequenceId = 1;
        MainFile.Logger.Info("Initialized combat replay state for undo.");
        NotifyStateChanged();
    }

    public void RecordReplayGameAction(GameAction gameAction)
    {
        if (!CanRecordReplayEvent())
            return;

        if (gameAction is GenericHookGameAction hookAction)
        {
            AppendReplayEvent(new CombatReplayEvent
            {
                playerId = gameAction.OwnerId,
                eventType = CombatReplayEventType.HookAction,
                hookId = hookAction.HookId,
                gameActionType = hookAction.ActionType
            });
            return;
        }

        if (!gameAction.RecordableToReplay)
        {
            MainFile.Logger.Warn($"Skipping unrecordable replay action: {gameAction}");
            return;
        }

        AppendReplayEvent(new CombatReplayEvent
        {
            playerId = gameAction.OwnerId,
            eventType = CombatReplayEventType.GameAction,
            action = gameAction.ToNetAction()
        });
    }

    public void RecordReplayActionResume(uint actionId)
    {
        if (!CanRecordReplayEvent())
            return;

        AppendReplayEvent(new CombatReplayEvent
        {
            eventType = CombatReplayEventType.ResumeAction,
            actionId = actionId
        });
    }

    public void RecordReplayPlayerChoice(Player player, uint choiceId, NetPlayerChoiceResult result)
    {
        if (!CanRecordReplayEvent())
            return;

        AppendReplayEvent(new CombatReplayEvent
        {
            eventType = CombatReplayEventType.PlayerChoice,
            playerId = player.NetId,
            choiceId = choiceId,
            playerChoiceResult = result
        });
    }

    public void ClearHistory(string reason)
    {
        if (IsRestoring)
            return;

        ClearHistoryInternal(reason);
        NotifyStateChanged();
    }

    public void TryCaptureAction(UndoActionKind actionKind, GameAction action)
    {
        if (!ShouldCapture(action))
            return;

        TryCaptureSnapshot(actionKind, GetReplayEventCountBeforeCurrentAction(), DescribeAction(actionKind, action));
    }

    public void TryCapturePlayerChoice(GameAction action)
    {
        if (!ShouldCapture(action))
            return;

        TryCaptureSnapshot(UndoActionKind.PlayerChoice, GetCurrentReplayEventCount(), DescribeAction(UndoActionKind.PlayerChoice, action));
    }

    public bool ShouldShowHud(NCombatUi combatUi)
    {
        return GodotObject.IsInstanceValid(combatUi)
            && combatUi.IsVisibleInTree()
            && IsSinglePlayerCombat()
            && CombatManager.Instance.IsInProgress;
    }

    public bool CanUndoNow(NCombatUi combatUi)
    {
        return HasUndo && CanOperateNow(combatUi);
    }

    public bool CanRedoNow(NCombatUi combatUi)
    {
        return HasRedo && CanOperateNow(combatUi);
    }

    public bool TryHandleHotkey(NCombatUi combatUi, InputEvent inputEvent)
    {
        if (!ShouldShowHud(combatUi))
            return false;

        if (inputEvent is not InputEventKey keyEvent || !keyEvent.Pressed || keyEvent.Echo)
            return false;

        if (!keyEvent.CtrlPressed || keyEvent.AltPressed || keyEvent.MetaPressed)
            return false;

        if (MatchesHotkey(keyEvent, Key.Z))
        {
            TryHandleUndoRequest(combatUi, "hotkey");
            combatUi.GetViewport()?.SetInputAsHandled();
            return true;
        }

        if (MatchesHotkey(keyEvent, Key.Y))
        {
            TryHandleRedoRequest(combatUi, "hotkey");
            combatUi.GetViewport()?.SetInputAsHandled();
            return true;
        }

        return false;
    }

    public void TryHandleUndoRequest(NCombatUi combatUi, string source)
    {
        if (!CanUndoNow(combatUi))
        {
            MainFile.Logger.Info($"Ignored undo request from {source}. HasUndo={HasUndo}, CanRestore={CanRestoreState()}, UiBlocked={IsUiBlocking(combatUi)}");
            return;
        }

        MainFile.Logger.Info($"Undo requested from {source}. Latest={UndoLabel}");
        Undo();
    }

    public void TryHandleRedoRequest(NCombatUi combatUi, string source)
    {
        if (!CanRedoNow(combatUi))
        {
            MainFile.Logger.Info($"Ignored redo request from {source}. HasRedo={HasRedo}, CanRestore={CanRestoreState()}, UiBlocked={IsUiBlocking(combatUi)}");
            return;
        }

        MainFile.Logger.Info($"Redo requested from {source}. Latest={RedoLabel}");
        Redo();
    }

    public void Undo()
    {
        if (!HasUndo || IsRestoring)
            return;

        TaskHelper.RunSafely(RestoreFromHistoryAsync(_pastSnapshots, _futureSnapshots, "undo"));
    }

    public void Redo()
    {
        if (!HasRedo || IsRestoring)
            return;

        TaskHelper.RunSafely(RestoreFromHistoryAsync(_futureSnapshots, _pastSnapshots, "redo"));
    }

    private async Task RestoreFromHistoryAsync(
        LinkedList<UndoSnapshot> source,
        LinkedList<UndoSnapshot> destination,
        string operation)
    {
        if (_combatReplay == null)
            return;

        if (!CanRestoreState())
            return;

        bool movedCurrentChoiceAnchor = MoveCurrentChoiceAnchorToDestinationIfNeeded(source, destination);
        if (source.First?.Value is not UndoSnapshot snapshot)
            return;

        UndoSnapshot? baseSnapshot = source.First.Next?.Value;

        source.RemoveFirst();
        if (!movedCurrentChoiceAnchor)
        {
            UndoSnapshot currentSnapshot = new(
                CaptureCurrentCombatFullState(),
                GetCurrentReplayEventCount(),
                snapshot.ActionKind,
                _nextSequenceId++,
                snapshot.ActionLabel);

            destination.AddFirst(currentSnapshot);
            TrimSnapshots(destination);
        }

        IsRestoring = true;
        NotifyStateChanged();

        try
        {
            string restoreMode = await RestoreSnapshotAsync(snapshot, baseSnapshot);
            _combatReplay.ActiveEventCount = snapshot.ReplayEventCount;
            EnsurePlayerChoiceUndoAnchor(snapshot);
            MainFile.Logger.Info($"{operation} completed. Restored={snapshot.ActionLabel}, ReplayEvents={snapshot.ReplayEventCount}, Mode={restoreMode}");
        }
        catch (Exception ex)
        {
            destination.RemoveFirst();
            source.AddFirst(snapshot);
            MainFile.Logger.Error($"Failed to {operation}: {ex}");
        }
        finally
        {
            IsRestoring = false;
            NotifyStateChanged();
        }
    }
    private async Task<string> RestoreSnapshotAsync(UndoSnapshot snapshot, UndoSnapshot? baseSnapshot)
    {
        if (snapshot.ActionKind == UndoActionKind.PlayerChoice)
        {
            if (baseSnapshot != null && await TryRestorePlayerChoiceInPlaceAsync(baseSnapshot, snapshot))
                return "choice_delta";
        }
        else if (await TryApplyFullStateInPlaceAsync(snapshot.CombatState))
        {
            WriteInteractionLog("restore_full_state", $"kind={snapshot.ActionKind} label={snapshot.ActionLabel} replayEvents={snapshot.ReplayEventCount}");
            return "full_state";
        }

        WriteInteractionLog("restore_fallback_to_replay", $"kind={snapshot.ActionKind} label={snapshot.ActionLabel} replayEvents={snapshot.ReplayEventCount}");
        await RestoreCombatReplayAsync(snapshot.ReplayEventCount, snapshot.ActionKind == UndoActionKind.PlayerChoice);
        return "replay";
    }

    private async Task<bool> TryRestorePlayerChoiceInPlaceAsync(UndoSnapshot baseSnapshot, UndoSnapshot targetSnapshot)
    {
        if (baseSnapshot.ReplayEventCount > targetSnapshot.ReplayEventCount)
            return false;

        NRun? currentRun = NGame.Instance?.CurrentRunNode;
        bool restoreVisible = currentRun?.Visible ?? false;
        if (currentRun != null)
            currentRun.Visible = false;

        try
        {
            if (!await TryApplyFullStateInPlaceAsync(baseSnapshot.CombatState, true))
                return false;

            if (currentRun != null)
                currentRun.Visible = false;

            RunState? runState = RunManager.Instance.DebugOnlyGetState();
            if (runState == null)
                return false;

            await ReplayEventsAsync(runState, baseSnapshot.ReplayEventCount, targetSnapshot.ReplayEventCount);
            await WaitForReplayToSettleAsync();
            await WaitForChoiceUiReadyAsync();
            return IsChoiceUiReady();
        }
        finally
        {
            if (currentRun != null)
            {
                currentRun.Visible = restoreVisible;
                await WaitOneFrameAsync();
            }
        }
    }

    private async Task<bool> TryApplyFullStateInPlaceAsync(UndoCombatFullState snapshot, bool hideCurrentRun = false)
    {
        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        if (runState == null || combatState == null)
            return false;

        if (!CanApplyFullStateInPlace(snapshot.FullState, runState, combatState, out string reason))
        {
            MainFile.Logger.Info($"Falling back to replay restore. Reason={reason}");
            return false;
        }

        NRun? currentRun = NGame.Instance?.CurrentRunNode;
        bool restoreVisible = currentRun?.Visible ?? false;
        if (hideCurrentRun && currentRun != null)
            currentRun.Visible = false;

        try
        {
            DismissSupportedChoiceUiIfPresent();
            RunManager.Instance.ActionQueueSet.Reset();
            RebuildActionQueues(runState.Players);
            runState.Rng.LoadFromSerializable(snapshot.FullState.Rng);

            RestorePlayers(runState, combatState, snapshot.FullState);
            RestoreCreatures(runState, combatState, snapshot.FullState);

            combatState.RoundNumber = snapshot.RoundNumber;
            combatState.CurrentSide = snapshot.CurrentSide;

            RunManager.Instance.ActionQueueSet.FastForwardNextActionId(snapshot.NextActionId);
            RunManager.Instance.ActionQueueSynchronizer.FastForwardHookId(snapshot.NextHookId);
            RunManager.Instance.ChecksumTracker.LoadReplayChecksums([], snapshot.NextChecksumId);
            RunManager.Instance.PlayerChoiceSynchronizer.FastForwardChoiceIds([.. snapshot.FullState.nextChoiceIds]);
            RestoreActionSynchronizationState(snapshot.SynchronizerCombatState);
            RebuildTransientCombatCaches(runState);

            foreach (Player player in runState.Players)
            {
                if (player.Creature.IsAlive)
                    player.ActivateHooks();
                else
                    player.DeactivateHooks();
            }

            await RefreshCombatUiAsync(combatState);
            return true;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Full-state restore failed, falling back to replay. {ex}");
            return false;
        }
        finally
        {
            if (hideCurrentRun && currentRun != null)
            {
                currentRun.Visible = restoreVisible;
                await WaitOneFrameAsync();
            }
        }
    }
    private async Task RestoreCombatReplayAsync(int targetEventCount, bool expectChoiceUi)
    {
        if (_combatReplay == null)
            throw new InvalidOperationException("Combat replay state is not initialized.");

        targetEventCount = Math.Clamp(targetEventCount, 0, _combatReplay.Events.Count);
        SerializableRun initialRun = CloneRun(_combatReplay.InitialRun);
        RunState runState = RunState.FromSerializable(initialRun);

        RunManager.Instance.ActionQueueSet.Reset();
        NRunMusicController.Instance?.StopMusic();

        await NGame.Instance!.Transition.FadeOut();

        RunManager.Instance.CleanUp();
        RunManager.Instance.SetUpSavedSinglePlayer(runState, initialRun);
        NGame.Instance.ReactionContainer.InitializeNetworking(new NetSingleplayerGameService());

        await PreloadManager.LoadRunAssets(runState.Players.Select(static player => player.Character));
        await PreloadManager.LoadActAssets(runState.Act);
        RunManager.Instance.Launch();
        NRun replayRun = NRun.Create(runState);
        NGame.Instance.RootSceneContainer.SetCurrentScene(replayRun);
        replayRun.Visible = false;
        await RunManager.Instance.GenerateMap();

        RunManager.Instance.ActionQueueSet.FastForwardNextActionId(_combatReplay.InitialNextActionId);
        RunManager.Instance.ActionQueueSynchronizer.FastForwardHookId(_combatReplay.InitialNextHookId);
        RunManager.Instance.ChecksumTracker.LoadReplayChecksums(new List<ReplayChecksumData>(), _combatReplay.InitialNextChecksumId);
        RunManager.Instance.PlayerChoiceSynchronizer.FastForwardChoiceIds([.. _combatReplay.InitialChoiceIds]);

        await RunManager.Instance.LoadIntoLatestMapCoord(AbstractRoom.FromSerializable(initialRun.PreFinishedRoom, runState));
        await WaitForExecutorToUnpauseAsync();
        await ReplayEventsAsync(runState, targetEventCount);
        await WaitForReplayToSettleAsync();
        if (expectChoiceUi)
            await WaitForChoiceUiReadyAsync();
        replayRun.Visible = true;
        await WaitOneFrameAsync();
        await NGame.Instance.Transition.FadeIn();
    }

    private async Task ReplayEventsAsync(RunState runState, int targetEventCount)
    {
        await ReplayEventsAsync(runState, 0, targetEventCount);
    }

    private async Task ReplayEventsAsync(RunState runState, int startEventIndex, int targetEventCount)
    {
        if (_combatReplay == null)
            return;

        startEventIndex = Math.Clamp(startEventIndex, 0, _combatReplay.Events.Count);
        targetEventCount = Math.Clamp(targetEventCount, startEventIndex, _combatReplay.Events.Count);

        for (int i = startEventIndex; i < targetEventCount; i++)
        {
            CombatReplayEvent replayEvent = _combatReplay.Events[i];
            switch (replayEvent.eventType)
            {
                case CombatReplayEventType.GameAction:
                {
                    while (CombatManager.Instance.EndingPlayerTurnPhaseOne || CombatManager.Instance.EndingPlayerTurnPhaseTwo)
                        await WaitOneFrameAsync();

                    Player player = runState.GetPlayer(replayEvent.playerId!.Value)
                        ?? throw new InvalidOperationException($"Replay action player {replayEvent.playerId!.Value} was not found.");
                    GameAction action = replayEvent.action?.ToGameAction(player)
                        ?? throw new InvalidOperationException("Replay action payload was missing.");
                    if (action.ActionType == GameActionType.CombatPlayPhaseOnly)
                    {
                        while (CombatManager.Instance.DebugOnlyGetState()?.CurrentSide == CombatSide.Enemy)
                            await WaitOneFrameAsync();
                    }

                    RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(action);
                    if (action is EndPlayerTurnAction or ReadyToBeginEnemyTurnAction)
                        await RunManager.Instance.ActionExecutor.FinishedExecutingActions();
                    break;
                }
                case CombatReplayEventType.HookAction:
                    RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(
                        RunManager.Instance.ActionQueueSynchronizer.GetHookActionForId(
                            replayEvent.hookId!.Value,
                            replayEvent.playerId!.Value,
                            replayEvent.gameActionType!.Value));
                    break;
                case CombatReplayEventType.ResumeAction:
                    RunManager.Instance.ActionQueueSet.ResumeActionWithoutSynchronizing(replayEvent.actionId!.Value);
                    break;
                case CombatReplayEventType.PlayerChoice:
                {
                    Player player = runState.GetPlayer(replayEvent.playerId!.Value)
                        ?? throw new InvalidOperationException($"Replay choice player {replayEvent.playerId!.Value} was not found.");
                    RunManager.Instance.PlayerChoiceSynchronizer.ReceiveReplayChoice(
                        player,
                        replayEvent.choiceId!.Value,
                        replayEvent.playerChoiceResult!.Value);
                    break;
                }
                default:
                    throw new InvalidOperationException($"Unsupported replay event type: {replayEvent.eventType}");
            }
        }
    }

    private void TryCaptureSnapshot(UndoActionKind actionKind, int replayEventCount, string actionLabel)
    {
        if (!CanCaptureSnapshot())
            return;

        try
        {
            replayEventCount = Math.Max(0, replayEventCount);
            UndoSnapshot snapshot = new(CaptureCurrentCombatFullState(), replayEventCount, actionKind, _nextSequenceId++, actionLabel);
            _pastSnapshots.AddFirst(snapshot);
            TrimSnapshots(_pastSnapshots);
            _futureSnapshots.Clear();
            MainFile.Logger.Info($"Captured snapshot #{snapshot.SequenceId}: {snapshot.ActionLabel}. ReplayEvents={snapshot.ReplayEventCount}, UndoCount={_pastSnapshots.Count}");
            NotifyStateChanged();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"Failed to capture snapshot for {actionKind}: {ex}");
        }
    }

    private bool CanCaptureSnapshot()
    {
        return !IsRestoring && IsSinglePlayerCombat() && _combatReplay != null;
    }

    private static string DescribeAction(UndoActionKind actionKind, GameAction? action)
    {
        return actionKind switch
        {
            UndoActionKind.PlayCard => FormatActionLabel("action.play_card", "Play {0}", GetPlayedCardName(action)),
            UndoActionKind.UsePotion => FormatActionLabel("action.use_potion", "Use {0}", GetPotionName(action)),
            UndoActionKind.DiscardPotion => FormatActionLabel("action.discard_potion", "Discard {0}", GetDiscardedPotionName(action)),
            UndoActionKind.EndTurn => ModLocalization.Get("action.end_turn", "End turn"),
            UndoActionKind.PlayerChoice => ModLocalization.Get("action.player_choice", "Make a choice"),
            _ => ModLocalization.Get("action.undo", "Undo")
        };
    }

    private static string FormatActionLabel(string key, string fallback, string? subject)
    {
        string template = ModLocalization.Get(key, fallback);
        return string.IsNullOrWhiteSpace(subject) ? template.Replace(" {0}", string.Empty) : string.Format(template, subject);
    }

    private static string? GetPlayedCardName(GameAction? action)
    {
        if (action is not PlayCardAction playCardAction)
            return null;

        CardModel? card = playCardAction.NetCombatCard.ToCardModelOrNull();
        if (card != null)
            return card.Title;

        try
        {
            return ModelDb.GetById<CardModel>(playCardAction.CardModelId).Title;
        }
        catch
        {
            return playCardAction.CardModelId.Entry;
        }
    }

    private static string? GetPotionName(GameAction? action)
    {
        if (action is not UsePotionAction potionAction)
            return null;

        return potionAction.Player.GetPotionAtSlotIndex((int)potionAction.PotionIndex)?.Title.GetFormattedText();
    }

    private static string? GetDiscardedPotionName(GameAction? action)
    {
        if (action is not DiscardPotionGameAction discardAction)
            return null;

        Player? player = FindField(typeof(DiscardPotionGameAction), "_player")?.GetValue(discardAction) as Player;
        object? slotIndexValue = FindField(typeof(DiscardPotionGameAction), "_potionSlotIndex")?.GetValue(discardAction);
        if (player == null || slotIndexValue is not uint slotIndex)
            return null;

        return player.GetPotionAtSlotIndex((int)slotIndex)?.Title.GetFormattedText();
    }
    private bool CanOperateNow(NCombatUi combatUi)
    {
        return ShouldShowHud(combatUi)
            && CanRestoreState()
            && !IsUiBlocking(combatUi)
            && _combatReplay != null;
    }

    private bool CanRestoreState()
    {
        if (!IsSinglePlayerCombat() || IsRestoring || _combatReplay == null)
            return false;

        if (RunManager.Instance.ActionQueueSet.IsEmpty)
            return true;

        GameAction? action = RunManager.Instance.ActionExecutor.CurrentlyRunningAction;
        return action != null && action.State == GameActionState.GatheringPlayerChoice;
    }

    private static bool IsSinglePlayerCombat()
    {
        return RunManager.Instance.IsSinglePlayerOrFakeMultiplayer
            && CombatManager.Instance.IsInProgress
            && NGame.Instance?.CurrentRunNode != null;
    }

    private static bool IsUiBlocking(NCombatUi combatUi)
    {
        if (IsSupportedChoiceUiActive(combatUi))
            return false;

        if (NOverlayStack.Instance != null && NOverlayStack.Instance.ScreenCount > 0)
            return true;

        if (NCapstoneContainer.Instance != null && NCapstoneContainer.Instance.InUse)
            return true;

        if (NTargetManager.Instance != null && NTargetManager.Instance.IsInSelection)
            return true;

        return combatUi.Hand.InCardPlay || combatUi.Hand.IsInCardSelection;
    }

    private bool MoveCurrentChoiceAnchorToDestinationIfNeeded(LinkedList<UndoSnapshot> source, LinkedList<UndoSnapshot> destination)
    {
        if (source.First?.Value is not UndoSnapshot snapshot
            || snapshot.ActionKind != UndoActionKind.PlayerChoice
            || source.First.Next == null
            || !IsCurrentStateAtChoiceAnchor(snapshot))
        {
            return false;
        }

        source.RemoveFirst();
        destination.AddFirst(snapshot);
        TrimSnapshots(destination);
        MainFile.Logger.Info("Skipped restoring the current player choice anchor because the choice UI is already active.");
        return true;
    }

    private bool IsCurrentStateAtChoiceAnchor(UndoSnapshot snapshot)
    {
        NCombatUi? combatUi = NCombatRoom.Instance?.Ui;
        GameAction? action = RunManager.Instance.ActionExecutor.CurrentlyRunningAction;
        return combatUi != null
            && action?.State == GameActionState.GatheringPlayerChoice
            && IsSupportedChoiceUiActive(combatUi)
            && snapshot.ReplayEventCount == GetCurrentReplayEventCount();
    }

    private static bool IsSupportedChoiceUiActive(NCombatUi combatUi)
    {
        if (NCombatRoom.Instance?.Ui != combatUi)
            return false;

        if (combatUi.Hand.IsInCardSelection)
            return true;

        return NOverlayStack.Instance?.Peek() is NChooseACardSelectionScreen;
    }

    private static void DismissSupportedChoiceUiIfPresent()
    {
        if (NOverlayStack.Instance?.Peek() is NChooseACardSelectionScreen choiceScreen)
            NOverlayStack.Instance.Remove(choiceScreen);

        NPlayerHand? hand = NCombatRoom.Instance?.Ui?.Hand;
        if (hand?.IsInCardSelection == true)
            InvokePrivateMethod(hand, "CancelHandSelectionIfNecessary");
    }

    private static bool ShouldCapture(GameAction action)
    {
        if (!IsSinglePlayerCombat())
            return false;

        if (!ActionQueueSet.IsGameActionPlayerDriven(action))
            return false;

        ulong? localNetId = LocalContext.NetId;
        return localNetId == null || action.OwnerId == localNetId.Value;
    }

    private static void WriteInteractionLog(string stage, string? extra = null)
    {
        try
        {
            CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
            Player? me = combatState == null ? null : LocalContext.GetMe(combatState);
            NCombatUi? ui = NCombatRoom.Instance?.Ui;
            NPlayerHand? hand = ui?.Hand;
            NEndTurnButton? endTurnButton = ui?.EndTurnButton;
            object? endTurnState = endTurnButton == null ? null : FindField(endTurnButton.GetType(), "_state")?.GetValue(endTurnButton);
            object? handDisabledRaw = hand == null ? null : FindField(hand.GetType(), "_isDisabled")?.GetValue(hand);
            string handDisabled = handDisabledRaw is bool disabled ? disabled.ToString() : "null";
            List<string> hitboxes = [];
            if (hand != null)
            {
                foreach (Node child in hand.CardHolderContainer.GetChildren())
                {
                    if (child is NHandCardHolder holder)
                        hitboxes.Add(holder.Hitbox.IsEnabled ? "1" : "0");
                }
            }

            string message = $"{stage}"
                + $" | combatSide={(combatState == null ? "null" : combatState.CurrentSide)}"
                + $" round={(combatState == null ? "null" : combatState.RoundNumber)}"
                + $" executorPaused={RunManager.Instance.ActionExecutor.IsPaused}"
                + $" currentAction={(RunManager.Instance.ActionExecutor.CurrentlyRunningAction == null ? "null" : RunManager.Instance.ActionExecutor.CurrentlyRunningAction + "/" + RunManager.Instance.ActionExecutor.CurrentlyRunningAction.State)}"
                + $" syncState={RunManager.Instance.ActionQueueSynchronizer.CombatState}"
                + $" me={(me == null ? "null" : me.NetId)}"
                + $" queuePaused={(me == null ? "null" : RunManager.Instance.ActionQueueSet.ActionQueueIsPaused(me.NetId))}"
                + $" playerActionsDisabled={CombatManager.Instance.PlayerActionsDisabled}"
                + $" isPlayPhase={CombatManager.Instance.IsPlayPhase}"
                + $" endTurn1={CombatManager.Instance.EndingPlayerTurnPhaseOne}"
                + $" endTurn2={CombatManager.Instance.EndingPlayerTurnPhaseTwo}"
                + $" handMode={(hand == null ? "null" : hand.CurrentMode)}"
                + $" handInPlay={(hand == null ? "null" : hand.InCardPlay)}"
                + $" handSelecting={(hand == null ? "null" : hand.IsInCardSelection)}"
                + $" handDisabled={handDisabled}"
                + $" peeking={(hand == null ? "null" : hand.PeekButton.IsPeeking)}"
                + $" endTurnState={(endTurnState ?? "null")}"
                + $" endTurnEnabled={(endTurnButton == null ? "null" : endTurnButton.IsEnabled)}"
                + $" overlayCount={(NOverlayStack.Instance == null ? "null" : NOverlayStack.Instance.ScreenCount)}"
                + $" targeting={(NTargetManager.Instance == null ? "null" : NTargetManager.Instance.IsInSelection)}"
                + $" activeScreen={(ActiveScreenContext.Instance.GetCurrentScreen()?.GetType().Name ?? "null")}"
                + $" holders={(hand == null ? "null" : hand.CardHolderContainer.GetChildCount())}"
                + $" hitboxes={string.Join(",", hitboxes)}";

            if (!string.IsNullOrWhiteSpace(extra))
                message += $" | {extra}";

            UndoDebugLog.Write(message);
        }
        catch (Exception ex)
        {
            UndoDebugLog.Write($"log_failed | {stage} | {ex}");
        }
    }
    private void ClearHistoryInternal(string reason)
    {
        _combatReplay = null;
        if (_pastSnapshots.Count == 0 && _futureSnapshots.Count == 0)
            return;

        _pastSnapshots.Clear();
        _futureSnapshots.Clear();
        MainFile.Logger.Info($"Cleared undo history: {reason}.");
    }

    private void TrimSnapshots(LinkedList<UndoSnapshot> snapshots)
    {
        while (snapshots.Count > MaxSnapshots)
            snapshots.RemoveLast();
    }

    private void NotifyStateChanged()
    {
        StateChanged?.Invoke();
    }

    private void AppendReplayEvent(CombatReplayEvent replayEvent)
    {
        if (_combatReplay == null)
            return;

        if (_combatReplay.ActiveEventCount < _combatReplay.Events.Count)
        {
            MainFile.Logger.Info($"Truncating replay branch from {_combatReplay.Events.Count} to {_combatReplay.ActiveEventCount} events.");
            _combatReplay.TruncateToActiveCount();
        }

        _combatReplay.Events.Add(replayEvent);
        _combatReplay.ActiveEventCount = _combatReplay.Events.Count;
    }

    private bool CanRecordReplayEvent()
    {
        return !IsRestoring
            && _combatReplay != null
            && RunManager.Instance.IsSinglePlayerOrFakeMultiplayer
            && CombatManager.Instance.IsInProgress;
    }

    private int GetCurrentReplayEventCount()
    {
        return _combatReplay?.ActiveEventCount ?? 0;
    }

    private int GetReplayEventCountBeforeCurrentAction()
    {
        return Math.Max(0, GetCurrentReplayEventCount() - 1);
    }

    private static bool MatchesHotkey(InputEventKey keyEvent, Key key)
    {
        return keyEvent.Keycode == key || keyEvent.PhysicalKeycode == key || keyEvent.KeyLabel == key;
    }

    private static UndoCombatFullState CaptureCurrentCombatFullState()
    {
        RunState runState = RunManager.Instance.DebugOnlyGetState()
            ?? throw new InvalidOperationException("Run state was not available while capturing undo snapshot.");
        CombatState combatState = CombatManager.Instance.DebugOnlyGetState()
            ?? throw new InvalidOperationException("Combat state was not available while capturing undo snapshot.");

        return new UndoCombatFullState(
            CloneFullState(NetFullCombatState.FromRun(runState, null)),
            combatState.RoundNumber,
            combatState.CurrentSide,
            RunManager.Instance.ActionQueueSynchronizer.CombatState,
            RunManager.Instance.ActionQueueSet.NextActionId,
            RunManager.Instance.ActionQueueSynchronizer.NextHookId,
            RunManager.Instance.ChecksumTracker.NextId);
    }

    private static bool CanApplyFullStateInPlace(
        NetFullCombatState snapshot,
        RunState runState,
        CombatState combatState,
        out string reason)
    {
        if (snapshot.Players.Count != runState.Players.Count)
        {
            reason = "player_count_changed";
            return false;
        }

        IReadOnlyList<Creature> currentCreatures = combatState.Creatures;
        if (snapshot.Creatures.Count != currentCreatures.Count)
        {
            reason = "creature_count_changed";
            return false;
        }

        for (int i = 0; i < currentCreatures.Count; i++)
        {
            Creature current = currentCreatures[i];
            NetFullCombatState.CreatureState saved = snapshot.Creatures[i];
            if (saved.playerId != null)
            {
                if (current.Player?.NetId != saved.playerId.Value)
                {
                    reason = $"player_creature_mismatch_{i}";
                    return false;
                }
            }
            else if (current.Monster?.Id != saved.monsterId)
            {
                reason = $"monster_mismatch_{i}";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private static void RestorePlayers(RunState runState, CombatState combatState, NetFullCombatState snapshot)
    {
        foreach (NetFullCombatState.PlayerState playerState in snapshot.Players)
        {
            Player player = runState.GetPlayer(playerState.playerId)
                ?? throw new InvalidOperationException($"Could not map player snapshot {playerState.playerId}.");

            player.PlayerRng.LoadFromSerializable(playerState.rngSet);
            player.PlayerOdds.LoadFromSerializable(playerState.oddsSet);
            player.RelicGrabBag.LoadFromSerializable(playerState.relicGrabBag);
            player.Gold = playerState.gold;

            RestoreRelics(player, playerState);
            RestorePotions(player, playerState);
            RestorePlayerCombatState(player, combatState, playerState);
        }
    }

    private static void RestoreRelics(Player player, NetFullCombatState.PlayerState playerState)
    {
        foreach (RelicModel relic in player.Relics.ToList())
            player.RemoveRelicInternal(relic, true);

        foreach (NetFullCombatState.RelicState relicState in playerState.relics)
            player.AddRelicInternal(RelicModel.FromSerializable(relicState.relic), -1, true);
    }

    private static void RestorePotions(Player player, NetFullCombatState.PlayerState playerState)
    {
        int maxPotionDelta = playerState.maxPotionCount - player.MaxPotionCount;
        if (maxPotionDelta > 0)
            player.AddToMaxPotionCount(maxPotionDelta);
        else if (maxPotionDelta < 0)
            player.SubtractFromMaxPotionCount(-maxPotionDelta);

        for (int i = 0; i < player.MaxPotionCount; i++)
        {
            PotionModel? potion = player.GetPotionAtSlotIndex(i);
            if (potion != null)
                player.DiscardPotionInternal(potion, true);
        }

        for (int i = 0; i < playerState.potions.Count; i++)
        {
            player.AddPotionInternal(
                PotionModel.FromSerializable(new SerializablePotion
                {
                    Id = playerState.potions[i].id,
                    SlotIndex = i
                }),
                i,
                true);
        }
    }

    private static void RestorePlayerCombatState(Player player, CombatState combatState, NetFullCombatState.PlayerState playerState)
    {
        PlayerCombatState playerCombatState = player.PlayerCombatState
            ?? throw new InvalidOperationException($"Player {player.NetId} has no combat state.");

        foreach (CardModel card in playerCombatState.AllCards.ToList())
        {
            card.HasBeenRemovedFromState = true;
            if (combatState.ContainsCard(card))
                combatState.RemoveCard(card);
        }

        foreach (CardPile pile in playerCombatState.AllPiles)
            pile.Clear(true);

        Dictionary<PileType, NetFullCombatState.CombatPileState> pilesByType = playerState.piles.ToDictionary(static pile => pile.pileType);
        foreach (PileType pileType in CombatPileOrder)
        {
            CardPile pile = CardPile.Get(pileType, player)
                ?? throw new InvalidOperationException($"Pile {pileType} was not available for player {player.NetId}.");

            if (pilesByType.TryGetValue(pileType, out NetFullCombatState.CombatPileState pileState))
            {
                foreach (NetFullCombatState.CardState cardState in pileState.cards)
                {
                    CardModel card = CardModel.FromSerializable(cardState.card);
                    combatState.AddCard(card, player);
                    RestoreCardState(card, cardState);
                    pile.AddInternal(card, -1, true);
                }
            }

            pile.InvokeContentsChanged();
        }

        playerCombatState.Energy = playerState.energy;
        playerCombatState.Stars = playerState.stars;
        RestoreOrbQueue(player, playerState);
        playerCombatState.RecalculateCardValues();
    }

    private static void RestoreCardState(CardModel card, NetFullCombatState.CardState cardState)
    {
        HashSet<CardKeyword> desiredKeywords = cardState.keywords != null
            ? [.. cardState.keywords]
            : [];

        foreach (CardKeyword keyword in card.Keywords.ToList())
        {
            if (!desiredKeywords.Contains(keyword))
                card.RemoveKeyword(keyword);
        }

        foreach (CardKeyword keyword in desiredKeywords)
        {
            if (!card.Keywords.Contains(keyword))
                card.AddKeyword(keyword);
        }

        if (card.Affliction != null)
            card.ClearAfflictionInternal();

        if (cardState.affliction != null)
        {
            card.AfflictInternal(
                ModelDb.GetById<AfflictionModel>(cardState.affliction).ToMutable(),
                cardState.afflictionCount);
        }
    }

    private static void RestoreOrbQueue(Player player, NetFullCombatState.PlayerState playerState)
    {
        OrbQueue orbQueue = player.PlayerCombatState!.OrbQueue;
        orbQueue.Clear();
        orbQueue.AddCapacity(Math.Max(player.BaseOrbSlotCount, playerState.orbs.Count));

        for (int i = 0; i < playerState.orbs.Count; i++)
        {
            OrbModel orb = ModelDb.GetById<OrbModel>(playerState.orbs[i].id).ToMutable();
            orb.Owner = player;
            orbQueue.Insert(i, orb);
        }
    }

    private static void RestoreActionSynchronizationState(ActionSynchronizerCombatState targetState)
    {
        ActionQueueSynchronizer synchronizer = RunManager.Instance.ActionQueueSynchronizer;
        ActionQueueSet actionQueueSet = RunManager.Instance.ActionQueueSet;
        if (targetState == ActionSynchronizerCombatState.PlayPhase)
        {
            synchronizer.SetCombatState(ActionSynchronizerCombatState.NotPlayPhase);
            synchronizer.SetCombatState(ActionSynchronizerCombatState.PlayPhase);
            actionQueueSet.UnpauseAllPlayerQueues();
            RunManager.Instance.ActionExecutor.Unpause();
            return;
        }
        synchronizer.SetCombatState(ActionSynchronizerCombatState.PlayPhase);
        synchronizer.SetCombatState(targetState);
        if (targetState == ActionSynchronizerCombatState.NotPlayPhase || targetState == ActionSynchronizerCombatState.EndTurnPhaseOne)
            actionQueueSet.PauseAllPlayerQueues();
    }
    private void EnsurePlayerChoiceUndoAnchor(UndoSnapshot restoredSnapshot)
    {
        GameAction? action = RunManager.Instance.ActionExecutor.CurrentlyRunningAction;
        if (action?.State != GameActionState.GatheringPlayerChoice)
            return;

        int replayEventCount = GetCurrentReplayEventCount();
        UndoSnapshot? existing = _pastSnapshots.First?.Value;
        if (existing != null
            && existing.ActionKind == UndoActionKind.PlayerChoice
            && existing.ReplayEventCount == replayEventCount)
        {
            return;
        }

        UndoSnapshot anchor = new(
            CaptureCurrentCombatFullState(),
            replayEventCount,
            UndoActionKind.PlayerChoice,
            _nextSequenceId++,
            restoredSnapshot.ActionLabel);

        _pastSnapshots.AddFirst(anchor);
        TrimSnapshots(_pastSnapshots);
        MainFile.Logger.Info($"Re-armed player choice undo anchor. ReplayEvents={anchor.ReplayEventCount}, UndoCount={_pastSnapshots.Count}");
    }

    private static void RebuildActionQueues(IReadOnlyList<Player> players)
    {
        ActionQueueSet actionQueueSet = RunManager.Instance.ActionQueueSet;
        Type queueSetType = actionQueueSet.GetType();
        if (FindField(queueSetType, "_actionQueues")?.GetValue(actionQueueSet) is not System.Collections.IList queues)
            throw new InvalidOperationException("Could not access ActionQueueSet._actionQueues.");

        queues.Clear();
        if (FindField(queueSetType, "_actionsWaitingForResumption")?.GetValue(actionQueueSet) is System.Collections.IList waiting)
            waiting.Clear();

        Type? queueType = queueSetType.GetNestedType("ActionQueue", BindingFlags.NonPublic);
        if (queueType == null)
            throw new InvalidOperationException("Could not access ActionQueueSet.ActionQueue type.");

        FieldInfo? actionsField = FindField(queueType, "actions");
        FieldInfo? ownerIdField = FindField(queueType, "ownerId");
        FieldInfo? cancellingPlayField = FindField(queueType, "isCancellingPlayCardActions");
        FieldInfo? cancellingDrivenField = FindField(queueType, "isCancellingPlayerDrivenCombatActions");
        FieldInfo? cancellingCombatField = FindField(queueType, "isCancellingCombatActions");
        FieldInfo? pausedField = FindField(queueType, "isPaused");
        if (actionsField == null || ownerIdField == null || cancellingCombatField == null || pausedField == null)
            throw new InvalidOperationException("Could not access ActionQueue fields.");

        foreach (Player player in players)
        {
            object queue = Activator.CreateInstance(queueType, true)
                ?? throw new InvalidOperationException("Could not create ActionQueue.");
            actionsField.SetValue(queue, new List<GameAction>());
            ownerIdField.SetValue(queue, player.NetId);
            cancellingPlayField?.SetValue(queue, false);
            cancellingDrivenField?.SetValue(queue, false);
            cancellingCombatField.SetValue(queue, false);
            pausedField.SetValue(queue, false);
            queues.Add(queue);
        }

        InvokePrivateMethod(actionQueueSet, "CheckIfQueuesEmpty");
    }

    private static void RebuildTransientCombatCaches(RunState runState)
    {
        RebuildNetCombatCardDb(runState.Players);
        RebuildPotionContainer(runState);
    }

    private static void RebuildNetCombatCardDb(IReadOnlyList<Player> players)
    {
        NetCombatCardDb db = NetCombatCardDb.Instance;
        InvokePrivateMethod(db, "OnCombatEnded", new object?[] { null });
        db.ClearCardsForTesting();
        db.StartCombat(players);
    }

    private static void RebuildPotionContainer(RunState runState)
    {
        NPotionContainer? potionContainer = NRun.Instance?.GlobalUi?.TopBar?.PotionContainer;
        if (potionContainer == null)
            return;

        if (GetPrivateFieldValue<System.Collections.IList>(potionContainer, "_holders") is { } holders)
        {
            foreach (object holderObject in holders)
            {
                if (holderObject is not NPotionHolder holder)
                    continue;

                if (holder.Potion is { } potionNode && GodotObject.IsInstanceValid(potionNode))
                {
                    potionNode.GetParent()?.RemoveChild(potionNode);
                    potionNode.QueueFree();
                }

                Node? popup = GetPrivateFieldValue<Node>(holder, "_popup");
                if (popup != null && GodotObject.IsInstanceValid(popup))
                {
                    popup.GetParent()?.RemoveChild(popup);
                    popup.QueueFree();
                }

                SetPrivatePropertyValue(holder, "Potion", null);
                SetPrivateFieldValue(holder, "_popup", null);
                SetPrivateFieldValue(holder, "_disabledUntilPotionRemoved", false);
                holder.Modulate = Colors.White;
            }
        }

        SetPrivateFieldValue(potionContainer, "_focusedHolder", null);
        potionContainer.Initialize(runState);
    }
    private static void RestoreCreatures(RunState runState, CombatState combatState, NetFullCombatState snapshot)
    {
        IReadOnlyList<Creature> creatures = combatState.Creatures;
        for (int i = 0; i < snapshot.Creatures.Count; i++)
            RestoreCreatureState(creatures[i], snapshot.Creatures[i]);

        foreach (Player player in runState.Players)
        {
            if (player.Creature.IsAlive)
                player.ActivateHooks();
            else
                player.DeactivateHooks();
        }
    }

    private static void RestoreCreatureState(Creature creature, NetFullCombatState.CreatureState saved)
    {
        creature.SetMaxHpInternal(saved.maxHp);
        creature.SetCurrentHpInternal(saved.currentHp);
        if (creature.Block < saved.block)
            creature.GainBlockInternal(saved.block - creature.Block);
        else if (creature.Block > saved.block)
            creature.LoseBlockInternal(creature.Block - saved.block);
        RestoreCreaturePowers(creature, saved);
    }
    private static void RestoreCreaturePowers(Creature creature, NetFullCombatState.CreatureState saved)
    {
        List<PowerModel> remainingCurrentPowers = creature.Powers.ToList();
        foreach (NetFullCombatState.PowerState powerState in saved.powers)
        {
            PowerModel? existingPower = remainingCurrentPowers.FirstOrDefault(power => power.Id == powerState.id);
            if (existingPower != null)
            {
                remainingCurrentPowers.Remove(existingPower);
                existingPower.SetAmount(powerState.amount, true);
                existingPower.AmountOnTurnStart = existingPower.Amount;
                continue;
            }
            PowerModel power = ModelDb.GetById<PowerModel>(powerState.id).ToMutable();
            power.ApplyInternal(creature, powerState.amount, true);
            power.AmountOnTurnStart = power.Amount;
        }
        foreach (PowerModel power in remainingCurrentPowers)
            power.RemoveInternal();
    }
    private static async Task RefreshCombatUiAsync(CombatState combatState)
    {
        foreach (Player player in combatState.Players)
            player.PlayerCombatState?.RecalculateCardValues();
        NormalizeCombatInteractionState(combatState);
        RebuildCombatUiCards(combatState);
        NCombatRoom? combatRoom = NCombatRoom.Instance;
        if (combatRoom != null)
        {
            Player? me = LocalContext.GetMe(combatState);
            ForceCombatUiInteractiveState(combatRoom.Ui, combatState, me);
            if (me != null)
                RefreshCombatPileCounts(combatRoom.Ui, me);
            foreach (Creature creature in combatState.Enemies)
            {
                NCreature? creatureNode = combatRoom.GetCreatureNode(creature);
                if (creatureNode != null)
                    await creatureNode.RefreshIntents();
            }
        }
        await WaitOneFrameAsync();
        if (NCombatRoom.Instance != null)
            ForceCombatUiInteractiveState(NCombatRoom.Instance.Ui, combatState, LocalContext.GetMe(combatState));
    }
    private static void NormalizeCombatInteractionState(CombatState combatState)
    {
        NTargetManager.Instance?.CancelTargeting();
        RunManager.Instance.HoveredModelTracker.OnLocalCardDeselected();
        RunManager.Instance.HoveredModelTracker.OnLocalCardUnhovered();
        ClearCombatManagerCollection("_playersReadyToEndTurn");
        ClearCombatManagerCollection("_playersReadyToBeginEnemyTurn");
        bool isPlayerTurn = combatState.CurrentSide == CombatSide.Player;
        SetCombatManagerProperty("IsPlayPhase", isPlayerTurn);
        SetCombatManagerProperty("IsEnemyTurnStarted", !isPlayerTurn);
        SetCombatManagerProperty("EndingPlayerTurnPhaseOne", false);
        SetCombatManagerProperty("EndingPlayerTurnPhaseTwo", false);
        SetCombatManagerProperty("PlayerActionsDisabled", !isPlayerTurn);
        Player? me = LocalContext.GetMe(combatState);
        if (me != null && isPlayerTurn)
            CombatManager.Instance.UndoReadyToEndTurn(me);
    }
    private static void RebuildCombatUiCards(CombatState combatState)
    {
        NCombatRoom? combatRoom = NCombatRoom.Instance;
        if (combatRoom == null)
            return;
        NCombatUi ui = combatRoom.Ui;
        ResetPlayerHandUi(ui.Hand);
        ClearPlayQueueUi(ui.PlayQueue);
        ClearNodeChildren(ui.PlayContainer);
        Player? me = LocalContext.GetMe(combatState);
        if (me == null)
            return;
        foreach (CardModel card in PileType.Hand.GetPile(me).Cards)
            ui.Hand.Add(CreateCardNode(card, PileType.Hand), -1);
        SnapHandHolders(ui.Hand);
        foreach (CardModel card in PileType.Play.GetPile(me).Cards)
            ui.AddToPlayContainer(CreateCardNode(card, PileType.Play));
    }
    private static void ResetPlayerHandUi(NPlayerHand hand)
    {
        InvokePrivateMethod(hand, "CancelHandSelectionIfNecessary");
        hand.CancelAllCardPlay();
        hand.PeekButton.SetPeeking(false);
        hand.PeekButton.Disable();
        ClearTween(hand, "_animInTween");
        ClearTween(hand, "_animOutTween");
        ClearTween(hand, "_animEnableTween");
        ClearTween(hand, "_selectedCardScaleTween");
        Node? currentCardPlay = GetPrivateFieldValue<Node>(hand, "_currentCardPlay");
        if (currentCardPlay != null && GodotObject.IsInstanceValid(currentCardPlay))
        {
            currentCardPlay.GetParent()?.RemoveChild(currentCardPlay);
            currentCardPlay.QueueFree();
        }
        GetPrivateFieldValue<System.Collections.IDictionary>(hand, "_holdersAwaitingQueue")?.Clear();
        GetPrivateFieldValue<System.Collections.IList>(hand, "_selectedCards")?.Clear();
        SetPrivateFieldValue(hand, "_currentCardPlay", null);
        SetPrivateFieldValue(hand, "_draggedHolderIndex", -1);
        SetPrivateFieldValue(hand, "_lastFocusedHolderIdx", -1);
        SetPrivateFieldValue(hand, "_currentMode", NPlayerHand.Mode.Play);
        SetPrivateFieldValue(hand, "_isDisabled", false);
        SetPrivatePropertyValue(hand, "FocusedHolder", null);
        hand.CardHolderContainer.FocusMode = Control.FocusModeEnum.All;
        hand.Position = GetStaticFieldValue<Vector2>(typeof(NPlayerHand), "_showPosition");
        hand.Modulate = Colors.White;
        ClearNodeChildren(hand.CardHolderContainer);
        ClearOptionalNodeChildren(hand, "%SelectedHandCardContainer");
        HideControl(hand, "%SelectModeBackstop", Control.MouseFilterEnum.Ignore);
        HideControl(hand, "%UpgradePreviewContainer");
        HideControl(hand, "%SelectionHeader");
    }
    private static void ClearPlayQueueUi(NCardPlayQueue playQueue)
    {
        GetPrivateFieldValue<System.Collections.IList>(playQueue, "_playQueue")?.Clear();
        ClearNodeChildren(playQueue);
    }
    private static void RefreshCombatPileCounts(NCombatUi ui, Player player)
    {
        RefreshCombatPileCount(ui.DrawPile, PileType.Draw.GetPile(player).Cards.Count);
        RefreshCombatPileCount(ui.DiscardPile, PileType.Discard.GetPile(player).Cards.Count);
        RefreshCombatPileCount(ui.ExhaustPile, PileType.Exhaust.GetPile(player).Cards.Count);
    }
    private static void RefreshCombatPileCount(Node pileNode, int count)
    {
        SetPrivateFieldValue(pileNode, "_currentCount", count);
        object? countLabel = GetPrivateFieldValue<object>(pileNode, "_countLabel");
        countLabel?.GetType().GetMethod("SetTextAutoSize", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(countLabel, [count.ToString()]);
    }
    private static void ForceCombatUiInteractiveState(NCombatUi ui, CombatState combatState, Player? me)
    {
        SetPrivateFieldValue(ui.Hand, "_combatState", combatState);
        SetHandPresentation(ui.Hand, combatState.CurrentSide == CombatSide.Player && me != null && me.Creature.IsAlive);
        ui.Hand.EnableControllerNavigation();
        ui.Hand.ForceRefreshCardIndices();
        SnapHandHolders(ui.Hand);
        ui.EndTurnButton.Initialize(combatState);
        ForceEndTurnButtonState(ui.EndTurnButton, combatState, me);
        WriteInteractionLog("force_ui_interactive", $"side={combatState.CurrentSide} me={(me == null ? "null" : me.NetId)}");
    }
    private static void SetHandPresentation(NPlayerHand hand, bool shouldBeEnabled)
    {
        ClearTween(hand, "_animInTween");
        ClearTween(hand, "_animOutTween");
        ClearTween(hand, "_animEnableTween");
        hand.Position = shouldBeEnabled
            ? GetStaticFieldValue<Vector2>(typeof(NPlayerHand), "_showPosition")
            : GetStaticFieldValue<Vector2>(typeof(NPlayerHand), "_disablePosition");
        hand.Modulate = shouldBeEnabled
            ? Colors.White
            : GetStaticFieldValue<Color>(typeof(NPlayerHand), "_disableModulate");
        SetPrivateFieldValue(hand, "_isDisabled", !shouldBeEnabled);
        hand.CardHolderContainer.FocusMode = Control.FocusModeEnum.All;
    }
    private static void ForceEndTurnButtonState(NEndTurnButton endTurnButton, CombatState combatState, Player? me)
    {
        ClearTween(endTurnButton, "_positionTween");
        ClearTween(endTurnButton, "_hoverTween");
        ClearTween(endTurnButton, "_glowEnableTween");
        ClearTween(endTurnButton, "_glowVfxTween");
        FieldInfo? stateField = FindField(endTurnButton.GetType(), "_state");
        if (combatState.CurrentSide == CombatSide.Player && me != null && me.Creature.IsAlive)
        {
            endTurnButton.Position = GetPrivatePropertyValue<Vector2>(endTurnButton, "ShowPos");
            if (stateField != null)
                stateField.SetValue(endTurnButton, Enum.ToObject(stateField.FieldType, 0));
            InvokePrivateMethod(endTurnButton, "AfterPlayerUnendedTurn", me);
        }
        else
        {
            endTurnButton.Position = GetPrivatePropertyValue<Vector2>(endTurnButton, "HidePos");
            if (stateField != null)
                stateField.SetValue(endTurnButton, Enum.ToObject(stateField.FieldType, 2));
        }
        endTurnButton.RefreshEnabled();
    }
    private static void SnapHandHolders(NPlayerHand hand)
    {
        foreach (Node child in hand.CardHolderContainer.GetChildren())
        {
            if (child is not NHandCardHolder holder)
                continue;
            holder.SetDefaultTargets();
            holder.Position = holder.TargetPosition;
            holder.SetAngleInstantly(holder.TargetAngle);
            object? targetScale = FindField(holder.GetType(), "_targetScale")?.GetValue(holder);
            holder.SetScaleInstantly(targetScale is Vector2 scale ? scale : Vector2.One);
            holder.SetClickable(true);
            holder.FocusMode = Control.FocusModeEnum.All;
            holder.Hitbox.SetEnabled(true);
            holder.Hitbox.MouseFilter = Control.MouseFilterEnum.Stop;
            holder.ZIndex = 0;
        }
    }
    private static void ClearTween(object instance, string fieldName)
    {
        if (GetPrivateFieldValue<Tween>(instance, fieldName) is { } tween)
            tween.Kill();
    }
    private static T GetStaticFieldValue<T>(Type type, string fieldName)
    {
        object? value = FindField(type, fieldName)?.GetValue(null);
        return value is T typed ? typed : default!;
    }
    private static T GetPrivatePropertyValue<T>(object instance, string propertyName)
    {
        object? value = FindProperty(instance.GetType(), propertyName)?.GetValue(instance);
        return value is T typed ? typed : default!;
    }
    private static NCard CreateCardNode(CardModel card, PileType pileType)
    {
        NCard cardNode = NCard.Create(card, ModelVisibility.Visible);
        cardNode.UpdateVisuals(pileType, CardPreviewMode.Normal);
        return cardNode;
    }

    private static void ClearNodeChildren(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            node.RemoveChild(child);
            child.QueueFree();
        }
    }

    private static void ClearOptionalNodeChildren(Node node, string path)
    {
        Node? child = node.GetNodeOrNull<Node>(path);
        if (child != null)
            ClearNodeChildren(child);
    }

    private static void HideControl(Node node, string path, Control.MouseFilterEnum mouseFilter = Control.MouseFilterEnum.Ignore)
    {
        Control? control = node.GetNodeOrNull<Control>(path);
        if (control == null)
            return;

        control.Visible = false;
        control.MouseFilter = mouseFilter;
    }

    private static void ClearCombatManagerCollection(string fieldName)
    {
        object? collection = FindField(typeof(CombatManager), fieldName)?.GetValue(CombatManager.Instance);
        collection?.GetType().GetMethod("Clear", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(collection, null);
    }

    private static void SetCombatManagerProperty(string propertyName, object? value)
    {
        FindProperty(typeof(CombatManager), propertyName)?.SetValue(CombatManager.Instance, value);
    }

    private static T? GetPrivateFieldValue<T>(object instance, string fieldName) where T : class
    {
        return FindField(instance.GetType(), fieldName)?.GetValue(instance) as T;
    }

    private static void SetPrivateFieldValue(object instance, string fieldName, object? value)
    {
        FindField(instance.GetType(), fieldName)?.SetValue(instance, value);
    }

    private static void SetPrivatePropertyValue(object instance, string propertyName, object? value)
    {
        FindProperty(instance.GetType(), propertyName)?.SetValue(instance, value);
    }

    private static object? InvokePrivateMethod(object instance, string methodName, params object?[]? args)
    {
        return FindMethod(instance.GetType(), methodName)?.Invoke(instance, args);
    }

    private static FieldInfo? FindField(Type? type, string name)
    {
        while (type != null)
        {
            FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
                return field;

            type = type.BaseType;
        }

        return null;
    }

    private static PropertyInfo? FindProperty(Type? type, string name)
    {
        while (type != null)
        {
            PropertyInfo? property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null)
                return property;

            type = type.BaseType;
        }

        return null;
    }

    private static MethodInfo? FindMethod(Type? type, string name)
    {
        while (type != null)
        {
            MethodInfo? method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method != null)
                return method;

            type = type.BaseType;
        }

        return null;
    }
private static SerializableRun CloneRun(SerializableRun run)
    {
        string json = SaveManager.ToJson(run);
        ReadSaveResult<SerializableRun> result = SaveManager.FromJson<SerializableRun>(json);
        if (!result.Success || result.SaveData == null)
        {
            throw new InvalidOperationException(
                $"Failed to clone SerializableRun. Status={result.Status} Msg={result.ErrorMessage}");
        }

        return result.SaveData;
    }

    private static NetFullCombatState CloneFullState(NetFullCombatState state)
    {
        PacketWriter writer = new() { WarnOnGrow = false };
        writer.Write(state);
        writer.ZeroByteRemainder();

        byte[] buffer = new byte[writer.BytePosition];
        Array.Copy(writer.Buffer, buffer, writer.BytePosition);

        PacketReader reader = new();
        reader.Reset(buffer);
        return reader.Read<NetFullCombatState>();
    }

    private static async Task WaitForExecutorToUnpauseAsync()
    {
        while (RunManager.Instance.ActionExecutor.IsPaused)
            await WaitOneFrameAsync();
    }

    private static async Task WaitForChoiceUiReadyAsync()
    {
        for (int i = 0; i < 240; i++)
        {
            if (IsChoiceUiReady())
                return;

            await WaitOneFrameAsync();
        }
    }

    private static bool IsChoiceUiReady()
    {
        if (NOverlayStack.Instance?.ScreenCount > 0)
            return true;

        if (NCombatRoom.Instance?.Ui?.Hand.IsInCardSelection == true)
            return true;

        if (NTargetManager.Instance?.IsInSelection == true)
            return true;

        return false;
    }
    private static async Task WaitForReplayToSettleAsync()
    {
        while (true)
        {
            await RunManager.Instance.ActionExecutor.FinishedExecutingActions();

            if (RunManager.Instance.ActionQueueSet.IsEmpty)
                break;

            GameAction? action = RunManager.Instance.ActionExecutor.CurrentlyRunningAction;
            if (action?.State == GameActionState.GatheringPlayerChoice)
                break;

            await WaitOneFrameAsync();
        }

        await WaitOneFrameAsync();
    }

    private static async Task WaitOneFrameAsync()
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
            throw new InvalidOperationException("Main loop is not a SceneTree.");

        await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
    }
}








































