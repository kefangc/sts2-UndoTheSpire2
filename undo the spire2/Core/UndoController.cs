// Coordinates undo/redo history and restore transactions.
// Capture/restore details should live in dedicated services; this type is the orchestrator.
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Enchantments;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Models;
using MegaCrit.Sts2.Core.Entities.Orbs;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Orbs;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Nodes.Vfx.Cards;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;
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
    private UndoChoiceSpec? _pendingChoiceSpec;
    private UndoChoiceSpec? _lastResolvedChoiceSpec;
    private AbstractModel? _pendingHandChoiceSource;
    private UndoChoiceResultKey? _lastResolvedChoiceResultKey;
    private UndoSyntheticChoiceSession? _syntheticChoiceSession;
    private bool _blockUndoUntilNextPlayerTurn;
    private int _blockedTurnRound = -1;
    private int _queuedHistoryMoves;
    private string? _lastRestoreFailureReason;
    private RestoreCapabilityReport _lastRestoreCapabilityReport = RestoreCapabilityReport.SupportedReport();
    private static string? _lastInteractionStage;
    private static int _hiddenChoiceAnchorSkipCount;
    private readonly Dictionary<string, int> _firstInSeriesPlayCountOverrides = [];
    private int _firstInSeriesPlayCountOverrideRound = -1;
    private CombatSide _firstInSeriesPlayCountOverrideSide;
    private bool _hasFirstInSeriesPlayCountOverride;
    private static readonly Dictionary<ulong, Vector2> SelectedHandContainerDefaultPositions = [];
    private static readonly Dictionary<ulong, Vector2> SelectedHandContainerDefaultScales = [];

    public event Action? StateChanged;

    public bool IsRestoring { get; private set; }

    public bool HasUndo => GetVisibleSnapshot(_pastSnapshots) != null;

    public bool HasRedo => GetVisibleSnapshot(_futureSnapshots) != null;

    public string UndoLabel => GetVisibleSnapshot(_pastSnapshots)?.ActionLabel ?? string.Empty;

    public string RedoLabel => GetVisibleSnapshot(_futureSnapshots)?.ActionLabel ?? string.Empty;
    public void OnCombatUiActivated(NCombatUi combatUi, CombatState combatState)
    {
        CaptureSelectedHandContainerDefaults(combatUi.Hand);
        NotifyStateChanged();
    }

    public void OnCombatUiDeactivated(NCombatUi combatUi)
    {
        NotifyStateChanged();
    }

    private UndoSnapshot? GetVisibleSnapshot(LinkedList<UndoSnapshot> snapshots)
    {
        LinkedListNode<UndoSnapshot>? node = snapshots.First;
        if (!UndoModSettings.EnableChoiceUndo && !UndoModSettings.EnableUnifiedEffectMode)
        {
            while (node != null && node.Value.IsChoiceAnchor)
                node = node.Next;

        return node?.Value;
    }

    private void DiscardHiddenChoiceAnchors(LinkedList<UndoSnapshot> snapshots, string operation)
    {
        if (UndoModSettings.EnableChoiceUndo || UndoModSettings.EnableUnifiedEffectMode)
            return;

        while (snapshots.First?.Value is UndoSnapshot snapshot && snapshot.IsChoiceAnchor)
        {
            snapshots.RemoveFirst();
            _hiddenChoiceAnchorSkipCount++;
            MainFile.Logger.Info($"Skipped hidden choice anchor during {operation}. ReplayEvents={snapshot.ReplayEventCount}");
        }
    }

        return node?.Value;
    }

    private void BeginTurnTransitionBlock()
    {
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        _blockUndoUntilNextPlayerTurn = true;
        _blockedTurnRound = state?.RoundNumber ?? -1;
        WriteInteractionLog("turn_transition_block_started", $"round={_blockedTurnRound}");
        NotifyStateChanged();
    }

    private void ClearTurnTransitionBlock()
    {
        _blockUndoUntilNextPlayerTurn = false;
        _blockedTurnRound = -1;
    }
    private void ClearFirstInSeriesPlayCountOverrides()
    {
        _firstInSeriesPlayCountOverrides.Clear();
        _firstInSeriesPlayCountOverrideRound = -1;
        _firstInSeriesPlayCountOverrideSide = default;
        _hasFirstInSeriesPlayCountOverride = false;
    }

    private void ApplyFirstInSeriesPlayCountOverrides(UndoCombatFullState snapshot)
    {
        ClearFirstInSeriesPlayCountOverrides();
        _firstInSeriesPlayCountOverrideRound = snapshot.RoundNumber;
        _firstInSeriesPlayCountOverrideSide = snapshot.CurrentSide;
        _hasFirstInSeriesPlayCountOverride = true;

        foreach (UndoFirstInSeriesPlayCountState state in snapshot.FirstInSeriesPlayCounts)
            _firstInSeriesPlayCountOverrides[state.CreatureKey] = state.Count;
    }

    public void OnCombatHistoryCardPlayStarted(CombatState combatState, CardPlay cardPlay)
    {
        if (IsRestoring || !cardPlay.IsFirstInSeries)
            return;

        if (!_hasFirstInSeriesPlayCountOverride
            || combatState.RoundNumber != _firstInSeriesPlayCountOverrideRound
            || combatState.CurrentSide != _firstInSeriesPlayCountOverrideSide)
        {
            ClearFirstInSeriesPlayCountOverrides();
            _firstInSeriesPlayCountOverrideRound = combatState.RoundNumber;
            _firstInSeriesPlayCountOverrideSide = combatState.CurrentSide;
            _hasFirstInSeriesPlayCountOverride = true;
        }

        string? creatureKey = TryResolveCreatureKey(combatState.Creatures, cardPlay.Card.Owner?.Creature);
        if (string.IsNullOrWhiteSpace(creatureKey))
            return;

        _firstInSeriesPlayCountOverrides[creatureKey] = _firstInSeriesPlayCountOverrides.TryGetValue(creatureKey, out int existingCount)
            ? existingCount + 1
            : 1;
    }

    public bool TryOverrideEchoFormModifyCardPlayCount(EchoFormPower power, CardModel card, int playCount, out int result)
    {
        result = 0;
        if (power.Owner == null)
            return false;

        CombatState? combatState = power.CombatState ?? CombatManager.Instance.DebugOnlyGetState();
        if (combatState == null)
            return false;

        string? powerOwnerKey = TryResolveCreatureKey(combatState.Creatures, power.Owner);
        string? cardOwnerKey = TryResolveCreatureKey(combatState.Creatures, card.Owner?.Creature);
        if (string.IsNullOrWhiteSpace(powerOwnerKey) || powerOwnerKey != cardOwnerKey)
            return false;

        int playCountSoFar;
        if (_hasFirstInSeriesPlayCountOverride
            && combatState.RoundNumber == _firstInSeriesPlayCountOverrideRound
            && combatState.CurrentSide == _firstInSeriesPlayCountOverrideSide)
        {
            playCountSoFar = _firstInSeriesPlayCountOverrides.TryGetValue(powerOwnerKey, out int overrideCount)
                ? overrideCount
                : 0;
        }
        else
        {
            playCountSoFar = CombatManager.Instance.History.CardPlaysStarted.Count(entry =>
                entry.HappenedThisTurn(combatState)
                && entry.CardPlay.IsFirstInSeries
                && TryResolveCreatureKey(combatState.Creatures, entry.Actor) == powerOwnerKey);
        }

        result = playCountSoFar >= power.Amount ? playCount : playCount + 1;
        return true;
    }

    private bool IsUndoRedoTemporarilyBlocked(NCombatUi combatUi)
    {
        return false;
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
        ClearTurnTransitionBlock();
        ClearFirstInSeriesPlayCountOverrides();
        MainFile.Logger.Info("Initialized combat replay state for undo.");
        UndoDebugLog.Write($"combat replay initialized nextAction={RunManager.Instance.ActionQueueSet.NextActionId} nextHook={RunManager.Instance.ActionQueueSynchronizer.NextHookId}");
        NotifyStateChanged();
    }


    private bool EnsureCombatReplayInitialized()
    {
        if (_combatReplay != null)
            return true;

        if (!RunManager.Instance.IsSinglePlayerOrFakeMultiplayer || !CombatManager.Instance.IsInProgress || IsRestoring)
            return false;

        try
        {
            SerializableRun currentRun = RunManager.Instance.ToSave(null);
            OnCombatReplayInitialized(currentRun);
            UndoDebugLog.Write("combat replay lazily initialized from current run state");
            return _combatReplay != null;
        }
        catch (Exception ex)
        {
            UndoDebugLog.Write($"combat replay lazy init failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
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

    public void RegisterPendingChooseACardChoice(Player player, IReadOnlyList<CardModel> cards, bool canSkip)
    {
        if (!ShouldTrackLocalChoice(player))
            return;

        _pendingChoiceSpec = UndoChoiceSpec.CreateChooseACard(cards, canSkip);
    }

    public void RegisterPendingHandChoice(Player player, CardSelectorPrefs prefs, Func<CardModel, bool>? filter, AbstractModel? source)
    {
        if (!ShouldTrackLocalChoice(player))
            return;

        _pendingChoiceSpec = UndoChoiceSpec.CreateHandSelection(player, prefs, filter);
        _pendingHandChoiceSource = source;
    }

    public void RegisterPendingSimpleGridChoice(Player player, IReadOnlyList<CardModel> cards, CardSelectorPrefs prefs)
    {
        if (!ShouldTrackLocalChoice(player))
            return;

        _pendingChoiceSpec = UndoChoiceSpec.CreateSimpleGridSelection(player, cards, prefs);
    }

    public void OnPlayerChoiceResolved(Player player, NetPlayerChoiceResult result)
    {
        if (!ShouldTrackLocalChoice(player) || _lastResolvedChoiceSpec == null)
            return;

        UndoChoiceResultKey? branchKey = _lastResolvedChoiceSpec.TryMapReplayResult(result);
        if (branchKey == null)
            return;

        _lastResolvedChoiceResultKey = branchKey;
        MainFile.Logger.Info($"Captured choice branch key {branchKey} for {_lastResolvedChoiceSpec.Kind}.");
    }

    private static bool ShouldTrackLocalChoice(Player player)
    {
        if (!RunManager.Instance.IsSinglePlayerOrFakeMultiplayer)
            return false;

        ulong? localNetId = LocalContext.NetId;
        return localNetId == null || player.NetId == localNetId.Value;
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
        if (!ShouldCaptureAction(action))
        {
            UndoDebugLog.Write($"capture skipped kind={actionKind} type={action.GetType().Name} owner={action.OwnerId} localNetId={LocalContext.NetId?.ToString() ?? "null"}");
            return;
        }

        if (actionKind == UndoActionKind.EndTurn)
            BeginTurnTransitionBlock();

        TryCaptureSnapshot(actionKind, GetReplayEventCountBeforeCurrentAction(), DescribeAction(actionKind, action));
    }

    public void TryCapturePlayerChoice(GameAction action)
    {
        if (!ShouldCapturePlayerChoice(action, out string? skipReason))
        {
            UndoDebugLog.Write($"choice_capture_skipped:{skipReason ?? "unknown"} type={action.GetType().Name} owner={action.OwnerId} localNetId={LocalContext.NetId?.ToString() ?? "null"} state={action.State}");
            return;
        }

        UndoChoiceSpec? choiceSpec = TakePendingChoiceSpec(action, clearWhenNotCaptured: false)
            ?? TryCaptureCurrentChoiceSpecFromUi()
            ?? TryCaptureChoiceSpecFromCurrentActionContext(action);
        if (choiceSpec == null)
        {
            UndoDebugLog.Write($"choice_capture_skipped:no_pending_spec type={action.GetType().Name} owner={action.OwnerId} localNetId={LocalContext.NetId?.ToString() ?? "null"} state={action.State}");
            return;
        }

        _lastResolvedChoiceSpec = choiceSpec;
        _lastResolvedChoiceResultKey = null;
        TryCaptureSnapshot(
            UndoActionKind.PlayerChoice,
            GetCurrentReplayEventCount(),
            DescribeAction(UndoActionKind.PlayerChoice, action),
            isChoiceAnchor: true,
            choiceSpec: choiceSpec);
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
        if (IsRestoring)
        {
            if (HasUndo)
                Undo();
            return;
        }

        if (!CanUndoNow(combatUi))
        {
            MainFile.Logger.Info($"Ignored undo request from {source}. HasUndo={HasUndo}, CanRestore={CanRestoreState()}, UiBlocked={IsUiBlocking(combatUi)}");
            UndoDebugLog.Write($"undo ignored source={source} hasUndo={HasUndo} canRestore={CanRestoreState()} uiBlocked={IsUiBlocking(combatUi)}");
            return;
        }

        MainFile.Logger.Info($"Undo requested from {source}. Latest={UndoLabel}");
        UndoDebugLog.Write($"undo requested source={source} latest={UndoLabel}");
        Undo();
    }

    public void TryHandleRedoRequest(NCombatUi combatUi, string source)
    {
        if (IsRestoring)
        {
            if (HasRedo)
                Redo();
            return;
        }

        if (!CanRedoNow(combatUi))
        {
            MainFile.Logger.Info($"Ignored redo request from {source}. HasRedo={HasRedo}, CanRestore={CanRestoreState()}, UiBlocked={IsUiBlocking(combatUi)}");
            UndoDebugLog.Write($"redo ignored source={source} hasRedo={HasRedo} canRestore={CanRestoreState()} uiBlocked={IsUiBlocking(combatUi)}");
            return;
        }

        MainFile.Logger.Info($"Redo requested from {source}. Latest={RedoLabel}");
        UndoDebugLog.Write($"redo requested source={source} latest={RedoLabel}");
        Redo();
    }

    public void Undo()
    {
        if (IsRestoring)
        {
            _queuedHistoryMoves--;
            return;
        }

        if (!HasUndo)
            return;

        TaskHelper.RunSafely(RestoreFromHistoryAsync(_pastSnapshots, _futureSnapshots, "undo"));
    }

    public void Redo()
    {
        if (IsRestoring)
        {
            _queuedHistoryMoves++;
            return;
        }

        if (!HasRedo)
            return;

        if (TryOpenChoiceRedoSession())
            return;

        TaskHelper.RunSafely(RestoreFromHistoryAsync(_futureSnapshots, _pastSnapshots, "redo"));
    }
    private void ProcessQueuedHistoryMoves()
    {
        if (IsRestoring || _queuedHistoryMoves == 0)
            return;

        if (_queuedHistoryMoves < 0)
        {
            if (!HasUndo)
            {
                _queuedHistoryMoves = 0;
                return;
            }

            _queuedHistoryMoves++;
            Undo();
            return;
        }

        if (!HasRedo)
        {
            _queuedHistoryMoves = 0;
            return;
        }

        _queuedHistoryMoves--;
        Redo();
    }

    private bool TryOpenChoiceRedoSession()
    {
        if (!UndoModSettings.EnableChoiceUndo || !UndoModSettings.EnableUnifiedEffectMode)
            return false;

        UndoSnapshot? anchorSnapshot = GetCurrentChoiceAnchorSnapshot();
        UndoSnapshot? branchSnapshot = _futureSnapshots.First?.Value;
        if (anchorSnapshot?.ChoiceSpec?.SupportsSyntheticRestore != true
            || !IsCurrentStateAtChoiceAnchor(anchorSnapshot)
            || branchSnapshot == null
            || branchSnapshot.IsChoiceAnchor)
        {
            return false;
        }

        OpenSyntheticChoiceSession(anchorSnapshot, branchSnapshot);
        NotifyStateChanged();
        return true;
    }
    private static bool HasRestorableChoiceSession(UndoSelectionSessionState? selectionSession)
    {
        return selectionSession != null
            && (selectionSession.SupportedChoiceUiActive
                || selectionSession.HandSelectionActive
                || selectionSession.OverlaySelectionActive);
    }

    private static bool IsHiddenChoiceBoundarySnapshot(UndoSnapshot snapshot)
    {
        return !UndoModSettings.EnableChoiceUndo
            && snapshot.ActionKind == UndoActionKind.EndTurn
            && HasRestorableChoiceSession(snapshot.CombatState.SelectionSessionState);
    }

    private static bool IsAlwaysHiddenUndoBoundarySnapshot(UndoSnapshot snapshot)
    {
        return snapshot.ActionKind == UndoActionKind.EndTurn
            && HasRestorableChoiceSession(snapshot.CombatState.SelectionSessionState);
    }


    private static bool IsCurrentEndTurnChoiceBoundarySnapshot(UndoSnapshot snapshot)
    {
        if (snapshot.ActionKind != UndoActionKind.EndTurn)
            return false;

        GameAction? action = RunManager.Instance.ActionExecutor.CurrentlyRunningAction;
        if (action?.State != GameActionState.GatheringPlayerChoice)
            return false;

        return TryCaptureChoiceSpecFromActionContext(snapshot) != null;
    }
    private static bool IsAlwaysHiddenUndoChoiceAnchorNode(LinkedListNode<UndoSnapshot>? node)
    {
        return node?.Value.IsChoiceAnchor == true
            && node.Next?.Value is UndoSnapshot nextSnapshot
            && IsAlwaysHiddenUndoBoundarySnapshot(nextSnapshot);
    }
    private static bool IsCurrentHiddenEndTurnChoiceAnchor(UndoSnapshot? snapshot)
    {
        return !UndoModSettings.EnableChoiceUndo
            && snapshot?.IsChoiceAnchor == true
            && snapshot.ChoiceSpec?.Kind == UndoChoiceKind.HandSelection
            && snapshot.ChoiceSpec.SourcePileType == PileType.Hand
            && snapshot.ActionKind == UndoActionKind.PlayerChoice;
    }
    private static bool ShouldSkipHiddenUndoSnapshot(
        LinkedListNode<UndoSnapshot> node,
        UndoSnapshot? currentChoiceAnchor)
    {
        if (UndoModSettings.EnableChoiceUndo)
            return false;
        if (IsCurrentHiddenEndTurnChoiceAnchor(currentChoiceAnchor))
        {
            int currentReplayEventCount = currentChoiceAnchor!.ReplayEventCount;
            if (node.Value.ReplayEventCount < currentReplayEventCount)
                return false;
            return node.Value.IsChoiceAnchor || node.Value.ActionKind == UndoActionKind.EndTurn;
        }
        return IsAlwaysHiddenUndoChoiceAnchorNode(node)
            || IsAlwaysHiddenUndoBoundarySnapshot(node.Value)
            || node.Value.IsChoiceAnchor;
    }

    private LinkedListNode<UndoSnapshot>? FindPreferredRedoSnapshotNode(LinkedList<UndoSnapshot> source)
    {
        LinkedListNode<UndoSnapshot>? node = source.First;
        if (node == null)
            return null;

        if (node.Value.IsChoiceAnchor && node.Value.CombatState.ActionKernelState.PausedChoiceState != null)
            return node;

        if (node.Value.ActionKind != UndoActionKind.EndTurn)
            return node;

        LinkedListNode<UndoSnapshot>? preferredStableNode = node;
        for (LinkedListNode<UndoSnapshot>? current = node; current != null; current = current.Next)
        {
            if (current.Value.IsChoiceAnchor && current.Value.CombatState.ActionKernelState.PausedChoiceState != null)
                return current;

            if (current.Value.ActionKind != UndoActionKind.EndTurn)
                break;

            UndoSelectionSessionState? selectionSession = current.Value.CombatState.SelectionSessionState;
            if (HasRestorableChoiceSession(selectionSession) || current.Value.CombatState.ActionKernelState.PausedChoiceState != null)
                return current;

            if (current.Value.CombatState.CurrentSide == CombatSide.Player
                && current.Value.CombatState.SynchronizerCombatState == ActionSynchronizerCombatState.PlayPhase)
            {
                preferredStableNode = current;
            }
        }

        return preferredStableNode;
    }

    private UndoSnapshot? FindSyntheticChoiceTemplateSnapshot(UndoSnapshot? preferredTemplate, int replayEventCount)
    {
        if (preferredTemplate?.ChoiceResultKey != null)
            return preferredTemplate;

        if (preferredTemplate != null
            && !preferredTemplate.IsChoiceAnchor
            && _lastResolvedChoiceResultKey != null
            && preferredTemplate.ReplayEventCount >= replayEventCount)
        {
            return new UndoSnapshot(
                preferredTemplate.CombatState,
                preferredTemplate.ReplayEventCount,
                preferredTemplate.ActionKind,
                preferredTemplate.SequenceId,
                preferredTemplate.ActionLabel,
                preferredTemplate.IsChoiceAnchor,
                preferredTemplate.ChoiceSpec,
                _lastResolvedChoiceResultKey);
        }

        foreach (UndoSnapshot snapshot in _futureSnapshots)
        {
            if (!snapshot.IsChoiceAnchor && snapshot.ChoiceResultKey != null && snapshot.ReplayEventCount >= replayEventCount)
                return snapshot;
        }

        foreach (UndoSnapshot snapshot in _pastSnapshots)
        {
            if (!snapshot.IsChoiceAnchor && snapshot.ChoiceResultKey != null && snapshot.ReplayEventCount >= replayEventCount)
                return snapshot;
        }

        return preferredTemplate;
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

        UndoSnapshot? currentChoiceAnchor = GetCurrentChoiceAnchorSnapshot();
        bool movedCurrentChoiceAnchor = MoveCurrentChoiceAnchorToDestinationIfNeeded(source, destination);
        if (operation == "undo")
        {
            while (source.First != null
                && ShouldSkipHiddenUndoSnapshot(source.First, currentChoiceAnchor))
            {
                UndoSnapshot skippedSnapshot = source.First.Value;
                source.RemoveFirst();
                destination.AddFirst(skippedSnapshot);
                TrimSnapshots(destination);
                MainFile.Logger.Info($"Skipped hidden undo boundary during undo. ReplayEvents={skippedSnapshot.ReplayEventCount}, Kind={skippedSnapshot.ActionKind}, ChoiceAnchor={skippedSnapshot.IsChoiceAnchor}");
            }
        }
        List<UndoSnapshot> skippedRedoSnapshots = [];
        if (operation == "redo")
        {
            LinkedListNode<UndoSnapshot>? preferredRedoNode = FindPreferredRedoSnapshotNode(source);
            while (source.First != null && source.First != preferredRedoNode)
            {
                skippedRedoSnapshots.Add(source.First.Value);
                source.RemoveFirst();
            }
        }

        if (source.First?.Value is not UndoSnapshot snapshot)
            return;

        UndoSnapshot? baseSnapshot = source.First.Next?.Value;
        UndoSnapshot? currentSnapshot = null;

        source.RemoveFirst();
        if (!movedCurrentChoiceAnchor)
        {
            bool isCurrentChoiceAnchor = currentChoiceAnchor != null && IsCurrentStateAtChoiceAnchor(currentChoiceAnchor);
            currentSnapshot = new UndoSnapshot(
                CaptureCurrentCombatFullState(),
                GetCurrentReplayEventCount(),
                snapshot.ActionKind,
                _nextSequenceId++,
                snapshot.ActionLabel,
                isChoiceAnchor: isCurrentChoiceAnchor,
                choiceSpec: isCurrentChoiceAnchor ? currentChoiceAnchor?.ChoiceSpec : null,
                choiceResultKey: isCurrentChoiceAnchor ? null : (snapshot.IsChoiceAnchor ? _lastResolvedChoiceResultKey : null));

            destination.AddFirst(currentSnapshot);
            TrimSnapshots(destination);
        }

        IsRestoring = true;
        ClearTurnTransitionBlock();
        NotifyStateChanged();

        try
        {
            string restoreMode = await RestoreSnapshotAsync(snapshot, baseSnapshot, currentSnapshot);
            _combatReplay.ActiveEventCount = snapshot.ReplayEventCount;
            EnsurePlayerChoiceUndoAnchor(snapshot);
            MainFile.Logger.Info($"{operation} completed. Restored={snapshot.ActionLabel}, ReplayEvents={snapshot.ReplayEventCount}, Mode={restoreMode}");
        }
                catch (Exception ex)
        {
            if (currentSnapshot != null)
                destination.RemoveFirst();
            source.AddFirst(snapshot);
            for (int i = skippedRedoSnapshots.Count - 1; i >= 0; i--)
                source.AddFirst(skippedRedoSnapshots[i]);
            string failure = _lastRestoreFailureReason ?? DescribeException(ex);
            MainFile.Logger.Error($"Failed to {operation}: {failure}");
        }
        finally
        {
            IsRestoring = false;
            NotifyStateChanged();
            ProcessQueuedHistoryMoves();
        }
    }
    private async Task<string> RestoreSnapshotAsync(UndoSnapshot snapshot, UndoSnapshot? baseSnapshot, UndoSnapshot? branchSnapshot)
    {
        if (snapshot.IsChoiceAnchor)
        {
            UndoSnapshot? choiceBranchTemplate = branchSnapshot ?? FindSyntheticChoiceTemplateSnapshot(baseSnapshot, snapshot.ReplayEventCount);
            if (UndoModSettings.EnableUnifiedEffectMode)
            {
                if (await TryRestorePrimaryChoiceAsync(snapshot, choiceBranchTemplate))
                {
                    WriteInteractionLog("primary_restore", $"label={snapshot.ActionLabel} replayEvents={snapshot.ReplayEventCount} mode=choice_anchor");
                    return "primary_choice";
                }

                if (UndoModSettings.EnableChoiceUndo && snapshot.ChoiceSpec?.SupportsSyntheticRestore == true && await TryRestoreSyntheticChoiceAsync(snapshot, choiceBranchTemplate))
                {
                    WriteInteractionLog("fallback_restore", $"label={snapshot.ActionLabel} replayEvents={snapshot.ReplayEventCount} mode=choice_anchor");
                    return "fallback_choice";
                }
            }

            if (!UndoModSettings.EnableChoiceUndo && baseSnapshot != null)
            {
                if (await TryApplyFullStateInPlaceAsync(baseSnapshot.CombatState))
                {
                    WriteInteractionLog("restore_choice_skipped", $"label={snapshot.ActionLabel} replayEvents={snapshot.ReplayEventCount}");
                    return "choice_skipped";
                }

                throw new InvalidOperationException(_lastRestoreFailureReason ?? "Choice skip restore failed.");
            }

            if (UndoModSettings.EnableUnifiedEffectMode)
            {
                WriteInteractionLog("restore_choice_instant_failed", $"label={snapshot.ActionLabel} replayEvents={snapshot.ReplayEventCount}");
                throw new InvalidOperationException(_lastRestoreFailureReason ?? "Choice instant restore failed.");
            }

            throw new InvalidOperationException("Choice restore is unsupported for instant full-state recovery.");
        }

        if (await TryApplyFullStateInPlaceAsync(snapshot.CombatState))
        {
            UndoSelectionSessionState? selectionSession = snapshot.CombatState.SelectionSessionState;
            UndoChoiceSpec? restorableChoiceSpec = selectionSession?.ChoiceSpec
                ?? TryCaptureChoiceSpecFromActionContext(snapshot);
            UndoSnapshot? templateSnapshot = FindSyntheticChoiceTemplateSnapshot(baseSnapshot, snapshot.ReplayEventCount);
            if (UndoModSettings.EnableUnifiedEffectMode && await TryRestorePrimaryChoiceAsync(snapshot, templateSnapshot, stateAlreadyApplied: true))
            {
                WriteInteractionLog("primary_restore", $"kind={snapshot.ActionKind} label={snapshot.ActionLabel} replayEvents={snapshot.ReplayEventCount}");
                return "primary_choice";
            }

            bool shouldRestoreChoiceSession = UndoModSettings.EnableUnifiedEffectMode
                && restorableChoiceSpec?.SupportsSyntheticRestore == true
                && (UndoModSettings.EnableChoiceUndo
                    || HasRestorableChoiceSession(selectionSession)
                    || (snapshot.ActionKind == UndoActionKind.EndTurn
                        && RunManager.Instance.ActionExecutor.CurrentlyRunningAction?.State == GameActionState.GatheringPlayerChoice));
            if (shouldRestoreChoiceSession)
            {
                UndoSnapshot syntheticAnchor = new(
                    snapshot.CombatState,
                    snapshot.ReplayEventCount,
                    UndoActionKind.PlayerChoice,
                    _nextSequenceId++,
                    snapshot.ActionLabel,
                    isChoiceAnchor: true,
                    choiceSpec: restorableChoiceSpec);
                await WaitOneFrameAsync();
                OpenSyntheticChoiceSession(syntheticAnchor, templateSnapshot);
                WriteInteractionLog("fallback_restore", $"kind={snapshot.ActionKind} label={snapshot.ActionLabel} replayEvents={snapshot.ReplayEventCount}");
                return "fallback_choice";
            }

            WriteInteractionLog("restore_full_state", $"kind={snapshot.ActionKind} label={snapshot.ActionLabel} replayEvents={snapshot.ReplayEventCount}");
            return "full_state";
        }

        throw new InvalidOperationException(_lastRestoreFailureReason ?? "Full-state restore failed.");
    }
    private async Task<bool> TryRestorePrimaryChoiceAsync(UndoSnapshot snapshot, UndoSnapshot? branchSnapshot, bool stateAlreadyApplied = false)
    {
        PausedChoiceState? pausedChoiceState = snapshot.CombatState.ActionKernelState.PausedChoiceState;
        UndoChoiceSpec? choiceSpec = pausedChoiceState?.ChoiceSpec
            ?? (snapshot.IsChoiceAnchor ? snapshot.ChoiceSpec : null);
        if (choiceSpec == null)
            return false;

        if (pausedChoiceState != null)
        {
            RestoreCapabilityReport capability = UndoActionCodecRegistry.EvaluateCapability(pausedChoiceState);
            if (capability.Result != RestoreCapabilityResult.Supported)
            {
                UndoDebugLog.Write($"primary_choice_restore_capability:{capability.Result} replayEvents={snapshot.ReplayEventCount} label={snapshot.ActionLabel} detail={capability.Detail ?? "none"}");
                return false;
            }
        }
        else if (choiceSpec.Kind is not (UndoChoiceKind.ChooseACard or UndoChoiceKind.SimpleGridSelection))
        {
            return false;
        }

        if (!stateAlreadyApplied && !await TryApplyFullStateInPlaceAsync(snapshot.CombatState))
            return false;

        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
            return false;

        await WaitOneFrameAsync();

        UndoSyntheticChoiceSession primarySession = new(snapshot, choiceSpec, branchSnapshot);
        if (branchSnapshot?.ChoiceResultKey != null)
            primarySession.RememberBranch(branchSnapshot.ChoiceResultKey, branchSnapshot);

        if (choiceSpec.Kind is UndoChoiceKind.ChooseACard or UndoChoiceKind.SimpleGridSelection)
        {
            _syntheticChoiceSession = primarySession;
            _lastResolvedChoiceSpec = choiceSpec;
            _lastResolvedChoiceResultKey = null;
            Task<UndoChoiceResultKey?> selectionTask = pausedChoiceState != null
                ? UndoActionCodecRegistry.RestoreAsync(pausedChoiceState, runState)
                : RestorePrimaryChoiceAnchorAsync(choiceSpec, runState);
            NCombatUi? combatUi = null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                await WaitOneFrameAsync();
                combatUi = NCombatRoom.Instance?.Ui;
                if (combatUi != null && IsSupportedChoiceUiActive(combatUi))
                    break;
            }

            if (combatUi == null || !IsSupportedChoiceUiActive(combatUi))
            {
                _syntheticChoiceSession = null;
                UndoDebugLog.Write($"primary_choice_restore_selected_key:null replayEvents={snapshot.ReplayEventCount} label={snapshot.ActionLabel} codec={pausedChoiceState?.SourceActionCodecId ?? "action:choose-a-card"} stage=anchor_reopen_failed");
                return false;
            }

            WriteInteractionLog("primary_restore_anchor_reopened", $"label={snapshot.ActionLabel} replayEvents={snapshot.ReplayEventCount} codec={pausedChoiceState?.SourceActionCodecId ?? "action:choose-a-card"}");
            NotifyStateChanged();
            TaskHelper.RunSafely(HandlePrimaryChoiceSelectionAsync(primarySession, selectionTask));
            return true;
        }

        UndoChoiceResultKey? selectedKey;

        try
        {
            selectedKey = await UndoActionCodecRegistry.RestoreAsync(pausedChoiceState!, runState);
        }
        catch (TaskCanceledException)
        {
            UndoDebugLog.Write($"primary_choice_restore_selected_key:canceled replayEvents={snapshot.ReplayEventCount} label={snapshot.ActionLabel}");
            return false;
        }

        if (selectedKey == null)
        {
            UndoDebugLog.Write($"primary_choice_restore_selected_key:null replayEvents={snapshot.ReplayEventCount} label={snapshot.ActionLabel} codec={pausedChoiceState?.SourceActionCodecId ?? "action:choose-a-card"}");
            return false;
        }

        UndoDebugLog.Write($"primary_choice_restore_selected_key:{selectedKey} replayEvents={snapshot.ReplayEventCount} label={snapshot.ActionLabel} codec={pausedChoiceState?.SourceActionCodecId ?? "action:choose-a-card"}");
        if (primarySession.CachedBranches.TryGetValue(selectedKey, out UndoSnapshot? cachedBranchSnapshot))
        {
            if (!await TryApplySynthesizedChoiceBranchAsync(primarySession, cachedBranchSnapshot, selectedKey, vfxRequest: null))
                return false;

            return true;
        }

        UndoDebugLog.Write($"primary_choice_branch_miss:{selectedKey} replayEvents={snapshot.ReplayEventCount} label={snapshot.ActionLabel} cached={primarySession.CachedBranches.Count}");
        if (!TryCreateSyntheticChoiceBranchSnapshot(primarySession, selectedKey, out UndoSnapshot? synthesizedBranch))
        {
            MainFile.Logger.Warn($"Could not synthesize primary branch for restored choice {selectedKey}.");
            return false;
        }

        return await TryApplySynthesizedChoiceBranchAsync(primarySession, synthesizedBranch!, selectedKey, vfxRequest: null);
    }

    private static async Task<UndoChoiceResultKey?> RestorePrimaryChoiceAnchorAsync(UndoChoiceSpec choiceSpec, RunState runState)
    {
        Player? player = LocalContext.NetId is ulong localNetId
            ? runState.GetPlayer(localNetId)
            : runState.Players.FirstOrDefault();
        if (player == null)
            return null;

        switch (choiceSpec.Kind)
        {
            case UndoChoiceKind.ChooseACard:
            {
                NChooseACardSelectionScreen screen = NChooseACardSelectionScreen.ShowScreen(choiceSpec.BuildOptionCards(player), choiceSpec.CanSkip);
                CardModel? selected = (await screen.CardsSelected()).FirstOrDefault();
                return choiceSpec.TryMapSyntheticSelection(player, selected == null ? [] : [selected]);
            }
            case UndoChoiceKind.SimpleGridSelection:
            {
                NSimpleCardSelectScreen screen = NSimpleCardSelectScreen.Create(choiceSpec.BuildOptionCards(player), choiceSpec.SelectionPrefs);
                NOverlayStack.Instance.Push(screen);
                IEnumerable<CardModel> selected = await screen.CardsSelected();
                return choiceSpec.TryMapSyntheticSelection(player, selected);
            }
            default:
                return null;
        }
    }
    private async Task HandlePrimaryChoiceSelectionAsync(UndoSyntheticChoiceSession session, Task<UndoChoiceResultKey?> selectionTask)
    {
        try
        {
            UndoChoiceResultKey? selectedKey = await selectionTask;
            if (_syntheticChoiceSession != session)
                return;

            if (selectedKey == null)
            {
                UndoDebugLog.Write($"primary_choice_restore_selected_key:null replayEvents={session.AnchorSnapshot.ReplayEventCount} label={session.AnchorSnapshot.ActionLabel} codec={session.ChoiceSpec.Kind}");
                _syntheticChoiceSession = null;
                NotifyStateChanged();
                return;
            }

            UndoDebugLog.Write($"primary_choice_restore_selected_key:{selectedKey} replayEvents={session.AnchorSnapshot.ReplayEventCount} label={session.AnchorSnapshot.ActionLabel} codec={session.ChoiceSpec.Kind}");
            if (session.CachedBranches.TryGetValue(selectedKey, out UndoSnapshot? cachedBranchSnapshot))
            {
                if (await TryApplySynthesizedChoiceBranchAsync(session, cachedBranchSnapshot, selectedKey, vfxRequest: null))
                    WriteInteractionLog("branch_commit_after_reselect", $"choice={selectedKey} label={session.AnchorSnapshot.ActionLabel} replayEvents={session.AnchorSnapshot.ReplayEventCount} source=cached");
                return;
            }

            UndoDebugLog.Write($"primary_choice_branch_miss:{selectedKey} replayEvents={session.AnchorSnapshot.ReplayEventCount} label={session.AnchorSnapshot.ActionLabel} cached={session.CachedBranches.Count}");
            if (!TryCreateSyntheticChoiceBranchSnapshot(session, selectedKey, out UndoSnapshot? synthesizedBranch))
            {
                MainFile.Logger.Warn($"Could not synthesize primary branch for restored choice {selectedKey}.");
                _syntheticChoiceSession = null;
                NotifyStateChanged();
                return;
            }

            if (await TryApplySynthesizedChoiceBranchAsync(session, synthesizedBranch!, selectedKey, vfxRequest: null))
                WriteInteractionLog("branch_commit_after_reselect", $"choice={selectedKey} label={session.AnchorSnapshot.ActionLabel} replayEvents={session.AnchorSnapshot.ReplayEventCount} source=synthesized");
        }
        catch (TaskCanceledException)
        {
            if (_syntheticChoiceSession == session)
            {
                _syntheticChoiceSession = null;
                UndoDebugLog.Write($"primary_choice_restore_selected_key:canceled replayEvents={session.AnchorSnapshot.ReplayEventCount} label={session.AnchorSnapshot.ActionLabel}");
                NotifyStateChanged();
            }
        }
    }

    private async Task<bool> TryApplySynthesizedChoiceBranchAsync(
        UndoSyntheticChoiceSession session,
        UndoSnapshot synthesizedBranch,
        UndoChoiceResultKey selectedKey,
        SyntheticChoiceVfxRequest? vfxRequest)
    {
        _syntheticChoiceSession = null;
        _lastResolvedChoiceSpec = session.ChoiceSpec;
        _lastResolvedChoiceResultKey = selectedKey;
        session.RememberBranch(selectedKey, synthesizedBranch);
        _futureSnapshots.Clear();

        RewriteReplayChoiceBranch(session.AnchorSnapshot, synthesizedBranch);
        if (!await TryApplyFullStateInPlaceAsync(synthesizedBranch.CombatState))
            throw new InvalidOperationException($"Failed to apply synthesized choice branch {selectedKey}.");

        _combatReplay!.ActiveEventCount = synthesizedBranch.ReplayEventCount;
        bool preservePrimaryChoiceAnchor = session.ChoiceSpec.Kind is UndoChoiceKind.ChooseACard or UndoChoiceKind.SimpleGridSelection;
        UndoSnapshot anchorSnapshot = preservePrimaryChoiceAnchor ? session.AnchorSnapshot : synthesizedBranch;
        UndoCombatFullState? anchorCombatStateOverride = preservePrimaryChoiceAnchor ? session.AnchorSnapshot.CombatState : null;
        EnsurePlayerChoiceUndoAnchor(anchorSnapshot, session.ChoiceSpec, forceRefresh: true, anchorCombatStateOverride: anchorCombatStateOverride);
        NotifyStateChanged();
        if (vfxRequest != null)
            _ = TaskHelper.RunSafely(PlaySyntheticChoiceVfxAsync(vfxRequest));

        return true;
    }

    private async Task<bool> TryRestoreSyntheticChoiceAsync(UndoSnapshot snapshot, UndoSnapshot? branchSnapshot)
    {
        if (snapshot.ChoiceSpec == null)
            return false;

        if (!await TryApplyFullStateInPlaceAsync(snapshot.CombatState))
            return false;

        await WaitOneFrameAsync();
        OpenSyntheticChoiceSession(snapshot, branchSnapshot);
        return true;
    }

    private void OpenSyntheticChoiceSession(UndoSnapshot anchorSnapshot, UndoSnapshot? branchSnapshot)
    {
        _syntheticChoiceSession = new UndoSyntheticChoiceSession(anchorSnapshot, anchorSnapshot.ChoiceSpec!, branchSnapshot);
        if (branchSnapshot?.ChoiceResultKey != null)
            _syntheticChoiceSession.RememberBranch(branchSnapshot.ChoiceResultKey, branchSnapshot);

        TaskHelper.RunSafely(HandleSyntheticChoiceSelectionAsync(_syntheticChoiceSession));
    }

    private async Task HandleSyntheticChoiceSelectionAsync(UndoSyntheticChoiceSession session)
    {
        try
        {
            UndoChoiceResultKey? selectedKey = await ShowSyntheticChoiceSelectionAsync(session);
            if (_syntheticChoiceSession != session || selectedKey == null)
                return;

            if (session.CachedBranches.TryGetValue(selectedKey, out UndoSnapshot? cachedBranchSnapshot))
            {
                if (ReferenceEquals(_futureSnapshots.First?.Value, cachedBranchSnapshot))
                {
                    _syntheticChoiceSession = null;
                    _lastResolvedChoiceSpec = session.ChoiceSpec;
                    _lastResolvedChoiceResultKey = selectedKey;
                    Redo();
                    return;
                }

                SyntheticChoiceVfxRequest? cachedVfxRequest = CaptureSyntheticChoiceVfxRequest(session, cachedBranchSnapshot, selectedKey);
                if (await TryApplySynthesizedChoiceBranchAsync(session, cachedBranchSnapshot, selectedKey, cachedVfxRequest))
                    MainFile.Logger.Info($"Applied cached instant branch {selectedKey} for {session.ChoiceSpec.Kind}.");
                return;
            }

            if (!TryCreateSyntheticChoiceBranchSnapshot(session, selectedKey, out UndoSnapshot? branchSnapshot))
            {
                MainFile.Logger.Warn($"Could not synthesize instant branch for synthetic choice {selectedKey}.");
                OpenSyntheticChoiceSession(session.AnchorSnapshot, session.TemplateSnapshot ?? _futureSnapshots.First?.Value);
                return;
            }

            UndoSnapshot synthesizedBranch = branchSnapshot!;
            SyntheticChoiceVfxRequest? vfxRequest = CaptureSyntheticChoiceVfxRequest(session, synthesizedBranch, selectedKey);
            if (await TryApplySynthesizedChoiceBranchAsync(session, synthesizedBranch, selectedKey, vfxRequest))
                MainFile.Logger.Info($"Applied synthesized instant branch {selectedKey} for {session.ChoiceSpec.Kind}.");
        }
        catch (TaskCanceledException)
        {
            if (_syntheticChoiceSession == session)
                _syntheticChoiceSession = null;
        }
    }
    private async Task<bool> TryCompleteSyntheticRetainSelectionAsync(UndoSyntheticChoiceSession session, UndoChoiceResultKey selectedKey)
    {
        if (session.AnchorSnapshot.ActionKind != UndoActionKind.EndTurn
            || session.ChoiceSpec.Kind != UndoChoiceKind.HandSelection
            || session.ChoiceSpec.SourcePileType != PileType.Hand)
        {
            return false;
        }
        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        Player? me = combatState == null ? null : LocalContext.GetMe(combatState);
        if (combatState == null || me == null || combatState.CurrentSide != CombatSide.Player)
            return false;
        IReadOnlyList<CardModel> handCards = PileType.Hand.GetPile(me).Cards;
        foreach (int optionIndex in selectedKey.OptionIndexes)
        {
            if (optionIndex < 0 || optionIndex >= session.ChoiceSpec.SourcePileOptionIndexes.Count)
                return false;
            int handIndex = session.ChoiceSpec.SourcePileOptionIndexes[optionIndex];
            if (handIndex < 0 || handIndex >= handCards.Count)
                return false;
            handCards[handIndex].GiveSingleTurnRetain();
        }
        RunManager.Instance.ActionQueueSynchronizer.SetCombatState(ActionSynchronizerCombatState.NotPlayPhase);
        await CombatManager.Instance.EndPlayerTurnPhaseTwoInternal();
        await CombatManager.Instance.SwitchFromPlayerToEnemySide();
        return true;
    }

    private UndoSnapshot? ResolveSyntheticChoiceTemplateSnapshot(UndoSyntheticChoiceSession session)
    {
        if (session.TemplateSnapshot?.ChoiceResultKey != null)
            return session.TemplateSnapshot;

        if (_futureSnapshots.First?.Value is { IsChoiceAnchor: false, ChoiceResultKey: not null } futureBranch
            && futureBranch.ReplayEventCount >= session.AnchorSnapshot.ReplayEventCount)
        {
            return futureBranch;
        }

        if (_lastResolvedChoiceResultKey != null
            && session.CachedBranches.TryGetValue(_lastResolvedChoiceResultKey, out UndoSnapshot? lastResolvedBranch)
            && lastResolvedBranch.ChoiceResultKey != null)
        {
            return lastResolvedBranch;
        }

        return session.CachedBranches.Values
            .Where(static snapshot => snapshot.ChoiceResultKey != null)
            .OrderByDescending(static snapshot => snapshot.SequenceId)
            .FirstOrDefault();
    }

    private bool TryCreateSyntheticChoiceBranchSnapshot(
        UndoSyntheticChoiceSession session,
        UndoChoiceResultKey selectedKey,
        out UndoSnapshot? branchSnapshot)
    {
        branchSnapshot = null;

        UndoSnapshot? templateSnapshot = ResolveSyntheticChoiceTemplateSnapshot(session);
        if (templateSnapshot?.ChoiceResultKey == null)
            return false;

        UndoCombatFullState? combatState = null;
        bool created = session.ChoiceSpec.Kind switch
        {
            UndoChoiceKind.ChooseACard => TryCreateChooseACardCombatState(session.AnchorSnapshot, templateSnapshot, selectedKey, out combatState),
            UndoChoiceKind.HandSelection => TryCreateHandSelectionCombatState(session.AnchorSnapshot, templateSnapshot, selectedKey, out combatState),
            UndoChoiceKind.SimpleGridSelection => TryCreateSimpleGridSelectionCombatState(session.AnchorSnapshot, templateSnapshot, selectedKey, out combatState),
            _ => false
        };
        if (!created || combatState == null)
            return false;

        branchSnapshot = new UndoSnapshot(
            combatState,
            templateSnapshot.ReplayEventCount,
            templateSnapshot.ActionKind,
            _nextSequenceId++,
            templateSnapshot.ActionLabel,
            choiceResultKey: selectedKey);
        return true;
    }

    private static bool TryCreateChooseACardCombatState(
        UndoSnapshot anchorSnapshot,
        UndoSnapshot templateSnapshot,
        UndoChoiceResultKey selectedKey,
        out UndoCombatFullState? combatState)
    {
        combatState = null;
        UndoChoiceSpec? choiceSpec = anchorSnapshot.ChoiceSpec;
        UndoChoiceResultKey? templateChoiceKey = templateSnapshot.ChoiceResultKey;
        if (choiceSpec == null
            || choiceSpec.Kind != UndoChoiceKind.ChooseACard
            || templateChoiceKey == null
            || templateChoiceKey.OptionIndexes.Count != 1
            || selectedKey.OptionIndexes.Count != 1)
        {
            return false;
        }

        int selectedOptionIndex = selectedKey.OptionIndexes[0];
        int templateOptionIndex = templateChoiceKey.OptionIndexes[0];
        if (selectedOptionIndex < -1 || selectedOptionIndex >= choiceSpec.OptionCards.Count)
            return false;
        if (templateOptionIndex < 0 || templateOptionIndex >= choiceSpec.OptionCards.Count)
            return false;

        NetFullCombatState fullState = CloneFullState(templateSnapshot.CombatState.FullState);
        if (!TryGetComparablePlayerStates(anchorSnapshot.CombatState.FullState, fullState, out int playerIndex, out NetFullCombatState.PlayerState anchorPlayerState, out NetFullCombatState.PlayerState branchPlayerState))
            return false;

        if (!TryFindGeneratedCombatCard(anchorPlayerState, branchPlayerState, out int pileIndex, out int cardIndex, out NetFullCombatState.CardState generatedCardState)
            && !TryFindChooseACardTemplateSlot(
                anchorPlayerState,
                templateSnapshot.CombatState.FullState.Players[playerIndex],
                choiceSpec.OptionCards[templateOptionIndex],
                out pileIndex,
                out cardIndex,
                out generatedCardState))
        {
            return false;
        }

        NetFullCombatState.CombatPileState branchPileState = branchPlayerState.piles[pileIndex];
        if (selectedOptionIndex < 0)
        {
            branchPileState.cards.RemoveAt(cardIndex);
        }
        else
        {
            branchPileState.cards[cardIndex] = CreateChooseACardCardState(choiceSpec.OptionCards[selectedOptionIndex], generatedCardState);
        }

        branchPlayerState.piles[pileIndex] = branchPileState;
        fullState.Players[playerIndex] = branchPlayerState;
        combatState = CreateDerivedCombatState(templateSnapshot.CombatState, anchorSnapshot.CombatState, fullState);
        return true;
    }

    private static bool TryCreateHandSelectionCombatState(
        UndoSnapshot anchorSnapshot,
        UndoSnapshot templateSnapshot,
        UndoChoiceResultKey selectedKey,
        out UndoCombatFullState? combatState)
    {
        combatState = null;
        UndoChoiceSpec? choiceSpec = anchorSnapshot.ChoiceSpec;
        return choiceSpec != null
            && TryCreateSourcePileSelectionCombatState(anchorSnapshot, templateSnapshot, selectedKey, PileType.Hand, choiceSpec.SourcePileOptionIndexes, out combatState);
    }

    private static bool TryCreateSimpleGridSelectionCombatState(
        UndoSnapshot anchorSnapshot,
        UndoSnapshot templateSnapshot,
        UndoChoiceResultKey selectedKey,
        out UndoCombatFullState? combatState)
    {
        combatState = null;
        UndoChoiceSpec? choiceSpec = anchorSnapshot.ChoiceSpec;
        if (choiceSpec?.Kind != UndoChoiceKind.SimpleGridSelection || choiceSpec.SourcePileType == null)
            return false;

        return TryCreateSourcePileSelectionCombatState(anchorSnapshot, templateSnapshot, selectedKey, choiceSpec.SourcePileType.Value, choiceSpec.SourcePileOptionIndexes, out combatState);
    }

    private static bool TryCreateSourcePileSelectionCombatState(
        UndoSnapshot anchorSnapshot,
        UndoSnapshot templateSnapshot,
        UndoChoiceResultKey selectedKey,
        PileType sourcePileType,
        IReadOnlyList<int> sourcePileOptionIndexes,
        out UndoCombatFullState? combatState)
    {
        combatState = null;
        UndoChoiceResultKey? templateChoiceKey = templateSnapshot.ChoiceResultKey;
        if (templateChoiceKey == null || templateChoiceKey.OptionIndexes.Count == 0)
            return false;

        if (TryCreateInPlaceSourcePileModificationCombatState(
                anchorSnapshot,
                templateSnapshot,
                templateChoiceKey,
                selectedKey,
                sourcePileType,
                sourcePileOptionIndexes,
                out combatState))
        {
            return true;
        }

        if (TryCreateLikeTemplateSourcePileSelectionCombatState(
                anchorSnapshot,
                templateSnapshot,
                templateChoiceKey,
                selectedKey,
                sourcePileType,
                sourcePileOptionIndexes,
                out combatState))
        {
            return true;
        }

        return TryCreateVariableCountSourcePileSelectionCombatState(
            anchorSnapshot,
            templateSnapshot,
            templateChoiceKey,
            selectedKey,
            sourcePileType,
            sourcePileOptionIndexes,
            out combatState);
    }

    private static bool TryCreateInPlaceSourcePileModificationCombatState(
        UndoSnapshot anchorSnapshot,
        UndoSnapshot templateSnapshot,
        UndoChoiceResultKey templateChoiceKey,
        UndoChoiceResultKey selectedKey,
        PileType sourcePileType,
        IReadOnlyList<int> sourcePileOptionIndexes,
        out UndoCombatFullState? combatState)
    {
        combatState = null;
        if (selectedKey.OptionIndexes.Count == 0
            || selectedKey.OptionIndexes.Count != templateChoiceKey.OptionIndexes.Count)
        {
            return false;
        }

        List<int> selectedOptionIndexes = [.. selectedKey.OptionIndexes];
        List<int> templateOptionIndexes = [.. templateChoiceKey.OptionIndexes];
        if (selectedOptionIndexes.Any(index => index < 0 || index >= sourcePileOptionIndexes.Count)
            || templateOptionIndexes.Any(index => index < 0 || index >= sourcePileOptionIndexes.Count))
        {
            return false;
        }

        NetFullCombatState fullState = CloneFullState(templateSnapshot.CombatState.FullState);
        if (!TryGetComparablePlayerStates(anchorSnapshot.CombatState.FullState, fullState, out int playerIndex, out NetFullCombatState.PlayerState anchorPlayerState, out NetFullCombatState.PlayerState branchPlayerState))
            return false;

        NetFullCombatState.PlayerState templatePlayerState = templateSnapshot.CombatState.FullState.Players[playerIndex];
        int anchorSourcePileIndex = FindPileIndex(anchorPlayerState.piles, sourcePileType);
        int templateSourcePileIndex = FindPileIndex(templatePlayerState.piles, sourcePileType);
        int branchSourcePileIndex = FindPileIndex(branchPlayerState.piles, sourcePileType);
        if (anchorSourcePileIndex < 0 || templateSourcePileIndex < 0 || branchSourcePileIndex < 0)
            return false;

        NetFullCombatState.CombatPileState anchorSourcePileState = anchorPlayerState.piles[anchorSourcePileIndex];
        NetFullCombatState.CombatPileState templateSourcePileState = templatePlayerState.piles[templateSourcePileIndex];
        if (anchorSourcePileState.cards.Count != templateSourcePileState.cards.Count)
            return false;

        List<int> selectedSourceIndexes = [];
        List<int> templateSourceIndexes = [];
        foreach (int optionIndex in selectedOptionIndexes)
            selectedSourceIndexes.Add(sourcePileOptionIndexes[optionIndex]);
        foreach (int optionIndex in templateOptionIndexes)
            templateSourceIndexes.Add(sourcePileOptionIndexes[optionIndex]);

        if (selectedSourceIndexes.Any(index => index < 0 || index >= anchorSourcePileState.cards.Count)
            || templateSourceIndexes.Any(index => index < 0 || index >= anchorSourcePileState.cards.Count))
        {
            return false;
        }

        List<int> changedSourceIndexes = FindUnmatchedCardIndexes(anchorSourcePileState.cards, templateSourcePileState.cards);
        if (changedSourceIndexes.Count != templateSourceIndexes.Count)
            return false;

        changedSourceIndexes.Sort();
        List<int> sortedTemplateSourceIndexes = [.. templateSourceIndexes.OrderBy(static index => index)];
        if (!changedSourceIndexes.SequenceEqual(sortedTemplateSourceIndexes))
            return false;

        List<NetFullCombatState.CardState> templateReplacementStates = templateSourceIndexes
            .Select(index => ClonePacketSerializable(templateSourcePileState.cards[index]))
            .ToList();

        NetFullCombatState.CombatPileState branchSourcePileState = ClonePacketSerializable(anchorSourcePileState);
        for (int i = 0; i < selectedSourceIndexes.Count; i++)
            branchSourcePileState.cards[selectedSourceIndexes[i]] = ClonePacketSerializable(templateReplacementStates[i]);

        branchPlayerState.piles[branchSourcePileIndex] = branchSourcePileState;
        fullState.Players[playerIndex] = branchPlayerState;
        combatState = CreateDerivedCombatState(templateSnapshot.CombatState, anchorSnapshot.CombatState, fullState);
        return true;
    }

    private SyntheticChoiceVfxRequest? CaptureSyntheticChoiceVfxRequest(
        UndoSyntheticChoiceSession session,
        UndoSnapshot synthesizedBranch,
        UndoChoiceResultKey selectedKey)
    {
        SyntheticChoiceVfxRequest request = new();
        TryCaptureExhaustChoiceVfx(session, synthesizedBranch, selectedKey, request);
        TryCaptureTransformChoiceVfx(session, synthesizedBranch, selectedKey, request);
        return request.HasEffects ? request : null;
    }

    private bool TryCaptureExhaustChoiceVfx(
        UndoSyntheticChoiceSession session,
        UndoSnapshot synthesizedBranch,
        UndoChoiceResultKey selectedKey,
        SyntheticChoiceVfxRequest request)
    {
        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        Player? me = combatState == null ? null : LocalContext.GetMe(combatState);
        NPlayerHand? hand = NCombatRoom.Instance?.Ui?.Hand;
        UndoChoiceSpec choiceSpec = session.ChoiceSpec;
        if (me == null
            || hand == null
            || choiceSpec.Kind != UndoChoiceKind.HandSelection
            || choiceSpec.SourcePileType != PileType.Hand
            || selectedKey.OptionIndexes.Count == 0)
        {
            return false;
        }

        if (!TryGetComparablePlayerStates(
                session.AnchorSnapshot.CombatState.FullState,
                synthesizedBranch.CombatState.FullState,
                out _,
                out NetFullCombatState.PlayerState anchorPlayerState,
                out NetFullCombatState.PlayerState branchPlayerState))
        {
            return false;
        }

        int anchorHandPileIndex = FindPileIndex(anchorPlayerState.piles, PileType.Hand);
        int branchHandPileIndex = FindPileIndex(branchPlayerState.piles, PileType.Hand);
        int anchorExhaustPileIndex = FindPileIndex(anchorPlayerState.piles, PileType.Exhaust);
        int branchExhaustPileIndex = FindPileIndex(branchPlayerState.piles, PileType.Exhaust);
        if (anchorHandPileIndex < 0
            || branchHandPileIndex < 0
            || anchorExhaustPileIndex < 0
            || branchExhaustPileIndex < 0)
        {
            return false;
        }

        int selectedCount = selectedKey.OptionIndexes.Count;
        NetFullCombatState.CombatPileState anchorHandPileState = anchorPlayerState.piles[anchorHandPileIndex];
        NetFullCombatState.CombatPileState branchHandPileState = branchPlayerState.piles[branchHandPileIndex];
        NetFullCombatState.CombatPileState anchorExhaustPileState = anchorPlayerState.piles[anchorExhaustPileIndex];
        NetFullCombatState.CombatPileState branchExhaustPileState = branchPlayerState.piles[branchExhaustPileIndex];
        if (anchorHandPileState.cards.Count - branchHandPileState.cards.Count != selectedCount
            || branchExhaustPileState.cards.Count - anchorExhaustPileState.cards.Count < selectedCount)
        {
            return false;
        }

        IReadOnlyList<CardModel> liveHandCards = PileType.Hand.GetPile(me).Cards;
        foreach (int optionIndex in selectedKey.OptionIndexes)
        {
            if (optionIndex < 0 || optionIndex >= choiceSpec.SourcePileOptionIndexes.Count)
                return false;

            int handIndex = choiceSpec.SourcePileOptionIndexes[optionIndex];
            if (handIndex < 0 || handIndex >= liveHandCards.Count)
                return false;

            CardModel liveCard = liveHandCards[handIndex];
            NCardHolder? holder = hand.GetCardHolder(liveCard);
            Vector2 globalPosition = holder?.CardNode?.GlobalPosition ?? holder?.GlobalPosition ?? hand.GlobalPosition;
            request.ExhaustCards.Add(new SyntheticExhaustVfxCard(liveCard.ToSerializable(), globalPosition));
        }

        return request.ExhaustCards.Count > 0;
    }

    private bool TryCaptureTransformChoiceVfx(
        UndoSyntheticChoiceSession session,
        UndoSnapshot synthesizedBranch,
        UndoChoiceResultKey selectedKey,
        SyntheticChoiceVfxRequest request)
    {
        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        Player? me = combatState == null ? null : LocalContext.GetMe(combatState);
        UndoChoiceSpec choiceSpec = session.ChoiceSpec;
        if (me == null
            || choiceSpec.SourcePileType == null
            || selectedKey.OptionIndexes.Count == 0)
        {
            return false;
        }

        if (!TryGetComparablePlayerStates(
                session.AnchorSnapshot.CombatState.FullState,
                synthesizedBranch.CombatState.FullState,
                out _,
                out NetFullCombatState.PlayerState anchorPlayerState,
                out NetFullCombatState.PlayerState branchPlayerState))
        {
            return false;
        }

        PileType sourcePileType = choiceSpec.SourcePileType.Value;
        int anchorSourcePileIndex = FindPileIndex(anchorPlayerState.piles, sourcePileType);
        int branchSourcePileIndex = FindPileIndex(branchPlayerState.piles, sourcePileType);
        if (anchorSourcePileIndex < 0 || branchSourcePileIndex < 0)
            return false;

        NetFullCombatState.CombatPileState anchorSourcePileState = anchorPlayerState.piles[anchorSourcePileIndex];
        NetFullCombatState.CombatPileState branchSourcePileState = branchPlayerState.piles[branchSourcePileIndex];
        if (anchorSourcePileState.cards.Count != branchSourcePileState.cards.Count)
            return false;

        List<int> selectedSourceIndexes = [];
        foreach (int optionIndex in selectedKey.OptionIndexes)
        {
            if (optionIndex < 0 || optionIndex >= choiceSpec.SourcePileOptionIndexes.Count)
                return false;

            selectedSourceIndexes.Add(choiceSpec.SourcePileOptionIndexes[optionIndex]);
        }

        if (selectedSourceIndexes.Any(index => index < 0 || index >= anchorSourcePileState.cards.Count))
            return false;

        List<int> changedIndexes = FindUnmatchedCardIndexes(anchorSourcePileState.cards, branchSourcePileState.cards);
        changedIndexes.Sort();
        List<int> sortedSelectedIndexes = [.. selectedSourceIndexes.OrderBy(static index => index)];
        if (!changedIndexes.SequenceEqual(sortedSelectedIndexes))
            return false;

        IReadOnlyList<CardModel> liveSourceCards = sourcePileType.GetPile(me).Cards;
        if (liveSourceCards.Count != anchorSourcePileState.cards.Count)
            return false;

        request.TransformPileType = sourcePileType;
        foreach (int sourceIndex in selectedSourceIndexes)
            request.TransformCards.Add(new SyntheticTransformVfxCard(liveSourceCards[sourceIndex].ToSerializable(), sourceIndex));

        return request.TransformCards.Count > 0;
    }

    private async Task PlaySyntheticChoiceVfxAsync(SyntheticChoiceVfxRequest request)
    {
        await WaitOneFrameAsync();
        await RunOnMainThreadAsync<object?>(() =>
        {
            CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
            Player? me = combatState == null ? null : LocalContext.GetMe(combatState);
            NCombatRoom? combatRoom = NCombatRoom.Instance;
            if (me == null || combatRoom == null)
                return null;

            for (int i = 0; i < request.ExhaustCards.Count; i++)
            {
                SyntheticExhaustVfxCard exhaustCard = request.ExhaustCards[i];
                CardModel card = CardModel.FromSerializable(ClonePacketSerializable(exhaustCard.Card));
                if (card.Owner == null)
                    card.Owner = me;

                NCard cardNode = CreateCardNode(card, PileType.Hand);
                combatRoom.Ui.AddChildSafely(cardNode);
                cardNode.GlobalPosition = exhaustCard.GlobalPosition;
                cardNode.ZIndex = 100;
                cardNode.RotationDegrees = (i % 2 == 0 ? -3f : 3f);

                NExhaustVfx? exhaustVfx = NExhaustVfx.Create(cardNode);
                if (exhaustVfx != null)
                {
                    exhaustVfx.ZIndex = 130;
                    combatRoom.Ui.AddChildSafely(exhaustVfx);
                }

                float duration = SaveManager.Instance.PrefsSave.FastMode == FastModeType.Fast ? 0.2f : 0.3f;
                Tween tween = combatRoom.CreateTween();
                tween.SetParallel(true);
                tween.TweenProperty(cardNode, "modulate", StsColors.exhaustGray, duration);
                tween.Chain().TweenInterval(0.1f);
                tween.Chain().TweenCallback(Callable.From(cardNode.QueueFreeSafely));
            }
            if (request.TransformPileType != null)
            {
                IReadOnlyList<CardModel> liveSourceCards = request.TransformPileType.Value.GetPile(me).Cards;
                Vector2 transformCenter = combatRoom.GetViewportRect().GetCenter();
                float transformSpacing = request.TransformCards.Count <= 1 ? 0f : 260f;
                for (int i = 0; i < request.TransformCards.Count; i++)
                {
                    SyntheticTransformVfxCard transformCard = request.TransformCards[i];
                    if (transformCard.SourcePileIndex < 0 || transformCard.SourcePileIndex >= liveSourceCards.Count)
                        continue;

                    CardModel startCard = CardModel.FromSerializable(ClonePacketSerializable(transformCard.Card));
                    if (startCard.Owner == null)
                        startCard.Owner = me;

                    CardModel endCard = liveSourceCards[transformCard.SourcePileIndex];
                    NCardTransformVfx? transformVfx = NCardTransformVfx.Create(startCard, endCard, Array.Empty<RelicModel>());
                    if (transformVfx != null)
                    {
                        combatRoom.CombatVfxContainer.AddChildSafely(transformVfx);
                        float offsetX = (i - (request.TransformCards.Count - 1) * 0.5f) * transformSpacing;
                        float offsetY = 0f;
                        transformVfx.GlobalPosition = transformCenter + new Vector2(offsetX, offsetY);
                    }
                }
            }

            return null;
        });
    }
    private static bool TryCreateLikeTemplateSourcePileSelectionCombatState(
        UndoSnapshot anchorSnapshot,
        UndoSnapshot templateSnapshot,
        UndoChoiceResultKey templateChoiceKey,
        UndoChoiceResultKey selectedKey,
        PileType sourcePileType,
        IReadOnlyList<int> sourcePileOptionIndexes,
        out UndoCombatFullState? combatState)
    {
        combatState = null;
        if (selectedKey.OptionIndexes.Count != templateChoiceKey.OptionIndexes.Count)
            return false;

        List<int> selectedOptionIndexes = [.. selectedKey.OptionIndexes];
        List<int> templateOptionIndexes = [.. templateChoiceKey.OptionIndexes];
        if (selectedOptionIndexes.Any(index => index < 0 || index >= sourcePileOptionIndexes.Count)
            || templateOptionIndexes.Any(index => index < 0 || index >= sourcePileOptionIndexes.Count))
        {
            return false;
        }

        NetFullCombatState fullState = CloneFullState(templateSnapshot.CombatState.FullState);
        if (!TryGetComparablePlayerStates(anchorSnapshot.CombatState.FullState, fullState, out int playerIndex, out NetFullCombatState.PlayerState anchorPlayerState, out NetFullCombatState.PlayerState branchPlayerState))
            return false;

        int anchorSourcePileIndex = FindPileIndex(anchorPlayerState.piles, sourcePileType);
        int branchSourcePileIndex = FindPileIndex(branchPlayerState.piles, sourcePileType);
        if (anchorSourcePileIndex < 0 || branchSourcePileIndex < 0)
            return false;

        NetFullCombatState.CombatPileState anchorSourcePileState = anchorPlayerState.piles[anchorSourcePileIndex];
        List<int> selectedSourceIndexes = [];
        List<int> templateSourceIndexes = [];
        foreach (int optionIndex in selectedOptionIndexes)
            selectedSourceIndexes.Add(sourcePileOptionIndexes[optionIndex]);
        foreach (int optionIndex in templateOptionIndexes)
            templateSourceIndexes.Add(sourcePileOptionIndexes[optionIndex]);

        if (selectedSourceIndexes.Any(index => index < 0 || index >= anchorSourcePileState.cards.Count)
            || templateSourceIndexes.Any(index => index < 0 || index >= anchorSourcePileState.cards.Count))
        {
            return false;
        }

        List<NetFullCombatState.CardState> selectedSourceCardStates = selectedSourceIndexes
            .Select(index => ClonePacketSerializable(anchorSourcePileState.cards[index]))
            .ToList();
        List<NetFullCombatState.CardState> templateSelectedCardStates = templateSourceIndexes
            .Select(index => anchorSourcePileState.cards[index])
            .ToList();

        NetFullCombatState.CombatPileState branchSourcePileState = ClonePacketSerializable(anchorSourcePileState);
        foreach (int sourceIndex in selectedSourceIndexes.OrderByDescending(static index => index))
            branchSourcePileState.cards.RemoveAt(sourceIndex);
        branchPlayerState.piles[branchSourcePileIndex] = branchSourcePileState;

        if (!TryFindSourceSelectionDestinationSlots(
                anchorPlayerState,
                templateSnapshot.CombatState.FullState.Players[playerIndex],
                sourcePileType,
                templateSelectedCardStates,
                out List<(int PileIndex, int CardIndex, int TemplateSelectionIndex)> destinationSlots))
        {
            return false;
        }

        if (destinationSlots.Count != selectedSourceCardStates.Count)
            return false;

        foreach ((int pileIndex, int cardIndex, int templateSelectionIndex) in destinationSlots)
        {
            NetFullCombatState.CombatPileState destinationPileState = branchPlayerState.piles[pileIndex];
            if (cardIndex < 0 || cardIndex >= destinationPileState.cards.Count)
                return false;

            destinationPileState.cards[cardIndex] = ClonePacketSerializable(selectedSourceCardStates[templateSelectionIndex]);
            branchPlayerState.piles[pileIndex] = destinationPileState;
        }

        fullState.Players[playerIndex] = branchPlayerState;
        combatState = CreateDerivedCombatState(templateSnapshot.CombatState, anchorSnapshot.CombatState, fullState);
        return true;
    }

    private static bool TryCreateVariableCountSourcePileSelectionCombatState(
        UndoSnapshot anchorSnapshot,
        UndoSnapshot templateSnapshot,
        UndoChoiceResultKey templateChoiceKey,
        UndoChoiceResultKey selectedKey,
        PileType sourcePileType,
        IReadOnlyList<int> sourcePileOptionIndexes,
        out UndoCombatFullState? combatState)
    {
        combatState = null;
        List<int> selectedOptionIndexes = [.. selectedKey.OptionIndexes];
        List<int> templateOptionIndexes = [.. templateChoiceKey.OptionIndexes];
        if (selectedOptionIndexes.Any(index => index < 0 || index >= sourcePileOptionIndexes.Count)
            || templateOptionIndexes.Any(index => index < 0 || index >= sourcePileOptionIndexes.Count))
        {
            return false;
        }

        NetFullCombatState fullState = CloneFullState(templateSnapshot.CombatState.FullState);
        if (!TryGetComparablePlayerStates(anchorSnapshot.CombatState.FullState, fullState, out int playerIndex, out NetFullCombatState.PlayerState anchorPlayerState, out NetFullCombatState.PlayerState branchPlayerState))
            return false;

        NetFullCombatState.PlayerState templatePlayerState = templateSnapshot.CombatState.FullState.Players[playerIndex];
        int anchorSourcePileIndex = FindPileIndex(anchorPlayerState.piles, sourcePileType);
        int branchSourcePileIndex = FindPileIndex(branchPlayerState.piles, sourcePileType);
        if (anchorSourcePileIndex < 0 || branchSourcePileIndex < 0)
            return false;

        NetFullCombatState.CombatPileState anchorSourcePileState = anchorPlayerState.piles[anchorSourcePileIndex];
        List<int> selectedSourceIndexes = [];
        List<int> templateSourceIndexes = [];
        foreach (int optionIndex in selectedOptionIndexes)
            selectedSourceIndexes.Add(sourcePileOptionIndexes[optionIndex]);
        foreach (int optionIndex in templateOptionIndexes)
            templateSourceIndexes.Add(sourcePileOptionIndexes[optionIndex]);

        if (selectedSourceIndexes.Any(index => index < 0 || index >= anchorSourcePileState.cards.Count)
            || templateSourceIndexes.Any(index => index < 0 || index >= anchorSourcePileState.cards.Count))
        {
            return false;
        }

        List<NetFullCombatState.CardState> selectedSourceCardStates = selectedSourceIndexes
            .Select(index => ClonePacketSerializable(anchorSourcePileState.cards[index]))
            .ToList();
        List<NetFullCombatState.CardState> templateSelectedCardStates = templateSourceIndexes
            .Select(index => anchorSourcePileState.cards[index])
            .ToList();

        NetFullCombatState.CombatPileState branchSourcePileState = ClonePacketSerializable(anchorSourcePileState);
        foreach (int sourceIndex in selectedSourceIndexes.OrderByDescending(static index => index))
            branchSourcePileState.cards.RemoveAt(sourceIndex);
        branchPlayerState.piles[branchSourcePileIndex] = branchSourcePileState;

        if (!TryApplyVariableCountSourceSelectionDestinationPattern(
                anchorPlayerState,
                templatePlayerState,
                branchPlayerState,
                sourcePileType,
                templateSelectedCardStates,
                selectedSourceCardStates))
        {
            return false;
        }

        fullState.Players[playerIndex] = branchPlayerState;
        combatState = CreateDerivedCombatState(templateSnapshot.CombatState, anchorSnapshot.CombatState, fullState);
        return true;
    }

    private static bool TryApplyVariableCountSourceSelectionDestinationPattern(
        NetFullCombatState.PlayerState anchorPlayerState,
        NetFullCombatState.PlayerState templatePlayerState,
        NetFullCombatState.PlayerState branchPlayerState,
        PileType sourcePileType,
        IReadOnlyList<NetFullCombatState.CardState> templateSelectedCardStates,
        IReadOnlyList<NetFullCombatState.CardState> selectedSourceCardStates)
    {
        return TryApplyTemplateMatchedSourceSelectionDestinationPattern(
                anchorPlayerState,
                templatePlayerState,
                branchPlayerState,
                sourcePileType,
                templateSelectedCardStates,
                selectedSourceCardStates)
            || TryApplyCountDeltaSourceSelectionDestinationPattern(
                anchorPlayerState,
                templatePlayerState,
                branchPlayerState,
                sourcePileType,
                templateSelectedCardStates,
                selectedSourceCardStates);
    }

    private static bool TryApplyTemplateMatchedSourceSelectionDestinationPattern(
        NetFullCombatState.PlayerState anchorPlayerState,
        NetFullCombatState.PlayerState templatePlayerState,
        NetFullCombatState.PlayerState branchPlayerState,
        PileType sourcePileType,
        IReadOnlyList<NetFullCombatState.CardState> templateSelectedCardStates,
        IReadOnlyList<NetFullCombatState.CardState> selectedSourceCardStates)
    {
        List<(int TemplatePileIndex, int CardIndex, int TemplateSelectionIndex)> matchedTemplateSlots = [];
        bool[] usedTemplateSelections = new bool[templateSelectedCardStates.Count];

        for (int i = 0; i < templatePlayerState.piles.Count; i++)
        {
            NetFullCombatState.CombatPileState templatePileState = templatePlayerState.piles[i];
            if (templatePileState.pileType == sourcePileType)
                continue;

            int anchorPileIndex = FindPileIndex(anchorPlayerState.piles, templatePileState.pileType);
            if (anchorPileIndex < 0)
                return false;

            NetFullCombatState.CombatPileState anchorPileState = anchorPlayerState.piles[anchorPileIndex];
            List<int> unmatchedIndexes = FindUnmatchedCardIndexes(anchorPileState.cards, templatePileState.cards);
            foreach (int unmatchedIndex in unmatchedIndexes)
            {
                for (int selectionIndex = 0; selectionIndex < templateSelectedCardStates.Count; selectionIndex++)
                {
                    if (usedTemplateSelections[selectionIndex]
                        || !PacketDataEquals(templatePileState.cards[unmatchedIndex], templateSelectedCardStates[selectionIndex]))
                    {
                        continue;
                    }

                    usedTemplateSelections[selectionIndex] = true;
                    matchedTemplateSlots.Add((i, unmatchedIndex, selectionIndex));
                    break;
                }
            }
        }

        if (!usedTemplateSelections.All(static used => used))
            return false;

        if (matchedTemplateSlots.Count == 0)
            return selectedSourceCardStates.Count == 0;

        List<IGrouping<int, (int TemplatePileIndex, int CardIndex, int TemplateSelectionIndex)>> destinationGroups = matchedTemplateSlots
            .GroupBy(static slot => slot.TemplatePileIndex)
            .ToList();
        if (destinationGroups.Count != 1)
            return false;

        IGrouping<int, (int TemplatePileIndex, int CardIndex, int TemplateSelectionIndex)> destinationGroup = destinationGroups[0];
        List<(int TemplatePileIndex, int CardIndex, int TemplateSelectionIndex)> orderedSlots = destinationGroup
            .OrderBy(slot => slot.CardIndex)
            .ToList();

        int insertionIndex = orderedSlots[0].CardIndex;
        for (int i = 0; i < orderedSlots.Count; i++)
        {
            if (orderedSlots[i].CardIndex != insertionIndex + i)
                return false;
        }

        List<int> templateSelectionOrder = orderedSlots
            .Select(slot => slot.TemplateSelectionIndex)
            .ToList();
        bool isForwardOrder = templateSelectionOrder.SequenceEqual(Enumerable.Range(0, templateSelectionOrder.Count));
        bool isReverseOrder = templateSelectionOrder.SequenceEqual(Enumerable.Range(0, templateSelectionOrder.Count).Reverse());
        if (!isForwardOrder && !isReverseOrder)
            return false;

        int templateDestinationPileIndex = destinationGroup.Key;
        PileType destinationPileType = templatePlayerState.piles[templateDestinationPileIndex].pileType;
        int branchDestinationPileIndex = FindPileIndex(branchPlayerState.piles, destinationPileType);
        if (branchDestinationPileIndex < 0)
            return false;

        NetFullCombatState.CombatPileState branchDestinationPileState = ClonePacketSerializable(branchPlayerState.piles[branchDestinationPileIndex]);
        foreach ((int _, int cardIndex, _) in orderedSlots.OrderByDescending(static slot => slot.CardIndex))
            branchDestinationPileState.cards.RemoveAt(cardIndex);

        List<NetFullCombatState.CardState> cardsToInsert = selectedSourceCardStates
            .Select(ClonePacketSerializable)
            .ToList();
        if (isReverseOrder)
            cardsToInsert.Reverse();

        for (int i = 0; i < cardsToInsert.Count; i++)
            branchDestinationPileState.cards.Insert(insertionIndex + i, cardsToInsert[i]);

        branchPlayerState.piles[branchDestinationPileIndex] = branchDestinationPileState;
        return true;
    }

    private static bool TryApplyCountDeltaSourceSelectionDestinationPattern(
        NetFullCombatState.PlayerState anchorPlayerState,
        NetFullCombatState.PlayerState templatePlayerState,
        NetFullCombatState.PlayerState branchPlayerState,
        PileType sourcePileType,
        IReadOnlyList<NetFullCombatState.CardState> templateSelectedCardStates,
        IReadOnlyList<NetFullCombatState.CardState> selectedSourceCardStates)
    {
        int anchorSourcePileIndex = FindPileIndex(anchorPlayerState.piles, sourcePileType);
        int templateSourcePileIndex = FindPileIndex(templatePlayerState.piles, sourcePileType);
        if (anchorSourcePileIndex < 0 || templateSourcePileIndex < 0)
            return false;

        NetFullCombatState.CombatPileState anchorSourcePileState = anchorPlayerState.piles[anchorSourcePileIndex];
        NetFullCombatState.CombatPileState templateSourcePileState = templatePlayerState.piles[templateSourcePileIndex];
        if (anchorSourcePileState.cards.Count - templateSourcePileState.cards.Count != templateSelectedCardStates.Count)
            return false;

        int templateDestinationPileIndex = -1;
        PileType destinationPileType = default;
        List<int>? destinationUnmatchedIndexes = null;

        for (int i = 0; i < templatePlayerState.piles.Count; i++)
        {
            NetFullCombatState.CombatPileState templatePileState = templatePlayerState.piles[i];
            if (templatePileState.pileType == sourcePileType)
                continue;

            int anchorPileIndex = FindPileIndex(anchorPlayerState.piles, templatePileState.pileType);
            if (anchorPileIndex < 0)
                return false;

            NetFullCombatState.CombatPileState anchorPileState = anchorPlayerState.piles[anchorPileIndex];
            int countDelta = templatePileState.cards.Count - anchorPileState.cards.Count;
            if (countDelta < 0)
                return false;
            if (countDelta == 0)
                continue;
            if (countDelta < templateSelectedCardStates.Count || templateDestinationPileIndex >= 0)
                return false;

            List<int> unmatchedIndexes = FindUnmatchedCardIndexes(anchorPileState.cards, templatePileState.cards);
            if (unmatchedIndexes.Count != countDelta)
                return false;

            templateDestinationPileIndex = i;
            destinationPileType = templatePileState.pileType;
            destinationUnmatchedIndexes = unmatchedIndexes;
        }

        if (templateDestinationPileIndex < 0 || destinationUnmatchedIndexes == null)
            return selectedSourceCardStates.Count == 0;

        destinationUnmatchedIndexes.Sort();
        int insertionIndex = destinationUnmatchedIndexes[0];
        int contiguousSelectionSpan = Math.Min(templateSelectedCardStates.Count, destinationUnmatchedIndexes.Count);
        for (int i = 0; i < contiguousSelectionSpan; i++)
        {
            if (destinationUnmatchedIndexes[i] != insertionIndex + i)
                return false;
        }

        int branchDestinationPileIndex = FindPileIndex(branchPlayerState.piles, destinationPileType);
        if (branchDestinationPileIndex < 0)
            return false;

        bool reverseOrder = destinationPileType == PileType.Deck && insertionIndex == 0 && selectedSourceCardStates.Count > 1;
        NetFullCombatState.CombatPileState branchDestinationPileState = ClonePacketSerializable(branchPlayerState.piles[branchDestinationPileIndex]);
        for (int i = contiguousSelectionSpan - 1; i >= 0; i--)
            branchDestinationPileState.cards.RemoveAt(insertionIndex + i);

        List<NetFullCombatState.CardState> cardsToInsert = selectedSourceCardStates
            .Select(ClonePacketSerializable)
            .ToList();
        if (reverseOrder)
            cardsToInsert.Reverse();

        for (int i = 0; i < cardsToInsert.Count; i++)
            branchDestinationPileState.cards.Insert(insertionIndex + i, cardsToInsert[i]);

        branchPlayerState.piles[branchDestinationPileIndex] = branchDestinationPileState;
        return true;
    }
    private static bool TryFindSourceSelectionDestinationSlots(
        NetFullCombatState.PlayerState anchorPlayerState,
        NetFullCombatState.PlayerState templatePlayerState,
        PileType sourcePileType,
        IReadOnlyList<NetFullCombatState.CardState> templateSelectedCardStates,
        out List<(int PileIndex, int CardIndex, int TemplateSelectionIndex)> destinationSlots)
    {
        destinationSlots = [];
        List<(int PileIndex, int CardIndex, NetFullCombatState.CardState CardState)> unmatchedSlots = [];

        for (int i = 0; i < templatePlayerState.piles.Count; i++)
        {
            NetFullCombatState.CombatPileState templatePileState = templatePlayerState.piles[i];
            if (templatePileState.pileType == sourcePileType)
                continue;

            int anchorPileIndex = FindPileIndex(anchorPlayerState.piles, templatePileState.pileType);
            if (anchorPileIndex < 0)
                return false;

            NetFullCombatState.CombatPileState anchorPileState = anchorPlayerState.piles[anchorPileIndex];
            List<int> unmatchedIndexes = FindUnmatchedCardIndexes(anchorPileState.cards, templatePileState.cards);
            foreach (int unmatchedIndex in unmatchedIndexes)
                unmatchedSlots.Add((i, unmatchedIndex, templatePileState.cards[unmatchedIndex]));
        }

        if (unmatchedSlots.Count != templateSelectedCardStates.Count)
            return false;

        bool[] usedTemplateSelections = new bool[templateSelectedCardStates.Count];
        foreach ((int pileIndex, int cardIndex, NetFullCombatState.CardState cardState) in unmatchedSlots)
        {
            int matchedSelectionIndex = -1;
            for (int i = 0; i < templateSelectedCardStates.Count; i++)
            {
                if (usedTemplateSelections[i] || !PacketDataEquals(cardState, templateSelectedCardStates[i]))
                    continue;

                matchedSelectionIndex = i;
                usedTemplateSelections[i] = true;
                break;
            }

            if (matchedSelectionIndex < 0)
                return false;

            destinationSlots.Add((pileIndex, cardIndex, matchedSelectionIndex));
        }

        return usedTemplateSelections.All(static used => used);
    }

    private static bool TryFindDeterministicSourceSelectionDestinationSlot(
        NetFullCombatState.PlayerState anchorPlayerState,
        NetFullCombatState.PlayerState templatePlayerState,
        PileType sourcePileType,
        out int destinationPileIndex,
        out int destinationCardIndex)
    {
        destinationPileIndex = -1;
        destinationCardIndex = -1;
        bool foundDestination = false;

        for (int i = 0; i < templatePlayerState.piles.Count; i++)
        {
            NetFullCombatState.CombatPileState templatePileState = templatePlayerState.piles[i];
            if (templatePileState.pileType == sourcePileType)
                continue;

            int anchorPileIndex = FindPileIndex(anchorPlayerState.piles, templatePileState.pileType);
            if (anchorPileIndex < 0)
                return false;

            NetFullCombatState.CombatPileState anchorPileState = anchorPlayerState.piles[anchorPileIndex];
            List<int> unmatchedIndexes = FindUnmatchedCardIndexes(anchorPileState.cards, templatePileState.cards);
            if (unmatchedIndexes.Count == 0)
                continue;
            if (unmatchedIndexes.Count != 1)
                return false;
            if (foundDestination)
                return false;

            foundDestination = true;
            destinationPileIndex = i;
            destinationCardIndex = unmatchedIndexes[0];
        }

        return foundDestination;
    }

    private static bool TryFindSourceSelectionDestinationSlot(
        NetFullCombatState.PlayerState anchorPlayerState,
        NetFullCombatState.PlayerState templatePlayerState,
        PileType sourcePileType,
        NetFullCombatState.CardState templateSelectedCardState,
        out int destinationPileIndex,
        out int destinationCardIndex)
    {
        destinationPileIndex = -1;
        destinationCardIndex = -1;
        bool foundDestination = false;

        for (int i = 0; i < templatePlayerState.piles.Count; i++)
        {
            NetFullCombatState.CombatPileState templatePileState = templatePlayerState.piles[i];
            if (templatePileState.pileType == sourcePileType)
                continue;

            int anchorPileIndex = FindPileIndex(anchorPlayerState.piles, templatePileState.pileType);
            if (anchorPileIndex < 0)
                return false;

            NetFullCombatState.CombatPileState anchorPileState = anchorPlayerState.piles[anchorPileIndex];
            List<int> unmatchedIndexes = FindUnmatchedCardIndexes(anchorPileState.cards, templatePileState.cards);
            foreach (int unmatchedIndex in unmatchedIndexes)
            {
                if (!PacketDataEquals(templatePileState.cards[unmatchedIndex], templateSelectedCardState))
                    continue;

                if (foundDestination)
                    return false;

                foundDestination = true;
                destinationPileIndex = i;
                destinationCardIndex = unmatchedIndex;
            }
        }

        return true;
    }

    private static List<int> FindUnmatchedCardIndexes(
        IReadOnlyList<NetFullCombatState.CardState> anchorCards,
        IReadOnlyList<NetFullCombatState.CardState> templateCards)
    {
        bool[] used = new bool[templateCards.Count];
        foreach (NetFullCombatState.CardState anchorCard in anchorCards)
        {
            int matchIndex = FindMatchingCardStateIndex(templateCards, anchorCard, used);
            if (matchIndex >= 0)
                used[matchIndex] = true;
        }

        List<int> unmatchedIndexes = [];
        for (int i = 0; i < used.Length; i++)
        {
            if (!used[i])
                unmatchedIndexes.Add(i);
        }

        return unmatchedIndexes;
    }

    private void RewriteReplayChoiceBranch(UndoSnapshot anchorSnapshot, UndoSnapshot branchSnapshot)
    {
        if (_combatReplay == null || branchSnapshot.ChoiceResultKey == null)
            return;

        NetPlayerChoiceResult? choiceResult = TryBuildReplayChoiceResult(anchorSnapshot.ChoiceSpec, branchSnapshot.ChoiceResultKey);
        if (choiceResult == null)
            return;

        int startIndex = Math.Max(0, anchorSnapshot.ReplayEventCount);
        int endIndex = Math.Min(branchSnapshot.ReplayEventCount, _combatReplay.Events.Count);
        for (int i = startIndex; i < endIndex; i++)
        {
            CombatReplayEvent replayEvent = _combatReplay.Events[i];
            if (replayEvent.eventType != CombatReplayEventType.PlayerChoice)
                continue;

            replayEvent.playerChoiceResult = choiceResult.Value;
            _combatReplay.Events[i] = replayEvent;
            return;
        }
    }

    private static NetPlayerChoiceResult? TryBuildReplayChoiceResult(UndoChoiceSpec? choiceSpec, UndoChoiceResultKey selectedKey)
    {
        if (choiceSpec == null)
            return null;

        if (choiceSpec.Kind is UndoChoiceKind.ChooseACard or UndoChoiceKind.SimpleGridSelection)
        {
            if (selectedKey.OptionIndexes.Count != 1)
                return null;

            return PlayerChoiceResult.FromIndex(selectedKey.OptionIndexes[0]).ToNetData();
        }

        if (choiceSpec.Kind == UndoChoiceKind.SimpleGridSelection)
        {
            return new NetPlayerChoiceResult
            {
                type = PlayerChoiceType.Index,
                indexes = [.. selectedKey.OptionIndexes]
            };
        }

        if (choiceSpec.Kind != UndoChoiceKind.HandSelection)
            return null;

        List<NetCombatCard> combatCards = [];
        foreach (int optionIndex in selectedKey.OptionIndexes)
        {
            if (optionIndex < 0 || optionIndex >= choiceSpec.SourcePileCombatCards.Count)
                return null;

            combatCards.Add(choiceSpec.SourcePileCombatCards[optionIndex]);
        }

        return new NetPlayerChoiceResult
        {
            type = PlayerChoiceType.CombatCard,
            combatCards = combatCards
        };
    }

    private static bool TryGetComparablePlayerStates(
        NetFullCombatState anchorState,
        NetFullCombatState branchState,
        out int branchPlayerIndex,
        out NetFullCombatState.PlayerState anchorPlayerState,
        out NetFullCombatState.PlayerState branchPlayerState)
    {
        branchPlayerIndex = -1;
        anchorPlayerState = default;
        branchPlayerState = default;
        if (branchState.Players.Count == 0)
            return false;

        ulong playerId = LocalContext.NetId ?? branchState.Players[0].playerId;
        if (!TryGetPlayerState(anchorState, playerId, out _, out anchorPlayerState))
            return false;
        if (!TryGetPlayerState(branchState, playerId, out branchPlayerIndex, out branchPlayerState))
            return false;

        return true;
    }

    private static bool TryGetPlayerState(
        NetFullCombatState fullState,
        ulong playerId,
        out int playerIndex,
        out NetFullCombatState.PlayerState playerState)
    {
        for (int i = 0; i < fullState.Players.Count; i++)
        {
            if (fullState.Players[i].playerId != playerId)
                continue;

            playerIndex = i;
            playerState = fullState.Players[i];
            return true;
        }

        playerIndex = -1;
        playerState = default;
        return false;
    }

    private static bool TryFindGeneratedCombatCard(
        NetFullCombatState.PlayerState anchorPlayerState,
        NetFullCombatState.PlayerState branchPlayerState,
        out int pileIndex,
        out int cardIndex,
        out NetFullCombatState.CardState generatedCardState)
    {
        pileIndex = -1;
        cardIndex = -1;
        generatedCardState = default;
        bool foundGeneratedCard = false;

        for (int i = 0; i < branchPlayerState.piles.Count; i++)
        {
            NetFullCombatState.CombatPileState branchPileState = branchPlayerState.piles[i];
            int anchorPileIndex = FindPileIndex(anchorPlayerState.piles, branchPileState.pileType);
            if (anchorPileIndex < 0)
                return false;

            NetFullCombatState.CombatPileState anchorPileState = anchorPlayerState.piles[anchorPileIndex];
            if (!TryFindExtraCardIndexes(anchorPileState.cards, branchPileState.cards, out List<int> extraCardIndexes))
                return false;

            if (extraCardIndexes.Count == 0)
                continue;

            if (extraCardIndexes.Count != 1 || foundGeneratedCard)
                return false;

            foundGeneratedCard = true;
            pileIndex = i;
            cardIndex = extraCardIndexes[0];
            generatedCardState = branchPileState.cards[cardIndex];
        }

        return foundGeneratedCard;
    }

    private static bool TryFindChooseACardTemplateSlot(
        NetFullCombatState.PlayerState anchorPlayerState,
        NetFullCombatState.PlayerState templatePlayerState,
        SerializableCard templateOptionCard,
        out int pileIndex,
        out int cardIndex,
        out NetFullCombatState.CardState generatedCardState)
    {
        pileIndex = -1;
        cardIndex = -1;
        generatedCardState = default;
        bool foundGeneratedCard = false;

        for (int i = 0; i < templatePlayerState.piles.Count; i++)
        {
            NetFullCombatState.CombatPileState templatePileState = templatePlayerState.piles[i];
            int anchorPileIndex = FindPileIndex(anchorPlayerState.piles, templatePileState.pileType);
            if (anchorPileIndex < 0)
                return false;

            NetFullCombatState.CombatPileState anchorPileState = anchorPlayerState.piles[anchorPileIndex];
            List<int> unmatchedIndexes = FindUnmatchedCardIndexes(anchorPileState.cards, templatePileState.cards);
            foreach (int unmatchedIndex in unmatchedIndexes)
            {
                SerializableCard templateCard = templatePileState.cards[unmatchedIndex].card;
                if (!templateCard.Equals(templateOptionCard))
                    continue;

                if (foundGeneratedCard)
                    return false;

                foundGeneratedCard = true;
                pileIndex = i;
                cardIndex = unmatchedIndex;
                generatedCardState = templatePileState.cards[unmatchedIndex];
            }
        }

        return foundGeneratedCard;
    }

    private static int FindPileIndex(IReadOnlyList<NetFullCombatState.CombatPileState> pileStates, PileType pileType)
    {
        for (int i = 0; i < pileStates.Count; i++)
        {
            if (pileStates[i].pileType == pileType)
                return i;
        }

        return -1;
    }

    private static bool TryFindExtraCardIndexes(
        IReadOnlyList<NetFullCombatState.CardState> baseCards,
        IReadOnlyList<NetFullCombatState.CardState> cardsWithExtra,
        out List<int> extraCardIndexes)
    {
        extraCardIndexes = [];
        if (cardsWithExtra.Count < baseCards.Count)
            return false;

        bool[] used = new bool[cardsWithExtra.Count];
        foreach (NetFullCombatState.CardState baseCard in baseCards)
        {
            int matchIndex = FindMatchingCardStateIndex(cardsWithExtra, baseCard, used);
            if (matchIndex < 0)
                return false;

            used[matchIndex] = true;
        }

        for (int i = 0; i < used.Length; i++)
        {
            if (!used[i])
                extraCardIndexes.Add(i);
        }

        return true;
    }

    private static int FindMatchingCardStateIndex(
        IReadOnlyList<NetFullCombatState.CardState> cards,
        NetFullCombatState.CardState targetCard,
        bool[] used)
    {
        for (int i = 0; i < cards.Count; i++)
        {
            if (used[i] || !PacketDataEquals(cards[i], targetCard))
                continue;

            return i;
        }

        return -1;
    }

    private static NetFullCombatState.CardState CreateChooseACardCardState(
        SerializableCard selectedOptionCard,
        NetFullCombatState.CardState templateCardState)
    {
        CardModel replacementCard = CardModel.FromSerializable(ClonePacketSerializable(selectedOptionCard));
        NetFullCombatState.CardState cardState = NetFullCombatState.CardState.From(replacementCard);
        cardState.card.Props = templateCardState.card.Props == null ? null : ClonePacketSerializable(templateCardState.card.Props);
        cardState.card.FloorAddedToDeck = templateCardState.card.FloorAddedToDeck;
        return cardState;
    }

    private async Task<UndoChoiceResultKey?> ShowSyntheticChoiceSelectionAsync(UndoSyntheticChoiceSession session)
    {
        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        Player? me = combatState == null ? null : LocalContext.GetMe(combatState);
        if (me == null)
            return null;

        return session.ChoiceSpec.Kind switch
        {
            UndoChoiceKind.ChooseACard => await ShowSyntheticChooseACardAsync(session.ChoiceSpec, me),
            UndoChoiceKind.HandSelection => await ShowSyntheticHandSelectionAsync(session.ChoiceSpec, me),
            UndoChoiceKind.SimpleGridSelection => await ShowSyntheticSimpleGridAsync(session.ChoiceSpec, me),
            _ => null
        };
    }

    private static async Task<UndoChoiceResultKey?> ShowSyntheticChooseACardAsync(UndoChoiceSpec choiceSpec, Player me)
    {
        NChooseACardSelectionScreen? screen = await RunOnMainThreadAsync(
            () => NChooseACardSelectionScreen.ShowScreen(choiceSpec.BuildOptionCards(me), choiceSpec.CanSkip));
        if (screen == null)
            return null;

        IEnumerable<CardModel> selectedCards = await screen.CardsSelected();
        return choiceSpec.TryMapSyntheticSelection(me, selectedCards);
    }

    private static async Task<UndoChoiceResultKey?> ShowSyntheticHandSelectionAsync(UndoChoiceSpec choiceSpec, Player me)
    {
        NPlayerHand? hand = NCombatRoom.Instance?.Ui?.Hand;
        if (hand == null)
            return null;

        Task<IEnumerable<CardModel>>? selectionTask = await RunOnMainThreadAsync(() =>
        {
            hand.CancelAllCardPlay();
            return hand.SelectCards(choiceSpec.SelectionPrefs, choiceSpec.BuildHandFilter(me), null);
        });
        if (selectionTask == null)
            return null;

        IEnumerable<CardModel> selectedCards = await selectionTask;
        return choiceSpec.TryMapSyntheticSelection(me, selectedCards);
    }

    private static async Task<UndoChoiceResultKey?> ShowSyntheticSimpleGridAsync(UndoChoiceSpec choiceSpec, Player me)
    {
        NSimpleCardSelectScreen? screen = await RunOnMainThreadAsync(() =>
        {
            NOverlayStack? overlayStack = NOverlayStack.Instance;
            if (overlayStack == null)
                return null;

            NSimpleCardSelectScreen selectionScreen = NSimpleCardSelectScreen.Create(choiceSpec.BuildOptionCards(me), choiceSpec.SelectionPrefs);
            overlayStack.Push(selectionScreen);
            return selectionScreen;
        });
        if (screen == null)
            return null;

        IEnumerable<CardModel> selectedCards = await screen.CardsSelected();
        return choiceSpec.TryMapSyntheticSelection(me, selectedCards);
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
        _lastRestoreFailureReason = null;
        _lastRestoreCapabilityReport = RestoreCapabilityReport.SupportedReport();
        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        if (runState == null || combatState == null)
        {
            _lastRestoreFailureReason = "missing_run_state";
            return false;
        }

        if (snapshot.SchemaVersion < UndoCombatFullState.CurrentSchemaVersion)
        {
            _lastRestoreFailureReason = "schema_version_mismatch";
            _lastRestoreCapabilityReport = new RestoreCapabilityReport
            {
                Result = RestoreCapabilityResult.SchemaMismatch,
                Detail = $"snapshot_schema={snapshot.SchemaVersion}"
            };
            return false;
        }

        if (!CanApplyFullStateInPlace(snapshot.FullState, runState, combatState, out string reason))
        {
            _lastRestoreFailureReason = reason;
            MainFile.Logger.Warn($"Skipped in-place restore. Reason={reason}");
            return false;
        }

        NRun? currentRun = NGame.Instance?.CurrentRunNode;
        bool restoreVisible = currentRun?.Visible ?? false;
        if (hideCurrentRun && currentRun != null)
            currentRun.Visible = false;

        try
        {
            await DismissSupportedChoiceUiIfPresentAsync();
            ResetActionExecutorForRestore();
            RunManager.Instance.ActionQueueSet.Reset();
            RebuildActionQueues(runState.Players);
            runState.Rng.LoadFromSerializable(snapshot.FullState.Rng);

            RestorePlayers(runState, combatState, snapshot);
            RestoreCreatures(runState, combatState, snapshot);

            combatState.RoundNumber = snapshot.RoundNumber;
            combatState.CurrentSide = snapshot.CurrentSide;
            UndoCombatHistoryCodec.Restore(runState, combatState, snapshot.CombatHistoryState);
            foreach (Player player in runState.Players)
                player.PlayerCombatState?.RecalculateCardValues();

            RunManager.Instance.ActionQueueSet.FastForwardNextActionId(snapshot.NextActionId);
            RunManager.Instance.ActionQueueSynchronizer.FastForwardHookId(snapshot.NextHookId);
            RunManager.Instance.ChecksumTracker.LoadReplayChecksums([], snapshot.NextChecksumId);
            RunManager.Instance.PlayerChoiceSynchronizer.FastForwardChoiceIds([.. snapshot.FullState.nextChoiceIds]);

            RestoreCapabilityReport topologyReport = UndoCreatureTopologyCodecRegistry.Restore(snapshot.CreatureTopologyStates, combatState.Creatures);
            if (topologyReport.IsFailure)
            {
                _lastRestoreFailureReason = topologyReport.Detail ?? topologyReport.Result.ToString();
                _lastRestoreCapabilityReport = topologyReport;
                return false;
            }


            RestoreCapabilityReport creatureStatusReport = UndoCreatureStatusCodecRegistry.Restore(snapshot.CreatureStatusRuntimeStates, combatState.Creatures);
            if (creatureStatusReport.IsFailure)
            {
                _lastRestoreFailureReason = creatureStatusReport.Detail ?? creatureStatusReport.Result.ToString();
                _lastRestoreCapabilityReport = creatureStatusReport;
                return false;
            }

            RestoreCapabilityReport creatureReconciliationReport = UndoCreatureReconciliationCodecRegistry.Restore(snapshot.MonsterStates, combatState.Creatures);
            if (creatureReconciliationReport.IsFailure)
            {
                _lastRestoreFailureReason = creatureReconciliationReport.Detail ?? creatureReconciliationReport.Result.ToString();
                _lastRestoreCapabilityReport = creatureReconciliationReport;
                return false;
            }
            RestoreCapabilityReport actionKernelReport = UndoActionKernelService.Restore(snapshot.ActionKernelState, runState);
            _lastRestoreCapabilityReport = actionKernelReport;
            if (actionKernelReport.IsFailure)
            {
                _lastRestoreFailureReason = actionKernelReport.Detail ?? actionKernelReport.Result.ToString();
                return false;
            }
            ResetActionExecutorForRestore();
            ActionSynchronizerCombatState effectiveSynchronizerState = GetEffectiveSynchronizerState(snapshot);
            if (!RestoreActionSynchronizationState(effectiveSynchronizerState, snapshot.ActionKernelState.BoundaryKind, out reason))
            {
                _lastRestoreFailureReason = reason;
                _lastRestoreCapabilityReport = new RestoreCapabilityReport
                {
                    Result = RestoreCapabilityResult.QueueStateMismatch,
                    Detail = reason
                };
                return false;
            }
            RebuildTransientCombatCaches(runState, snapshot.CombatCardDbState);
            NormalizeRelicInventoryUi(runState);
            ApplyFirstInSeriesPlayCountOverrides(snapshot);

            foreach (Player player in runState.Players)
            {
                if (player.Creature.IsAlive)
                    player.ActivateHooks();
                else
                    player.DeactivateHooks();
            }

            await RefreshCombatUiAsync(combatState);
            if (!TryValidateRestoredState(snapshot, hideCurrentRun, out reason))
            {
                _lastRestoreFailureReason = reason;
                MainFile.Logger.Warn($"Restore validation failed. Reason={reason}");
                WriteInteractionLog("restore_noop", $"reason={reason}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _lastRestoreFailureReason = DescribeException(ex);
            MainFile.Logger.Warn($"Full-state restore failed. {DescribeException(ex)}");
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

    private void TryCaptureSnapshot(
        UndoActionKind actionKind,
        int replayEventCount,
        string actionLabel,
        bool isChoiceAnchor = false,
        UndoChoiceSpec? choiceSpec = null,
        UndoChoiceResultKey? choiceResultKey = null)
    {
        if (!CanCaptureSnapshot())
            return;

        try
        {
            replayEventCount = Math.Max(0, replayEventCount);
            UndoSnapshot snapshot = new(
                CaptureCurrentCombatFullState(),
                replayEventCount,
                actionKind,
                _nextSequenceId++,
                actionLabel,
                isChoiceAnchor,
                choiceSpec,
                choiceResultKey);
            _pastSnapshots.AddFirst(snapshot);
            TrimSnapshots(_pastSnapshots);
            _futureSnapshots.Clear();
            MainFile.Logger.Info($"Captured snapshot #{snapshot.SequenceId}: {snapshot.ActionLabel}. ReplayEvents={snapshot.ReplayEventCount}, UndoCount={_pastSnapshots.Count}");
            UndoDebugLog.Write($"snapshot captured seq={snapshot.SequenceId} label={snapshot.ActionLabel} replayEvents={snapshot.ReplayEventCount} undoCount={_pastSnapshots.Count}");
            NotifyStateChanged();
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"Failed to capture snapshot for {actionKind}: {ex}");
        }
    }

    private UndoChoiceSpec? TakePendingChoiceSpec(GameAction action, bool clearWhenNotCaptured = true)
    {
        UndoChoiceSpec? choiceSpec = _pendingChoiceSpec;
        bool shouldCapture = ShouldCapturePlayerChoice(action, out _);
        if (shouldCapture || clearWhenNotCaptured)
            _pendingChoiceSpec = null;

        return shouldCapture ? choiceSpec : null;
    }

    private bool CanCaptureSnapshot()
    {
        return !IsRestoring && IsSinglePlayerCombat() && EnsureCombatReplayInitialized();
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

        if (!CombatManager.Instance.EndingPlayerTurnPhaseOne && !CombatManager.Instance.EndingPlayerTurnPhaseTwo)
            return true;

        NCombatUi? combatUi = NCombatRoom.Instance?.Ui;
        GameAction? action = RunManager.Instance.ActionExecutor.CurrentlyRunningAction;
        if (action?.State == GameActionState.GatheringPlayerChoice)
            return true;

        return CanRestoreResolvedChoiceBranch(_pastSnapshots.First?.Value, action);
    }

    private static bool IsSinglePlayerCombat()
    {
        return RunManager.Instance.IsSinglePlayerOrFakeMultiplayer
            && CombatManager.Instance.IsInProgress
            && NGame.Instance?.CurrentRunNode != null;
    }

    private static bool IsSupportedChoiceAnchorKind(UndoChoiceSpec? choiceSpec)
    {
        return choiceSpec?.Kind is UndoChoiceKind.ChooseACard or UndoChoiceKind.HandSelection or UndoChoiceKind.SimpleGridSelection;
    }

    private bool CanRestoreResolvedChoiceBranch(UndoSnapshot? snapshot, GameAction? action)
    {
        if (snapshot?.IsChoiceAnchor != true
            || !IsSupportedChoiceAnchorKind(snapshot.ChoiceSpec)
            || snapshot.ReplayEventCount != GetCurrentReplayEventCount()
            || _lastResolvedChoiceResultKey == null
            || action == null)
        {
            return false;
        }

        ulong? localNetId = LocalContext.NetId;
        if (localNetId != null && action.OwnerId != localNetId.Value)
        {
            CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
            if (combatState?.Players.Count > 1)
                return false;
        }

        return true;
    }

    private bool MoveCurrentChoiceAnchorToDestinationIfNeeded(LinkedList<UndoSnapshot> source, LinkedList<UndoSnapshot> destination)
    {
        if (source.First?.Value is not UndoSnapshot snapshot
            || !snapshot.IsChoiceAnchor
            || source.First.Next == null
            || !IsCurrentStateAtChoiceAnchor(snapshot))
        {
            return false;
        }

        if (IsAlwaysHiddenUndoChoiceAnchorNode(source.First))
            return false;

        source.RemoveFirst();
        destination.AddFirst(snapshot);
        TrimSnapshots(destination);
        MainFile.Logger.Info("Skipped restoring the current player choice anchor because the choice UI is already active.");
        return true;
    }


    private bool IsUiBlocking(NCombatUi combatUi)
    {
        return IsUndoRedoTemporarilyBlocked(combatUi) || IsCombatUiTransitioning(combatUi);
    }
    private static bool IsCombatUiTransitioning(NCombatUi combatUi)
    {
        if (!CombatManager.Instance.EndingPlayerTurnPhaseOne && !CombatManager.Instance.EndingPlayerTurnPhaseTwo)
            return false;

        GameAction? action = RunManager.Instance.ActionExecutor.CurrentlyRunningAction;
        return action?.State != GameActionState.GatheringPlayerChoice || !IsSupportedChoiceUiActive(combatUi);
    }

    private UndoSnapshot? GetCurrentChoiceAnchorSnapshot()
    {
        if (_syntheticChoiceSession != null)
            return _syntheticChoiceSession.AnchorSnapshot;

        UndoSnapshot? snapshot = _pastSnapshots.First?.Value;
        return snapshot?.IsChoiceAnchor == true ? snapshot : null;
    }

    private UndoChoiceSpec? ResolveCurrentChoiceSpec(UndoSnapshot restoredSnapshot, int replayEventCount)
    {
        return restoredSnapshot.ChoiceSpec
            ?? FindChoiceSpecInHistory(replayEventCount)
            ?? TryCaptureCurrentChoiceSpecFromUi()
            ?? TryCaptureChoiceSpecFromActionContext(restoredSnapshot);
    }

    private static UndoChoiceSpec? TryCaptureChoiceSpecFromActionContext(UndoSnapshot restoredSnapshot)
    {
        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        Player? me = combatState == null ? null : LocalContext.GetMe(combatState);
        if (me == null)
            return null;

        GameAction? action = RunManager.Instance.ActionExecutor.CurrentlyRunningAction;
        if (restoredSnapshot.ActionKind == UndoActionKind.EndTurn
            && action?.State == GameActionState.GatheringPlayerChoice)
        {
            return TryCreateWellLaidPlansChoiceSpec(me)
                ?? TryCreateEntropyChoiceSpec(me, action);
        }

        return TryCreateEntropyChoiceSpec(me, action);
    }

    private static UndoChoiceSpec? TryCaptureChoiceSpecFromCurrentActionContext(GameAction action)
    {
        if (action.State != GameActionState.GatheringPlayerChoice)
            return null;

        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        Player? me = combatState == null ? null : LocalContext.GetMe(combatState);
        if (me == null)
            return null;

        return TryCreateWellLaidPlansChoiceSpec(me)
            ?? TryCreateEntropyChoiceSpec(me, action);
    }

    private static UndoChoiceSpec? TryCreateWellLaidPlansChoiceSpec(Player me)
    {
        WellLaidPlansPower? wellLaidPlans = me.Creature.GetPower<WellLaidPlansPower>();
        if (wellLaidPlans == null)
            return null;

        LocString prompt = FindProperty(wellLaidPlans.GetType(), "SelectionScreenPrompt")?.GetValue(wellLaidPlans) as LocString
            ?? new LocString(string.Empty, string.Empty);
        CardSelectorPrefs prefs = new(prompt, 0, wellLaidPlans.Amount);
        return UndoChoiceSpec.CreateHandSelection(me, prefs, static card => !card.ShouldRetainThisTurn);
    }

    private static UndoChoiceSpec? TryCreateEntropyChoiceSpec(Player me, GameAction? action)
    {
        if (action is not GenericHookGameAction)
            return null;

        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        if (combatState?.CurrentSide != CombatSide.Player)
            return null;

        EntropyPower? entropy = me.Creature.GetPower<EntropyPower>();
        if (entropy == null)
            return null;

        CardSelectorPrefs prefs = new(CardSelectorPrefs.TransformSelectionPrompt, entropy.Amount);
        return UndoChoiceSpec.CreateHandSelection(me, prefs, null);
    }

    private UndoChoiceSpec? FindChoiceSpecInHistory(int replayEventCount)
    {
        if (_syntheticChoiceSession?.AnchorSnapshot.ReplayEventCount == replayEventCount)
            return _syntheticChoiceSession.ChoiceSpec;

        foreach (UndoSnapshot snapshot in _pastSnapshots)
        {
            if (snapshot.IsChoiceAnchor && snapshot.ReplayEventCount == replayEventCount && snapshot.ChoiceSpec != null)
                return snapshot.ChoiceSpec;
        }

        foreach (UndoSnapshot snapshot in _futureSnapshots)
        {
            if (snapshot.IsChoiceAnchor && snapshot.ReplayEventCount == replayEventCount && snapshot.ChoiceSpec != null)
                return snapshot.ChoiceSpec;
        }

        return null;
    }

    private static UndoChoiceSpec? TryCaptureCurrentChoiceSpecFromUi()
    {
        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        Player? me = combatState == null ? null : LocalContext.GetMe(combatState);
        if (me == null)
            return null;

        NCombatUi? combatUi = NCombatRoom.Instance?.Ui;
        if (combatUi?.Hand?.IsInCardSelection == true
            && FindField(combatUi.Hand.GetType(), "_prefs")?.GetValue(combatUi.Hand) is CardSelectorPrefs handPrefs)
        {
            Func<CardModel, bool>? handFilter = FindField(combatUi.Hand.GetType(), "_currentSelectionFilter")?.GetValue(combatUi.Hand) as Func<CardModel, bool>;
            return UndoChoiceSpec.CreateHandSelection(me, handPrefs, handFilter);
        }

        if (NOverlayStack.Instance?.Peek() is NChooseACardSelectionScreen chooseScreen
            && GetPrivateFieldValue<IReadOnlyList<CardModel>>(chooseScreen, "_cards") is { } chooseCards)
        {
            bool canSkip = FindField(chooseScreen.GetType(), "_canSkip")?.GetValue(chooseScreen) is true;
            return UndoChoiceSpec.CreateChooseACard(chooseCards, canSkip);
        }

        if (NOverlayStack.Instance?.Peek() is NCardGridSelectionScreen gridScreen
            && GetPrivateFieldValue<IReadOnlyList<CardModel>>(gridScreen, "_cards") is { } gridCards
            && FindField(gridScreen.GetType(), "_prefs")?.GetValue(gridScreen) is CardSelectorPrefs gridPrefs)
        {
            return UndoChoiceSpec.CreateSimpleGridSelection(me, gridCards, gridPrefs);
        }

        return null;
    }

    private bool IsCurrentStateAtChoiceAnchor(UndoSnapshot snapshot, bool includeResolvedChoiceBranch = false)
    {
        NCombatUi? combatUi = NCombatRoom.Instance?.Ui;
        if (combatUi == null || snapshot.ReplayEventCount != GetCurrentReplayEventCount())
            return false;

        if (_syntheticChoiceSession?.AnchorSnapshot.SequenceId == snapshot.SequenceId)
            return IsSupportedChoiceUiActive(combatUi);

        GameAction? action = RunManager.Instance.ActionExecutor.CurrentlyRunningAction;
        if (action?.State == GameActionState.GatheringPlayerChoice
            && IsSupportedChoiceUiActive(combatUi))
        {
            return true;
        }

        return includeResolvedChoiceBranch
            && ReferenceEquals(_pastSnapshots.First?.Value, snapshot)
            && CanRestoreResolvedChoiceBranch(snapshot, action);
    }

    private static bool IsSupportedChoiceUiActive(NCombatUi combatUi)
    {
        if (NCombatRoom.Instance?.Ui != combatUi)
            return false;

        if (combatUi.Hand.IsInCardSelection)
            return true;

        return NOverlayStack.Instance?.Peek() is NChooseACardSelectionScreen or NCardGridSelectionScreen;
    }

    private async Task DismissSupportedChoiceUiIfPresentAsync()
    {
        _syntheticChoiceSession = null;

        bool removedOverlay = false;
        if (NOverlayStack.Instance?.Peek() is IOverlayScreen choiceScreen
            && choiceScreen is NChooseACardSelectionScreen or NCardGridSelectionScreen)
        {
            RemoveChoiceOverlaySafely(choiceScreen);
            removedOverlay = true;
        }

        NPlayerHand? hand = NCombatRoom.Instance?.Ui?.Hand;
        if (hand != null)
        {
            TaskCompletionSource<IEnumerable<CardModel>>? selectionCompletionSource = GetPrivateFieldValue<TaskCompletionSource<IEnumerable<CardModel>>>(hand, "_selectionCompletionSource");
            Task<IEnumerable<CardModel>>? selectionTask = selectionCompletionSource?.Task;
            bool hasSelectedCards = GetPrivateFieldValue<System.Collections.IList>(hand, "_selectedCards")?.Count > 0;
            bool isSelectionPending = hand.IsInCardSelection || hasSelectedCards || (selectionTask != null && !selectionTask.IsCompleted);
            if (isSelectionPending)
            {
                DetachPendingHandSelectionSource(hand);
                selectionCompletionSource?.TrySetCanceled();
                SetPrivateFieldValue(hand, "_selectionCompletionSource", null);
                InvokePrivateMethod(hand, "AfterCardsSelected", [null]);
                await WaitOneFrameAsync();
                await WaitOneFrameAsync();
                RestoreSelectedHandCardsToHand(hand);
                ResetSelectedHandCardContainerState(hand);
                ResetPlayerHandUi(hand);
                _pendingHandChoiceSource = null;
            }
        }

        if (removedOverlay)
            await WaitOneFrameAsync();
    }

    private static void RemoveChoiceOverlaySafely(IOverlayScreen choiceScreen)
    {
        NOverlayStack? overlayStack = NOverlayStack.Instance;
        Node? choiceNode = choiceScreen as Node;
        if (overlayStack == null)
        {
            choiceNode?.QueueFreeSafelyNoPool();
            return;
        }

        try
        {
            overlayStack.Remove(choiceScreen);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Warn($"Forced removal of broken choice overlay after exception: {ex}");
            if (FindField(typeof(NOverlayStack), "_overlays")?.GetValue(overlayStack) is System.Collections.IList overlays)
                overlays.Remove(choiceScreen);

            Node? parent = choiceNode?.GetParent();
            parent?.RemoveChildSafely(choiceNode);
            choiceNode?.QueueFreeSafelyNoPool();
            InvokePrivateMethod(overlayStack, "HideBackstop");
            ActiveScreenContext.Instance.Update();
            overlayStack.EmitSignal(NOverlayStack.SignalName.Changed);
        }
    }

    private static bool ShouldCaptureAction(GameAction action)
    {
        if (!IsSinglePlayerCombat())
            return false;

        if (!ActionQueueSet.IsGameActionPlayerDriven(action))
            return false;

        ulong? localNetId = LocalContext.NetId;
        if (localNetId == null || action.OwnerId == localNetId.Value)
            return true;

        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        return combatState?.Players.Count <= 1;
    }

    private bool ShouldCapturePlayerChoice(GameAction action, out string? skipReason)
    {
        skipReason = null;
        if (!IsSinglePlayerCombat())
        {
            skipReason = "not_single_player";
            return false;
        }

        ulong? localNetId = LocalContext.NetId;
        if (localNetId != null && action.OwnerId != localNetId.Value)
        {
            CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
            if (combatState?.Players.Count > 1)
            {
                skipReason = "not_local";
                return false;
            }
        }

        if (action.State != GameActionState.GatheringPlayerChoice && _pendingChoiceSpec == null)
        {
            skipReason = "not_choice_boundary";
            return false;
        }

        return true;
    }

    internal static string? DebugGetLastInteractionStage()
    {
        return _lastInteractionStage;
    }

    internal static int DebugGetHiddenChoiceAnchorSkipCount()
    {
        return _hiddenChoiceAnchorSkipCount;
    }

    private static void WriteInteractionLog(string stage, string? extra = null)
    {
        _lastInteractionStage = stage;
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
        _pendingChoiceSpec = null;
        _lastResolvedChoiceSpec = null;
        _pendingHandChoiceSource = null;
        _lastResolvedChoiceResultKey = null;
        _syntheticChoiceSession = null;
        ClearTurnTransitionBlock();
        ClearFirstInSeriesPlayCountOverrides();
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
            && EnsureCombatReplayInitialized()
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

    private UndoCombatFullState CaptureCurrentCombatFullState()
    {
        RunState runState = RunManager.Instance.DebugOnlyGetState()
            ?? throw new InvalidOperationException("Run state was not available while capturing undo snapshot.");
        CombatState combatState = CombatManager.Instance.DebugOnlyGetState()
            ?? throw new InvalidOperationException("Combat state was not available while capturing undo snapshot.");
        UndoSelectionSessionState selectionSessionState = CaptureSelectionSessionState();

        return new UndoCombatFullState(
            CloneFullState(NetFullCombatState.FromRun(runState, null)),
            combatState.RoundNumber,
            combatState.CurrentSide,
            RunManager.Instance.ActionQueueSynchronizer.CombatState,
            RunManager.Instance.ActionQueueSet.NextActionId,
            RunManager.Instance.ActionQueueSynchronizer.NextHookId,
            RunManager.Instance.ChecksumTracker.NextId,
            UndoCombatHistoryCodec.Capture(runState, combatState),
            UndoActionKernelService.Capture(runState, selectionSessionState.ChoiceSpec),
            CaptureMonsterStates(combatState.Creatures),
            CaptureCardCostStates(runState),
            CaptureCardRuntimeStates(runState, combatState),
            CapturePowerRuntimeStates(runState, combatState),
            CaptureRelicRuntimeStates(runState, combatState),
            selectionSessionState,
            CaptureFirstInSeriesPlayCounts(combatState),
            creatureTopologyStates: UndoCreatureTopologyCodecRegistry.Capture(combatState.Creatures),
            creatureStatusRuntimeStates: UndoCreatureStatusCodecRegistry.Capture(combatState.Creatures),
            combatCardDbState: CaptureCombatCardDbState(runState),
            playerOrbStates: CapturePlayerOrbStates(runState),
            playerDeckStates: CapturePlayerDeckStates(runState));
    }

    private static IReadOnlyList<UndoPlayerOrbState> CapturePlayerOrbStates(RunState runState)
    {
        return [.. runState.Players.Select(player => new UndoPlayerOrbState
        {
            PlayerNetId = player.NetId,
            BaseOrbSlotCount = player.BaseOrbSlotCount,
            Capacity = player.PlayerCombatState?.OrbQueue.Capacity ?? player.BaseOrbSlotCount,
            Orbs = player.PlayerCombatState == null
                ? []
                : [.. player.PlayerCombatState.OrbQueue.Orbs.Select(CaptureOrbRuntimeState)]
        })];
    }

    private static UndoOrbRuntimeState CaptureOrbRuntimeState(OrbModel orb)
    {
        return new UndoOrbRuntimeState
        {
            OrbId = orb.Id,
            DarkEvokeValue = orb is DarkOrb darkOrb && FindField(darkOrb.GetType(), "_evokeVal")?.GetValue(darkOrb) is decimal darkEvokeValue
                ? darkEvokeValue
                : null,
            GlassPassiveValue = orb is GlassOrb glassOrb && FindField(glassOrb.GetType(), "_passiveVal")?.GetValue(glassOrb) is decimal glassPassiveValue
                ? glassPassiveValue
                : null
        };
    }

    private static IReadOnlyList<UndoPlayerDeckState> CapturePlayerDeckStates(RunState runState)
    {
        return [.. runState.Players.Select(player => new UndoPlayerDeckState
        {
            PlayerNetId = player.NetId,
            Cards = [.. player.Deck.Cards.Select(card => ClonePacketSerializable(card.ToSerializable()))]
        })];
    }

    private static IReadOnlyList<UndoMonsterState> CaptureMonsterStates(IReadOnlyList<Creature> creatures)
    {
        List<UndoMonsterState> states = [];
        for (int i = 0; i < creatures.Count; i++)
        {
            Creature creature = creatures[i];
            MonsterModel? monster = creature.Monster;
            if (monster?.MoveStateMachine == null)
                continue;

            MonsterMoveStateMachine moveStateMachine = monster.MoveStateMachine;
            string creatureKey = BuildCreatureKey(creature, i);
            string? currentStateId = GetPrivateFieldValue<MonsterState>(moveStateMachine, "_currentState")?.Id;
            bool performedFirstMove = FindField(moveStateMachine.GetType(), "_performedFirstMove")?.GetValue(moveStateMachine) is true;
            bool nextMovePerformedAtLeastOnce = monster.NextMove != null
                && FindField(monster.NextMove.GetType(), "_performedAtLeastOnce")?.GetValue(monster.NextMove) is true;
            string? transientNextMoveFollowUpId = monster.NextMove?.Id == MonsterModel.stunnedMoveId
                ? monster.NextMove.FollowUpState?.Id ?? monster.NextMove.FollowUpStateId
                : null;
            string? specialNodeStateKey = creature.Powers.OfType<SwipePower>().Any(static power => power.StolenCard != null)
                ? "%StolenCardPos"
                : null;
            states.Add(new UndoMonsterState
            {
                CreatureKey = creatureKey,
                SlotName = string.IsNullOrWhiteSpace(creature.SlotName) ? null : creature.SlotName,
                CurrentStateId = currentStateId,
                NextMoveId = monster.NextMove?.Id,
                IsHovering = false,
                SpawnedThisTurn = monster.SpawnedThisTurn,
                PerformedFirstMove = performedFirstMove,
                NextMovePerformedAtLeastOnce = nextMovePerformedAtLeastOnce,
                TransientNextMoveFollowUpId = transientNextMoveFollowUpId,
                SpecialNodeStateKey = specialNodeStateKey,
                StateLogIds = [.. moveStateMachine.StateLog.Select(static state => state.Id)]
            });
        }

        return states;
    }

    private static string BuildCreatureKey(Creature creature, int index)
    {
        if (creature.Player != null)
            return $"player:{index}:{creature.Player.NetId}";

        if (creature.PetOwner != null && creature.Monster != null)
            return $"pet:{index}:{creature.PetOwner.NetId}:{creature.Monster.Id.Entry}";

        if (creature.Monster != null)
            return $"monster:{index}:{creature.Monster.Id.Entry}";

        return $"creature:{index}";
    }

    private static string? TryResolveCreatureKey(IReadOnlyList<Creature> creatures, Creature? target)
    {
        if (target == null)
            return null;

        for (int i = 0; i < creatures.Count; i++)
        {
            if (ReferenceEquals(creatures[i], target))
                return BuildCreatureKey(target, i);
        }

        if (target.PetOwner != null && target.Monster != null)
        {
            for (int i = 0; i < creatures.Count; i++)
            {
                Creature candidate = creatures[i];
                if (candidate.PetOwner?.NetId != target.PetOwner.NetId)
                    continue;

                if (candidate.Monster?.Id != target.Monster.Id)
                    continue;

                return BuildCreatureKey(candidate, i);
            }
        }

        if (target.Player != null)
        {
            for (int i = 0; i < creatures.Count; i++)
            {
                if (creatures[i].Player?.NetId == target.Player.NetId)
                    return BuildCreatureKey(creatures[i], i);
            }
        }

        if (target.Monster != null)
        {
            for (int i = 0; i < creatures.Count; i++)
            {
                Creature candidate = creatures[i];
                if (candidate.Monster?.Id == target.Monster.Id)
                    return BuildCreatureKey(candidate, i);
            }
        }

        return null;
    }

    private static IReadOnlyList<UndoPlayerPileCardCostState> CaptureCardCostStates(RunState runState)
    {
        List<UndoPlayerPileCardCostState> states = [];
        foreach (Player player in runState.Players)
        {
            foreach (PileType pileType in CombatPileOrder)
            {
                CardPile? pile = CardPile.Get(pileType, player);
                if (pile == null)
                    continue;

                states.Add(new UndoPlayerPileCardCostState
                {
                    PlayerNetId = player.NetId,
                    PileType = pileType,
                    Cards = [.. pile.Cards.Select(CaptureCardCostState)]
                });
            }
        }

        return states;
    }

    private static IReadOnlyList<UndoPlayerPileCardRuntimeState> CaptureCardRuntimeStates(RunState runState, CombatState combatState)
    {
        UndoRuntimeCaptureContext context = new()
        {
            RunState = runState,
            CombatState = combatState
        };

        List<UndoPlayerPileCardRuntimeState> states = [];
        foreach (Player player in runState.Players)
        {
            foreach (PileType pileType in CombatPileOrder)
            {
                CardPile? pile = CardPile.Get(pileType, player);
                if (pile == null)
                    continue;

                states.Add(new UndoPlayerPileCardRuntimeState
                {
                    PlayerNetId = player.NetId,
                    PileType = pileType,
                    Cards = [.. pile.Cards.Select(card => CaptureCardRuntimeState(card, context))]
                });
            }
        }

        return states;
    }

    private static UndoCardRuntimeState CaptureCardRuntimeState(CardModel card, UndoRuntimeCaptureContext context)
    {
        return CreateCardRuntimeState(
            card,
            CaptureEnchantmentRuntimeState(card.Enchantment),
            UndoRuntimeStateCodecRegistry.CaptureCardStates(card, context));
    }

    private static UndoCardRuntimeState CaptureDefaultCardRuntimeState(CardModel card)
    {
        return CreateCardRuntimeState(card, CaptureEnchantmentRuntimeState(card.Enchantment), []);
    }

    private static UndoCardRuntimeState CreateCardRuntimeState(
        CardModel card,
        UndoEnchantmentRuntimeState? enchantmentState,
        IReadOnlyList<UndoComplexRuntimeState> complexStates)
    {
        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        return new UndoCardRuntimeState
        {
            BaseReplayCount = card.BaseReplayCount,
            HasSingleTurnRetain = FindProperty(card.GetType(), "HasSingleTurnRetain")?.GetValue(card) is bool retain && retain,
            HasSingleTurnSly = FindProperty(card.GetType(), "HasSingleTurnSly")?.GetValue(card) is bool sly && sly,
            ExhaustOnNextPlay = card.ExhaustOnNextPlay,
            DeckVersionRef = runState != null && card.DeckVersion != null
                ? UndoStableRefs.CaptureCardRef(runState, card.DeckVersion)
                : null,
            EnchantmentState = enchantmentState,
            ComplexStates = complexStates
        };
    }
    private static UndoEnchantmentRuntimeState? CaptureEnchantmentRuntimeState(EnchantmentModel? enchantment)
    {
        if (enchantment == null)
            return null;

        return new UndoEnchantmentRuntimeState
        {
            Serializable = ClonePacketSerializable(enchantment.ToSerializable()),
            Status = enchantment.Status,
            BoolProperties = CaptureRuntimeBoolProperties(enchantment),
            IntProperties = CaptureRuntimeIntProperties(enchantment, "Amount"),
            EnumProperties = CaptureRuntimeEnumProperties(enchantment)
        };
    }

    private static IReadOnlyList<UndoPowerRuntimeState> CapturePowerRuntimeStates(RunState runState, CombatState combatState)
    {
        UndoRuntimeCaptureContext context = new()
        {
            RunState = runState,
            CombatState = combatState
        };

        List<UndoPowerRuntimeState> states = [];
        IReadOnlyList<Creature> creatures = combatState.Creatures;
        for (int i = 0; i < creatures.Count; i++)
        {
            Creature creature = creatures[i];
            string ownerCreatureKey = BuildCreatureKey(creature, i);
            Dictionary<ModelId, int> ordinalsByPowerId = [];
            foreach (PowerModel power in creature.Powers)
            {
                int ordinal = ordinalsByPowerId.TryGetValue(power.Id, out int existingOrdinal) ? existingOrdinal : 0;
                ordinalsByPowerId[power.Id] = ordinal + 1;
                states.Add(new UndoPowerRuntimeState
                {
                    OwnerCreatureKey = ownerCreatureKey,
                    PowerId = power.Id,
                    Ordinal = ordinal,
                    TargetCreatureKey = TryResolveCreatureKey(creatures, power.Target),
                    ApplierCreatureKey = TryResolveCreatureKey(creatures, power.Applier),
                    StolenCard = power is SwipePower swipe && swipe.StolenCard != null
                        ? ClonePacketSerializable(swipe.StolenCard.ToSerializable())
                        : null,
                    TriggeredPlayerNetIds = CaptureTriggeredPlayerNetIds(power),
                    BoolProperties = CapturePowerRuntimeBoolProperties(power),
                    IntProperties = CaptureRuntimeIntProperties(power, "Amount"),
                    EnumProperties = CaptureRuntimeEnumProperties(power),
                    ComplexStates = UndoRuntimeStateCodecRegistry.CapturePowerStates(power, context)
                });
            }
        }

        return states;
    }
    private static IReadOnlyList<UndoRelicRuntimeState> CaptureRelicRuntimeStates(RunState runState, CombatState combatState)
    {
        UndoRuntimeCaptureContext context = new()
        {
            RunState = runState,
            CombatState = combatState
        };

        List<UndoRelicRuntimeState> states = [];
        foreach (Player player in runState.Players)
        {
            Dictionary<ModelId, int> ordinalsByRelicId = [];
            foreach (RelicModel relic in player.Relics)
            {
                int ordinal = ordinalsByRelicId.TryGetValue(relic.Id, out int existingOrdinal) ? existingOrdinal : 0;
                ordinalsByRelicId[relic.Id] = ordinal + 1;
                states.Add(new UndoRelicRuntimeState
                {
                    PlayerNetId = player.NetId,
                    RelicId = relic.Id,
                    Ordinal = ordinal,
                    Status = relic.Status,
                    IsActivating = FindProperty(relic.GetType(), "IsActivating")?.GetValue(relic) is bool activating ? activating : null,
                    BoolProperties = CaptureRuntimeBoolProperties(relic, "IsActivating"),
                    IntProperties = CaptureRuntimeIntProperties(relic),
                    EnumProperties = CaptureRuntimeEnumProperties(relic),
                    ComplexStates = UndoRuntimeStateCodecRegistry.CaptureRelicStates(relic, context)
                });
            }
        }

        return states;
    }
    private static UndoSelectionSessionState CaptureSelectionSessionState()
    {
        NCombatUi? combatUi = NCombatRoom.Instance?.Ui;
        object? overlay = NOverlayStack.Instance?.Peek();
        return new UndoSelectionSessionState
        {
            HandSelectionActive = combatUi?.Hand?.IsInCardSelection == true,
            OverlaySelectionActive = overlay is NChooseACardSelectionScreen or NCardGridSelectionScreen,
            SupportedChoiceUiActive = combatUi != null && IsSupportedChoiceUiActive(combatUi),
            OverlayScreenType = overlay?.GetType().Name,
            ChoiceSpec = TryCaptureCurrentChoiceSpecFromUi()
        };
    }
    private IReadOnlyList<UndoFirstInSeriesPlayCountState> CaptureFirstInSeriesPlayCounts(CombatState combatState)
    {
        if (_hasFirstInSeriesPlayCountOverride
            && combatState.RoundNumber == _firstInSeriesPlayCountOverrideRound
            && combatState.CurrentSide == _firstInSeriesPlayCountOverrideSide)
        {
            return _firstInSeriesPlayCountOverrides
                .Select(static pair => new UndoFirstInSeriesPlayCountState
                {
                    CreatureKey = pair.Key,
                    Count = pair.Value
                })
                .ToList();
        }

    private static ActionKernelState CaptureActionKernelState()
    {
        RunState runState = RunManager.Instance.DebugOnlyGetState()
            ?? throw new InvalidOperationException("Run state was not available while capturing action kernel state.");
        return UndoActionKernelService.Capture(runState, TryCaptureCurrentChoiceSpecFromUi());
    }

    private static IReadOnlyList<UndoFirstInSeriesPlayCountState> CaptureFirstInSeriesPlayCounts(CombatState combatState)
    {
        List<UndoFirstInSeriesPlayCountState> states = [];
        IReadOnlyList<Creature> creatures = combatState.Creatures;
        foreach (CardPlayStartedEntry entry in CombatManager.Instance.History.CardPlaysStarted)
        {
            if (!entry.HappenedThisTurn(combatState) || !entry.CardPlay.IsFirstInSeries)
                continue;

            string? creatureKey = TryResolveCreatureKey(creatures, entry.Actor);
            if (string.IsNullOrWhiteSpace(creatureKey))
                continue;

            for (int i = 0; i < states.Count; i++)
            {
                if (states[i].CreatureKey != creatureKey)
                    continue;

                states[i] = new UndoFirstInSeriesPlayCountState
                {
                    CreatureKey = creatureKey,
                    Count = states[i].Count + 1
                };
                creatureKey = null;
                break;
            }

            if (!string.IsNullOrWhiteSpace(creatureKey))
            {
                states.Add(new UndoFirstInSeriesPlayCountState
                {
                    CreatureKey = creatureKey,
                    Count = 1
                });
            }
        }

        return states;
    }

    private static IReadOnlyList<ulong> CaptureTriggeredPlayerNetIds(PowerModel power)
    {
        if (power is not VitalSparkPower)
            return [];

        if (FindField(typeof(PowerModel), "_internalData")?.GetValue(power) is not { } internalData)
            return [];

        if (FindField(internalData.GetType(), "playersTriggeredThisTurn")?.GetValue(internalData) is not System.Collections.IEnumerable triggeredPlayers)
            return [];

        List<ulong> playerNetIds = [];
        foreach (object? entry in triggeredPlayers)
        {
            if (entry is Player player)
                playerNetIds.Add(player.NetId);
        }

        return playerNetIds;
    }

    private static IReadOnlyList<UndoNamedBoolState> CapturePowerRuntimeBoolProperties(PowerModel power)
    {
        List<UndoNamedBoolState> states = [.. CaptureRuntimeBoolProperties(power, "Target", "Applier")];
        PropertyInfo? isRevivingProperty = FindProperty(power.GetType(), "IsReviving");
        if (isRevivingProperty?.PropertyType == typeof(bool)
            && states.All(static state => state.Name != "IsReviving"))
        {
            states.Add(new UndoNamedBoolState
            {
                Name = "IsReviving",
                Value = isRevivingProperty.GetValue(power) is bool isReviving && isReviving
            });
        }

        return states;
    }

    private static IReadOnlyList<UndoNamedBoolState> CaptureRuntimeBoolProperties(object model, params string[] excludedNames)
    {
        HashSet<string> excluded = [.. excludedNames];
        return GetRuntimeStateProperties(model.GetType())
            .Where(property => property.PropertyType == typeof(bool) && !ShouldSkipRuntimeStateProperty(model.GetType(), property.Name, excluded))
            .Select(property => new UndoNamedBoolState
            {
                Name = property.Name,
                Value = property.GetValue(model) is bool value && value
            })
            .ToList();
    }

    private static IReadOnlyList<UndoNamedIntState> CaptureRuntimeIntProperties(object model, params string[] excludedNames)
    {
        HashSet<string> excluded = [.. excludedNames];
        return GetRuntimeStateProperties(model.GetType())
            .Where(property => property.PropertyType == typeof(int) && !ShouldSkipRuntimeStateProperty(model.GetType(), property.Name, excluded))
            .Select(property => new UndoNamedIntState
            {
                Name = property.Name,
                Value = property.GetValue(model) is int value ? value : 0
            })
            .ToList();
    }

    private static IReadOnlyList<UndoNamedEnumState> CaptureRuntimeEnumProperties(object model, params string[] excludedNames)
    {
        HashSet<string> excluded = [.. excludedNames];
        return GetRuntimeStateProperties(model.GetType())
            .Where(property => property.PropertyType.IsEnum && !ShouldSkipRuntimeStateProperty(model.GetType(), property.Name, excluded))
            .Select(property => new UndoNamedEnumState
            {
                Name = property.Name,
                EnumTypeName = property.PropertyType.AssemblyQualifiedName ?? property.PropertyType.FullName ?? property.PropertyType.Name,
                Value = Convert.ToInt32(property.GetValue(model))
            })
            .ToList();
    }

    private static bool ShouldSkipRuntimeStateProperty(Type type, string propertyName, IReadOnlySet<string>? excludedNames = null)
    {
        if (excludedNames?.Contains(propertyName) == true)
            return true;

        if (propertyName.StartsWith("Test", StringComparison.Ordinal))
            return true;

        return type == typeof(ConfusedPower) && propertyName == "TestEnergyCostOverride";
    }

    private static IEnumerable<PropertyInfo> GetRuntimeStateProperties(Type type)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        return type
            .GetProperties(Flags)
            .Where(static property => property.GetIndexParameters().Length == 0)
            .Where(static property => property.CanRead)
            .Where(property => property.GetMethod != null)
            .Where(property => property.SetMethod != null || FindField(type, $"<{property.Name}>k__BackingField") != null)
            .Where(static property => property.Name != "Status");
    }
    private static UndoCardCostState CaptureCardCostState(CardModel card)
    {
        CardEnergyCost energyCost = card.EnergyCost;
        List<LocalCostModifier> localModifiers = GetPrivateFieldValue<List<LocalCostModifier>>(energyCost, "_localModifiers") ?? [];
        List<TemporaryCardCost> temporaryStarCosts = GetPrivateFieldValue<List<TemporaryCardCost>>(card, "_temporaryStarCosts") ?? [];

        return new UndoCardCostState
        {
            EnergyBaseCost = FindField(energyCost.GetType(), "_base")?.GetValue(energyCost) as int? ?? energyCost.Canonical,
            CapturedXValue = FindField(energyCost.GetType(), "_capturedXValue")?.GetValue(energyCost) as int? ?? 0,
            EnergyWasJustUpgraded = FindProperty(energyCost.GetType(), "WasJustUpgraded")?.GetValue(energyCost) as bool? ?? false,
            EnergyLocalModifiers =
            [
                .. localModifiers.Select(static modifier => new UndoLocalCostModifierState
                {
                    Amount = modifier.Amount,
                    Type = modifier.Type,
                    Expiration = modifier.Expiration,
                    IsReduceOnly = modifier.IsReduceOnly
                })
            ],
            StarCostSet = FindField(card.GetType(), "_starCostSet")?.GetValue(card) as bool? ?? false,
            BaseStarCost = FindField(card.GetType(), "_baseStarCost")?.GetValue(card) as int? ?? 0,
            StarWasJustUpgraded = FindField(card.GetType(), "_wasStarCostJustUpgraded")?.GetValue(card) as bool? ?? false,
            TemporaryStarCosts =
            [
                .. temporaryStarCosts.Select(static cost => new UndoTemporaryStarCostState
                {
                    Cost = cost.Cost,
                    ClearsWhenTurnEnds = cost.ClearsWhenTurnEnds,
                    ClearsWhenCardIsPlayed = cost.ClearsWhenCardIsPlayed
                })
            ]
        };
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

        IReadOnlyList<Player> currentPlayers = runState.Players;
        for (int i = 0; i < snapshot.Players.Count; i++)
        {
            if (currentPlayers[i].NetId != snapshot.Players[i].playerId)
            {
                reason = $"player_mismatch_{i}";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private static void RestorePlayers(RunState runState, CombatState combatState, UndoCombatFullState snapshotState)
    {
        foreach (NetFullCombatState.PlayerState playerState in snapshotState.FullState.Players)
        {
            Player player = runState.GetPlayer(playerState.playerId)
                ?? throw new InvalidOperationException($"Could not map player snapshot {playerState.playerId}.");
            UndoPlayerOrbState? playerOrbState = GetPlayerOrbState(snapshotState, player.NetId);

            player.PlayerRng.LoadFromSerializable(playerState.rngSet);
            player.PlayerOdds.LoadFromSerializable(playerState.oddsSet);
            player.RelicGrabBag.LoadFromSerializable(playerState.relicGrabBag);
            player.Gold = playerState.gold;
            if (playerOrbState != null)
                player.BaseOrbSlotCount = playerOrbState.BaseOrbSlotCount;

            RestoreRelics(runState, combatState, player, playerState, GetRelicRuntimeStatesForPlayer(snapshotState, player.NetId));
            RestorePotions(player, playerState);
            RestorePlayerDeck(runState, player, GetPlayerDeckState(snapshotState, player.NetId));
            RestorePlayerCombatState(
                player,
                runState,
                combatState,
                playerState,
                GetCardCostStatesForPlayer(snapshotState, player.NetId),
                GetCardRuntimeStatesForPlayer(snapshotState, player.NetId),
                playerOrbState);
        }
    }

    private static void RestorePlayerDeck(RunState runState, Player player, UndoPlayerDeckState? deckState)
    {
        if (deckState == null)
            return;

        foreach (CardModel card in player.Deck.Cards.ToList())
        {
            card.RemoveFromCurrentPile();
            card.RemoveFromState();
        }

        foreach (SerializableCard serializableCard in deckState.Cards)
        {
            CardModel deckCard = runState.LoadCard(ClonePacketSerializable(serializableCard), player);
            player.Deck.AddInternal(deckCard, -1, true);
        }

        player.Deck.InvokeContentsChanged();
    }

    private static void RestoreRelics(RunState runState, CombatState combatState, Player player, NetFullCombatState.PlayerState playerState, IReadOnlyList<UndoRelicRuntimeState>? relicRuntimeStates)
    {
        foreach (RelicModel relic in player.Relics.ToList())
            player.RemoveRelicInternal(relic, true);

        foreach (NetFullCombatState.RelicState relicState in playerState.relics)
            player.AddRelicInternal(RelicModel.FromSerializable(relicState.relic), -1, true);

        if (relicRuntimeStates == null || relicRuntimeStates.Count == 0)
            return;

        UndoRuntimeRestoreContext context = new()
        {
            RunState = runState,
            CombatState = combatState
        };

        Dictionary<ModelId, int> ordinalsByRelicId = [];
        foreach (RelicModel relic in player.Relics)
        {
            int ordinal = ordinalsByRelicId.TryGetValue(relic.Id, out int existingOrdinal) ? existingOrdinal : 0;
            ordinalsByRelicId[relic.Id] = ordinal + 1;
            UndoRelicRuntimeState? runtimeState = relicRuntimeStates.FirstOrDefault(state => state.RelicId == relic.Id && state.Ordinal == ordinal);
            if (runtimeState == null)
                continue;

            relic.Status = runtimeState.Status;
            if (runtimeState.IsActivating.HasValue)
            {
                if (!TrySetPrivateAutoPropertyBackingField(relic, "IsActivating", runtimeState.IsActivating.Value))
                    SetPrivatePropertyValue(relic, "IsActivating", runtimeState.IsActivating.Value);
            }

            RestoreRuntimeBoolProperties(relic, runtimeState.BoolProperties);
            RestoreRuntimeIntProperties(relic, runtimeState.IntProperties);
            RestoreRuntimeEnumProperties(relic, runtimeState.EnumProperties);
            UndoRuntimeStateCodecRegistry.RestoreRelicStates(relic, runtimeState.ComplexStates, context);
        }
    }

    private static void RestoreSpecialPowerRuntimeState(PowerModel power, UndoPowerRuntimeState runtimeState, CombatState combatState)
    {
        if (FindField(typeof(PowerModel), "_internalData")?.GetValue(power) is not { } internalData)
            return;

        UndoNamedBoolState? isRevivingState = runtimeState.BoolProperties.FirstOrDefault(static state => state.Name == "IsReviving");
        FieldInfo? isRevivingField = FindField(internalData.GetType(), "isReviving");
        if (isRevivingState != null && isRevivingField?.FieldType == typeof(bool))
            isRevivingField.SetValue(internalData, isRevivingState.Value);

        if (power is not VitalSparkPower)
            return;

        object? triggeredPlayersValue = FindField(internalData.GetType(), "playersTriggeredThisTurn")?.GetValue(internalData);
        if (triggeredPlayersValue == null)
            return;

        MethodInfo? clearMethod = triggeredPlayersValue.GetType().GetMethod("Clear", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        MethodInfo? addMethod = triggeredPlayersValue.GetType().GetMethod("Add", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, [typeof(Player)], null);
        if (clearMethod == null || addMethod == null)
            return;

        clearMethod.Invoke(triggeredPlayersValue, null);
        foreach (ulong playerNetId in runtimeState.TriggeredPlayerNetIds)
        {
            Player? player = combatState.GetPlayer(playerNetId);
            if (player != null)
                addMethod.Invoke(triggeredPlayersValue, [player]);
        }
    }

    private static void RestoreRuntimeBoolProperties(object target, IReadOnlyList<UndoNamedBoolState> states)
    {
        foreach (UndoNamedBoolState state in states)
        {
            if (ShouldSkipRuntimeStateProperty(target.GetType(), state.Name))
                continue;

            PropertyInfo? property = FindProperty(target.GetType(), state.Name);
            if (property != null && property.PropertyType == typeof(bool))
            {
                if (TrySetRuntimePropertyValue(target, property, state.Name, state.Value))
                    continue;
            }

            FieldInfo? field = FindField(target.GetType(), state.Name);
            if (field != null && field.FieldType == typeof(bool))
                field.SetValue(target, state.Value);
        }
    }

    private static void RestoreRuntimeIntProperties(object target, IReadOnlyList<UndoNamedIntState> states)
    {
        foreach (UndoNamedIntState state in states)
        {
            if (ShouldSkipRuntimeStateProperty(target.GetType(), state.Name))
                continue;

            PropertyInfo? property = FindProperty(target.GetType(), state.Name);
            if (property != null && property.PropertyType == typeof(int))
            {
                if (TrySetRuntimePropertyValue(target, property, state.Name, state.Value))
                    continue;
            }

            FieldInfo? field = FindField(target.GetType(), state.Name);
            if (field != null && field.FieldType == typeof(int))
                field.SetValue(target, state.Value);
        }
    }

    private static void RestoreRuntimeEnumProperties(object target, IReadOnlyList<UndoNamedEnumState> states)
    {
        foreach (UndoNamedEnumState state in states)
        {
            if (ShouldSkipRuntimeStateProperty(target.GetType(), state.Name))
                continue;

            PropertyInfo? property = FindProperty(target.GetType(), state.Name);
            if (property == null || !property.PropertyType.IsEnum)
                continue;

            object value = Enum.ToObject(property.PropertyType, state.Value);
            if (TrySetRuntimePropertyValue(target, property, state.Name, value))
                continue;

            FieldInfo? field = FindField(target.GetType(), state.Name);
            if (field != null && field.FieldType.IsEnum)
                field.SetValue(target, value);
        }
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

    private static void RestorePlayerCombatState(
        Player player,
        RunState runState,
        CombatState combatState,
        NetFullCombatState.PlayerState playerState,
        IReadOnlyDictionary<PileType, IReadOnlyList<UndoCardCostState>>? cardCostStatesByPile,
        IReadOnlyDictionary<PileType, IReadOnlyList<UndoCardRuntimeState>>? cardRuntimeStatesByPile,
        UndoPlayerOrbState? playerOrbState)
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

            IReadOnlyList<UndoCardCostState>? pileCardCostStates = null;
            IReadOnlyList<UndoCardRuntimeState>? pileCardRuntimeStates = null;
            cardCostStatesByPile?.TryGetValue(pileType, out pileCardCostStates);
            cardRuntimeStatesByPile?.TryGetValue(pileType, out pileCardRuntimeStates);
            if (pilesByType.TryGetValue(pileType, out NetFullCombatState.CombatPileState pileState))
            {
                for (int cardIndex = 0; cardIndex < pileState.cards.Count; cardIndex++)
                {
                    NetFullCombatState.CardState cardState = pileState.cards[cardIndex];
                    CardModel card = CardModel.FromSerializable(cardState.card);
                    combatState.AddCard(card, player);
                    RestoreCardState(
                        runState,
                        combatState,
                        card,
                        cardState,
                        pileCardCostStates != null && cardIndex < pileCardCostStates.Count ? pileCardCostStates[cardIndex] : null,
                        pileCardRuntimeStates != null && cardIndex < pileCardRuntimeStates.Count ? pileCardRuntimeStates[cardIndex] : null);
                    pile.AddInternal(card, -1, true);
                }
            }

            pile.InvokeContentsChanged();
        }

        playerCombatState.Energy = playerState.energy;
        playerCombatState.Stars = playerState.stars;
        RestoreOrbQueue(player, playerState, playerOrbState);
        playerCombatState.RecalculateCardValues();
    }

    private static void RestoreCardState(RunState runState, CombatState combatState, CardModel card, NetFullCombatState.CardState cardState, UndoCardCostState? costState, UndoCardRuntimeState? runtimeState)
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

        RestoreCardCostState(card, costState);
        RestoreCardRuntimeState(runState, combatState, card, runtimeState);
    }

    private static IReadOnlyDictionary<PileType, IReadOnlyList<UndoCardCostState>>? GetCardCostStatesForPlayer(UndoCombatFullState snapshotState, ulong playerNetId)
    {
        Dictionary<PileType, IReadOnlyList<UndoCardCostState>> states = snapshotState.CardCostStates
            .Where(static state => state != null)
            .Where(state => state.PlayerNetId == playerNetId)
            .ToDictionary(static state => state.PileType, static state => state.Cards);

        return states.Count == 0 ? null : states;
    }

    private static UndoPlayerOrbState? GetPlayerOrbState(UndoCombatFullState snapshotState, ulong playerNetId)
    {
        return snapshotState.PlayerOrbStates.FirstOrDefault(state => state.PlayerNetId == playerNetId);
    }

    private static UndoPlayerDeckState? GetPlayerDeckState(UndoCombatFullState snapshotState, ulong playerNetId)
    {
        return snapshotState.PlayerDeckStates.FirstOrDefault(state => state.PlayerNetId == playerNetId);
    }

    private static IReadOnlyDictionary<PileType, IReadOnlyList<UndoCardRuntimeState>>? GetCardRuntimeStatesForPlayer(UndoCombatFullState snapshotState, ulong playerNetId)
    {
        Dictionary<PileType, IReadOnlyList<UndoCardRuntimeState>> states = snapshotState.CardRuntimeStates
            .Where(static state => state != null)
            .Where(state => state.PlayerNetId == playerNetId)
            .ToDictionary(static state => state.PileType, static state => state.Cards);

        return states.Count == 0 ? null : states;
    }

    private static IReadOnlyList<UndoRelicRuntimeState>? GetRelicRuntimeStatesForPlayer(UndoCombatFullState snapshotState, ulong playerNetId)
    {
        List<UndoRelicRuntimeState> states = snapshotState.RelicRuntimeStates
            .Where(static state => state != null)
            .Where(state => state.PlayerNetId == playerNetId)
            .ToList();

        return states.Count == 0 ? null : states;
    }

    private static void RestoreCardRuntimeState(RunState runState, CombatState combatState, CardModel card, UndoCardRuntimeState? runtimeState)
    {
        if (runtimeState == null)
            return;

        card.BaseReplayCount = runtimeState.BaseReplayCount;
        if (!TrySetPrivateAutoPropertyBackingField(card, "HasSingleTurnRetain", runtimeState.HasSingleTurnRetain))
            SetPrivatePropertyValue(card, "HasSingleTurnRetain", runtimeState.HasSingleTurnRetain);
        if (!TrySetPrivateAutoPropertyBackingField(card, "HasSingleTurnSly", runtimeState.HasSingleTurnSly))
            SetPrivatePropertyValue(card, "HasSingleTurnSly", runtimeState.HasSingleTurnSly);
        card.ExhaustOnNextPlay = runtimeState.ExhaustOnNextPlay;
        card.DeckVersion = runtimeState.DeckVersionRef == null
            ? null
            : UndoStableRefs.ResolveCardRef(runState, runtimeState.DeckVersionRef);
        RestoreEnchantmentRuntimeState(card, runtimeState.EnchantmentState);

        UndoRuntimeRestoreContext context = new()
        {
            RunState = runState,
            CombatState = combatState
        };
        UndoRuntimeStateCodecRegistry.RestoreCardStates(card, runtimeState.ComplexStates, context);
    }

    private static void RestoreEnchantmentRuntimeState(CardModel card, UndoEnchantmentRuntimeState? enchantmentState)
    {
        if (enchantmentState == null)
        {
            if (card.Enchantment != null)
                card.ClearEnchantmentInternal();

            return;
        }

        if (card.Enchantment == null && enchantmentState.Serializable != null)
        {
            EnchantmentModel enchantment = EnchantmentModel.FromSerializable(ClonePacketSerializable(enchantmentState.Serializable));
            card.EnchantInternal(enchantment, enchantment.Amount);
            card.Enchantment?.ModifyCard();
        }

        if (card.Enchantment == null)
            return;

        card.Enchantment.Status = enchantmentState.Status;
        RestoreRuntimeBoolProperties(card.Enchantment, enchantmentState.BoolProperties);
        RestoreRuntimeIntProperties(card.Enchantment, enchantmentState.IntProperties);
        RestoreRuntimeEnumProperties(card.Enchantment, enchantmentState.EnumProperties);
        card.Enchantment.RecalculateValues();
        card.DynamicVars.RecalculateForUpgradeOrEnchant();
    }

    private static void RestoreCardCostState(CardModel card, UndoCardCostState? costState)
    {
        if (costState == null)
            return;

        CardEnergyCost energyCost = card.EnergyCost;
        SetPrivateFieldValue(energyCost, "_base", costState.EnergyBaseCost);
        SetPrivateFieldValue(energyCost, "_capturedXValue", costState.CapturedXValue);
        if (!TrySetPrivateAutoPropertyBackingField(energyCost, "WasJustUpgraded", costState.EnergyWasJustUpgraded))
            SetPrivatePropertyValue(energyCost, "WasJustUpgraded", costState.EnergyWasJustUpgraded);
        SetPrivateFieldValue(
            energyCost,
            "_localModifiers",
            costState.EnergyLocalModifiers
                .Select(static modifier => new LocalCostModifier(modifier.Amount, modifier.Type, modifier.Expiration, modifier.IsReduceOnly))
                .ToList());

        SetPrivateFieldValue(card, "_starCostSet", costState.StarCostSet);
        SetPrivateFieldValue(card, "_baseStarCost", costState.BaseStarCost);
        SetPrivateFieldValue(card, "_wasStarCostJustUpgraded", costState.StarWasJustUpgraded);
        SetPrivateFieldValue(
            card,
            "_temporaryStarCosts",
            costState.TemporaryStarCosts.Select(CreateTemporaryStarCost).ToList());

        card.InvokeEnergyCostChanged();
        InvokeCardStarCostChanged(card);
    }

    private static TemporaryCardCost CreateTemporaryStarCost(UndoTemporaryStarCostState costState)
    {
        if (!costState.ClearsWhenTurnEnds && !costState.ClearsWhenCardIsPlayed)
            return TemporaryCardCost.ThisCombat(costState.Cost);

        if (costState.ClearsWhenTurnEnds)
            return TemporaryCardCost.ThisTurn(costState.Cost);

        return TemporaryCardCost.UntilPlayed(costState.Cost);
    }

    private static void InvokeCardStarCostChanged(CardModel card)
    {
        if (FindField(card.GetType(), "StarCostChanged")?.GetValue(card) is Action starCostChanged)
            starCostChanged();
    }

    private static void RestoreOrbQueue(Player player, NetFullCombatState.PlayerState playerState, UndoPlayerOrbState? playerOrbState)
    {
        OrbQueue orbQueue = player.PlayerCombatState!.OrbQueue;
        orbQueue.Clear();
        int desiredCapacity = playerOrbState?.Capacity ?? Math.Max(player.BaseOrbSlotCount, playerState.orbs.Count);
        orbQueue.AddCapacity(desiredCapacity);

        for (int i = 0; i < playerState.orbs.Count; i++)
        {
            OrbModel orb = ModelDb.GetById<OrbModel>(playerState.orbs[i].id).ToMutable();
            orb.Owner = player;
            orbQueue.Insert(i, orb);
            RestoreOrbRuntimeState(orb, playerOrbState != null && i < playerOrbState.Orbs.Count ? playerOrbState.Orbs[i] : null);
        }
    }

    private static void RestoreOrbRuntimeState(OrbModel orb, UndoOrbRuntimeState? runtimeState)
    {
        if (runtimeState == null || runtimeState.OrbId != orb.Id)
            return;

        if (orb is DarkOrb && runtimeState.DarkEvokeValue is decimal darkEvokeValue)
            SetPrivateFieldValue(orb, "_evokeVal", darkEvokeValue);

        if (orb is GlassOrb && runtimeState.GlassPassiveValue is decimal glassPassiveValue)
            SetPrivateFieldValue(orb, "_passiveVal", glassPassiveValue);
    }

    private static void ResetActionExecutorForRestore()
    {
        ActionExecutor actionExecutor = RunManager.Instance.ActionExecutor;
        actionExecutor.Pause();
        actionExecutor.Cancel();
        UndoReflectionUtil.TrySetPropertyValue(actionExecutor, "CurrentlyRunningAction", null);
        UndoReflectionUtil.TrySetFieldValue(actionExecutor, "_actionCancelToken", null);
        UndoReflectionUtil.TrySetFieldValue(actionExecutor, "_queueTaskCompletionSource", null);
    }

    private static ActionSynchronizerCombatState GetEffectiveSynchronizerState(UndoCombatFullState snapshot)
    {
        if (snapshot.ActionKernelState.BoundaryKind == ActionKernelBoundaryKind.StableBoundary
            && snapshot.CurrentSide == CombatSide.Player
            && snapshot.SynchronizerCombatState == ActionSynchronizerCombatState.NotPlayPhase)
        {
            return ActionSynchronizerCombatState.PlayPhase;
        }

        return snapshot.SynchronizerCombatState;
    }

    private static bool RestoreActionSynchronizationState(
        ActionSynchronizerCombatState targetState,
        ActionKernelBoundaryKind boundaryKind,
        out string? reason)
    {
        reason = null;
        if (!TryValidateActionQueueFrontStates(boundaryKind, out reason))
            return false;

        ActionQueueSynchronizer synchronizer = RunManager.Instance.ActionQueueSynchronizer;
        ActionQueueSet actionQueueSet = RunManager.Instance.ActionQueueSet;
        if (targetState == ActionSynchronizerCombatState.PlayPhase)
        {
            synchronizer.SetCombatState(ActionSynchronizerCombatState.NotPlayPhase);
            synchronizer.SetCombatState(ActionSynchronizerCombatState.PlayPhase);
            actionQueueSet.UnpauseAllPlayerQueues();
            RunManager.Instance.ActionExecutor.Unpause();
            return true;
        }

        synchronizer.SetCombatState(ActionSynchronizerCombatState.PlayPhase);
        synchronizer.SetCombatState(targetState);
        if (targetState == ActionSynchronizerCombatState.NotPlayPhase || targetState == ActionSynchronizerCombatState.EndTurnPhaseOne)
            actionQueueSet.PauseAllPlayerQueues();

        return true;
    }

    private static bool TryValidateActionQueueFrontStates(ActionKernelBoundaryKind boundaryKind, out string? reason)
    {
        reason = null;
        bool allowGatheringPlayerChoice = boundaryKind == ActionKernelBoundaryKind.PausedChoice;
        if (UndoReflectionUtil.FindField(RunManager.Instance.ActionQueueSet.GetType(), "_actionQueues")?.GetValue(RunManager.Instance.ActionQueueSet) is not System.Collections.IEnumerable rawQueues)
            return true;

        foreach (object rawQueue in rawQueues)
        {
            if (UndoReflectionUtil.FindField(rawQueue.GetType(), "actions")?.GetValue(rawQueue) is not System.Collections.IList actions
                || actions.Count == 0
                || actions[0] is not GameAction frontAction)
            {
                continue;
            }

            bool legalState = frontAction.State == GameActionState.WaitingForExecution
                || frontAction.State == GameActionState.ReadyToResumeExecuting
                || (allowGatheringPlayerChoice && frontAction.State == GameActionState.GatheringPlayerChoice);
            if (legalState)
                continue;

            ulong ownerId = UndoReflectionUtil.FindField(rawQueue.GetType(), "ownerId")?.GetValue(rawQueue) is ulong owner ? owner : 0UL;
            reason = $"front_action_invalid_state:{ownerId}:{frontAction.State}:{frontAction.GetType().Name}";
            return false;
        }

        return true;
    }
    private void EnsurePlayerChoiceUndoAnchor(UndoSnapshot restoredSnapshot, UndoChoiceSpec? preferredChoiceSpec = null, bool forceRefresh = false, UndoCombatFullState? anchorCombatStateOverride = null)
    {
        int replayEventCount = GetCurrentReplayEventCount();
        UndoChoiceSpec? choiceSpec = preferredChoiceSpec ?? ResolveCurrentChoiceSpec(restoredSnapshot, replayEventCount);
        if (!IsSupportedChoiceAnchorKind(choiceSpec))
            return;

        GameAction? action = RunManager.Instance.ActionExecutor.CurrentlyRunningAction;
        NCombatUi? combatUi = NCombatRoom.Instance?.Ui;
        UndoSnapshot? existing = _pastSnapshots.First?.Value;
        bool canRearm = forceRefresh
            || CanRestoreResolvedChoiceBranch(existing, action)
            || (restoredSnapshot.IsChoiceAnchor && CanRestoreResolvedChoiceBranch(restoredSnapshot, action))
            || (action?.State == GameActionState.GatheringPlayerChoice
                && combatUi != null
                && IsSupportedChoiceUiActive(combatUi));
        if (!canRearm)
            return;

        if (existing != null
            && existing.IsChoiceAnchor
            && existing.ReplayEventCount == replayEventCount)
        {
            if (!forceRefresh && existing.ChoiceSpec != null)
                return;

            _pastSnapshots.RemoveFirst();
        }

        UndoSnapshot anchor = new(
            anchorCombatStateOverride ?? CaptureCurrentCombatFullState(),
            replayEventCount,
            UndoActionKind.PlayerChoice,
            _nextSequenceId++,
            restoredSnapshot.ActionLabel,
            isChoiceAnchor: true,
            choiceSpec: choiceSpec);

        _pastSnapshots.AddFirst(anchor);
        TrimSnapshots(_pastSnapshots);
        MainFile.Logger.Info($"Re-armed player choice undo anchor. ReplayEvents={anchor.ReplayEventCount}, UndoCount={_pastSnapshots.Count}, ChoiceKind={(choiceSpec == null ? "null" : choiceSpec.Kind)} forceRefresh={forceRefresh}");
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

    private static UndoCombatCardDbState CaptureCombatCardDbState(RunState runState)
    {
        NetCombatCardDb db = NetCombatCardDb.Instance;
        if (GetPrivateFieldValue<System.Collections.IDictionary>(db, "_idToCard") is not { } idToCard)
            return new UndoCombatCardDbState();

        List<UndoCombatCardDbEntryState> entries = [];
        foreach (System.Collections.DictionaryEntry entry in idToCard)
        {
            if (entry.Key is not uint combatCardId || entry.Value is not CardModel card || !card.IsMutable)
                continue;

            entries.Add(new UndoCombatCardDbEntryState
            {
                CombatCardId = combatCardId,
                Card = UndoStableRefs.CaptureCardRef(runState, card)
            });
        }

        return new UndoCombatCardDbState
        {
            Entries = [.. entries.OrderBy(static entry => entry.CombatCardId)],
            NextId = FindField(db.GetType(), "_nextId")?.GetValue(db) is uint nextId ? nextId : 0U
        };
    }

    private static void RestoreCombatCardDbState(RunState runState, NetCombatCardDb db, UndoCombatCardDbState combatCardDbState)
    {
        if (GetPrivateFieldValue<System.Collections.IDictionary>(db, "_idToCard") is not { } idToCard
            || GetPrivateFieldValue<System.Collections.IDictionary>(db, "_cardToId") is not { } cardToId)
        {
            return;
        }

        List<CardModel> fallbackCards = cardToId.Keys.OfType<CardModel>().Where(static card => card.IsMutable).ToList();
        idToCard.Clear();
        cardToId.Clear();

        uint nextId = combatCardDbState.NextId;
        foreach (UndoCombatCardDbEntryState entry in combatCardDbState.Entries.OrderBy(static entry => entry.CombatCardId))
        {
            CardModel card = UndoStableRefs.ResolveCardRef(runState, entry.Card);
            if (!card.IsMutable || !IsCardInCombatPiles(runState, card) || cardToId.Contains(card) || idToCard.Contains(entry.CombatCardId))
                continue;

            idToCard[entry.CombatCardId] = card;
            cardToId[card] = entry.CombatCardId;
            if (entry.CombatCardId >= nextId)
                nextId = entry.CombatCardId + 1;
        }

        foreach (CardModel card in fallbackCards)
        {
            if (cardToId.Contains(card))
                continue;

            while (idToCard.Contains(nextId))
                nextId++;

            idToCard[nextId] = card;
            cardToId[card] = nextId;
            nextId++;
        }

        UndoReflectionUtil.TrySetFieldValue(db, "_nextId", nextId);
    }

    private static bool IsCardInCombatPiles(RunState runState, CardModel card)
    {
        foreach (Player player in runState.Players)
        {
            if (player.PlayerCombatState == null)
                continue;

            foreach (CardPile pile in player.PlayerCombatState.AllPiles)
            {
                if (pile.Cards.Contains(card))
                    return true;
            }
        }

        return false;
    }
    private static void RebuildTransientCombatCaches(RunState runState, UndoCombatCardDbState? combatCardDbState = null)
    {
        RebuildNetCombatCardDb(runState, combatCardDbState);
        RebuildPotionContainer(runState);
    }

    private static void RebuildNetCombatCardDb(RunState runState, UndoCombatCardDbState? combatCardDbState)
    {
        NetCombatCardDb db = NetCombatCardDb.Instance;
        InvokePrivateMethod(db, "OnCombatEnded", new object?[] { null });
        db.ClearCardsForTesting();
        db.StartCombat(runState.Players);

        if (combatCardDbState != null)
            RestoreCombatCardDbState(runState, db, combatCardDbState);
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
    private static void NormalizeRelicInventoryUi(RunState runState)
    {
        NRelicInventory? relicInventory = NRun.Instance?.GlobalUi?.RelicInventory;
        if (relicInventory == null)
            return;

        RunManager.Instance.HoveredModelTracker.OnLocalRelicUnhovered();
        if (GetPrivateFieldValue<System.Collections.IList>(relicInventory, "_relicNodes") is { } relicNodes)
        {
            foreach (Node node in relicNodes.Cast<Node>().ToList())
            {
                if (!GodotObject.IsInstanceValid(node))
                    continue;

                node.GetParent()?.RemoveChild(node);
                node.QueueFree();
            }

            relicNodes.Clear();
        }

        ClearNodeChildren(relicInventory);
        relicInventory.Initialize(runState);
        InvokePrivateMethod(relicInventory, "UpdateNavigation");
    }

    private static bool TryValidateRestoredState(UndoCombatFullState snapshot, bool runMayBeHidden, out string reason)
    {
        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        if (runState == null || combatState == null)
        {
            reason = "missing_runtime_state";
            return false;
        }

        if (combatState.RoundNumber != snapshot.RoundNumber)
        {
            reason = "round_mismatch";
            return false;
        }

        if (combatState.CurrentSide != snapshot.CurrentSide)
        {
            reason = "side_mismatch";
            return false;
        }

        if (RunManager.Instance.ActionQueueSynchronizer.CombatState != GetEffectiveSynchronizerState(snapshot))
        {
            reason = "synchronizer_state_mismatch";
            return false;
        }

        if (combatState.Creatures.Count != snapshot.FullState.Creatures.Count)
        {
            reason = "creature_count_mismatch";
            return false;
        }

        if (CombatManager.Instance.History.Entries.Count() != snapshot.CombatHistoryState.Entries.Count)
        {
            reason = "history_count_mismatch";
            return false;
        }

        NRun? currentRun = NGame.Instance?.CurrentRunNode;
        if (!runMayBeHidden && currentRun != null && !currentRun.Visible)
        {
            reason = "run_hidden";
            return false;
        }

        NCombatRoom? combatRoom = NCombatRoom.Instance;
        if (combatRoom != null)
        {
            Control? encounterSlots = GetPrivateFieldValue<Control>(combatRoom, "<EncounterSlots>k__BackingField")
                ?? GetPrivateFieldValue<Control>(combatRoom, "EncounterSlots");
            foreach (Creature creature in combatState.Creatures)
            {
                if (combatRoom.GetCreatureNode(creature) == null)
                {
                    reason = $"missing_creature_node:{creature}";
                    return false;
                }
            }

            foreach (Creature creature in combatState.Enemies)
            {
                if (string.IsNullOrWhiteSpace(creature.SlotName))
                    continue;

                if (encounterSlots == null || !encounterSlots.HasNode(creature.SlotName))
                {
                    reason = $"invalid_slot:{creature.SlotName}";
                    return false;
                }
            }

            Player? me = LocalContext.GetMe(combatState);
            if (me != null)
            {
                int handCount = PileType.Hand.GetPile(me).Cards.Count;
                int holderCount = combatRoom.Ui.Hand.CardHolderContainer.GetChildCount();
                if (holderCount != handCount)
                {
                    reason = $"hand_holder_mismatch:{holderCount}:{handCount}";
                    return false;
                }
            }

            bool choiceUiActive = IsSupportedChoiceUiActive(combatRoom.Ui);
            if (snapshot.SelectionSessionState?.SupportedChoiceUiActive != true && choiceUiActive)
            {
                reason = "unexpected_choice_ui";
                return false;
            }
        }

        if (NOverlayStack.Instance != null
            && NOverlayStack.Instance.ScreenCount > 0
            && snapshot.SelectionSessionState?.OverlaySelectionActive != true)
        {
            reason = "unexpected_overlay";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static void RestoreCreatures(RunState runState, CombatState combatState, UndoCombatFullState snapshotState)
    {
        SyncCombatCreaturesToSnapshot(runState, combatState, snapshotState);

        IReadOnlyList<Creature> creatures = combatState.Creatures;
        for (int i = 0; i < snapshotState.FullState.Creatures.Count; i++)
            RestoreCreatureState(creatures[i], snapshotState.FullState.Creatures[i]);

        RestorePowerRuntimeStates(runState, combatState, snapshotState.PowerRuntimeStates);
        if (snapshotState.MonsterStates.Count > 0)
        {
            Dictionary<string, UndoMonsterState> monsterStatesByKey = snapshotState.MonsterStates.ToDictionary(static state => state.CreatureKey);
            for (int j = 0; j < creatures.Count; j++)
            {
                Creature creature = creatures[j];
                MonsterModel? monster = creature.Monster;
                if (monster?.MoveStateMachine == null)
                    continue;

                if (!monsterStatesByKey.TryGetValue(BuildCreatureKey(creature, j), out UndoMonsterState? monsterState))
                    continue;

                RestoreMonsterState(monster, monsterState);
            }
        }

        foreach (Player player in runState.Players)
        {
            if (player.Creature.IsAlive)
                player.ActivateHooks();
            else
                player.DeactivateHooks();
        }
    }

    // Creature ordering must match NetFullCombatState.Creatures exactly so later
    // per-creature state restore can address the correct live creature. Pets are
    // restored as allies owned by a player, never inferred as enemies.
    private static void SyncCombatCreaturesToSnapshot(RunState runState, CombatState combatState, UndoCombatFullState snapshotState)
    {
        List<Creature> currentAllies = combatState.Allies.ToList();
        List<Creature> currentEnemies = combatState.Enemies.ToList();
        HashSet<Creature> usedAllies = [];
        HashSet<Creature> usedEnemies = [];
        List<Creature> desiredAllies = [];
        List<Creature> desiredEnemies = [];
        List<CreatureTopologyState> topologyStates = snapshotState.CreatureTopologyStates.ToList();
        List<UndoMonsterState> snapshotMonsterStates = snapshotState.MonsterStates.ToList();
        int monsterIndex = 0;

        foreach (NetFullCombatState.CreatureState creatureState in snapshotState.FullState.Creatures)
        {
            if (creatureState.playerId is ulong playerNetId)
            {
                Creature playerCreature = runState.GetPlayer(playerNetId)?.Creature
                    ?? throw new InvalidOperationException($"Could not map player creature {playerNetId}.");
                desiredAllies.Add(playerCreature);
                usedAllies.Add(playerCreature);
                continue;
            }

            CreatureTopologyState? topologyState = monsterIndex < topologyStates.Count ? topologyStates[monsterIndex] : null;
            UndoMonsterState? monsterState = monsterIndex < snapshotMonsterStates.Count ? snapshotMonsterStates[monsterIndex] : null;
            monsterIndex++;

            Creature creature = ResolveSnapshotMonsterCreature(
                runState,
                combatState,
                creatureState,
                topologyState,
                monsterState,
                currentAllies,
                currentEnemies,
                usedAllies,
                usedEnemies);

            if (topologyState?.Role == CreatureRole.Pet)
            {
                desiredAllies.Add(creature);
                usedAllies.Add(creature);
            }
            else
            {
                desiredEnemies.Add(creature);
                usedEnemies.Add(creature);
            }
        }

        SyncPlayerPetCollections(runState, desiredAllies);

        foreach (Creature ally in currentAllies)
        {
            if (usedAllies.Contains(ally) || ally.IsPlayer)
                continue;

            ally.CombatState = null;
        }

        foreach (Creature enemy in currentEnemies)
        {
            if (usedEnemies.Contains(enemy))
                continue;

            enemy.CombatState = null;
        }

        ReplaceCombatCreatureList(combatState, "_allies", desiredAllies);
        ReplaceCombatCreatureList(combatState, "_enemies", desiredEnemies);
        combatState.SortEnemiesBySlotName();
        NotifyCombatCreaturesChanged(combatState);
    }

    private static Creature ResolveSnapshotMonsterCreature(
        RunState runState,
        CombatState combatState,
        NetFullCombatState.CreatureState creatureState,
        CreatureTopologyState? topologyState,
        UndoMonsterState? monsterState,
        IReadOnlyList<Creature> currentAllies,
        IReadOnlyList<Creature> currentEnemies,
        ISet<Creature> usedAllies,
        ISet<Creature> usedEnemies)
    {
        ModelId monsterId = creatureState.monsterId ?? topologyState?.MonsterId
            ?? throw new InvalidOperationException("Snapshot creature state had no monster id.");
        string? desiredSlot = !string.IsNullOrWhiteSpace(topologyState?.SlotName)
            ? topologyState!.SlotName
            : string.IsNullOrWhiteSpace(monsterState?.SlotName) ? null : monsterState!.SlotName;

        if (topologyState?.Role == CreatureRole.Pet)
        {
            if (topologyState.PetOwnerPlayerNetId is not ulong ownerNetId)
                throw new InvalidOperationException($"Pet topology for {monsterId.Entry} was missing an owner.");

            Player owner = runState.GetPlayer(ownerNetId)
                ?? throw new InvalidOperationException($"Could not map pet owner {ownerNetId} for {monsterId.Entry}.");
            Creature? existingPet = currentAllies.FirstOrDefault(creature =>
                !usedAllies.Contains(creature)
                && !creature.IsPlayer
                && creature.Monster?.Id == monsterId
                && creature.Side == owner.Creature.Side
                && (creature.PetOwner == null || creature.PetOwner == owner)
                && string.Equals(creature.SlotName, desiredSlot, StringComparison.Ordinal));
            existingPet ??= currentAllies.FirstOrDefault(creature =>
                !usedAllies.Contains(creature)
                && !creature.IsPlayer
                && creature.Monster?.Id == monsterId
                && creature.Side == owner.Creature.Side
                && (creature.PetOwner == null || creature.PetOwner == owner));
            existingPet ??= CreateSnapshotCreature(combatState, monsterId, owner.Creature.Side, desiredSlot);
            existingPet.SlotName = desiredSlot;
            EnsurePetOwnership(owner, existingPet);
                        return existingPet;
        }

        Creature? existingEnemy = currentEnemies.FirstOrDefault(creature =>
            !usedEnemies.Contains(creature)
            && creature.Monster?.Id == monsterId
            && string.Equals(creature.SlotName, desiredSlot, StringComparison.Ordinal));
        if (existingEnemy == null && string.IsNullOrWhiteSpace(desiredSlot))
        {
            existingEnemy = currentEnemies.FirstOrDefault(creature =>
                !usedEnemies.Contains(creature)
                && creature.Monster?.Id == monsterId);
        }
        existingEnemy ??= CreateSnapshotCreature(combatState, monsterId, CombatSide.Enemy, desiredSlot);
        existingEnemy.SlotName = desiredSlot;
        return existingEnemy;
    }

    private static Creature CreateSnapshotCreature(CombatState combatState, ModelId monsterId, CombatSide side, string? desiredSlot)
    {
        MonsterModel monster = ModelDb.GetById<MonsterModel>(monsterId).ToMutable();
        Creature creature = combatState.CreateCreature(monster, side, desiredSlot);
        monster.SetUpForCombat();
        CombatManager.Instance.StateTracker.Subscribe(creature);
        return creature;
    }

    private static void EnsurePetOwnership(Player owner, Creature pet)
    {
        PlayerCombatState playerCombatState = owner.PlayerCombatState
            ?? throw new InvalidOperationException($"Player {owner.NetId} had no combat state while restoring pet {pet.Monster?.Id.Entry}.");
        if (pet.PetOwner != null && pet.PetOwner != owner)
            throw new InvalidOperationException($"Pet {pet.Monster?.Id.Entry} was already bound to a different owner.");

        if (!playerCombatState.Pets.Contains(pet))
            playerCombatState.AddPetInternal(pet);
    }

    private static void SyncPlayerPetCollections(RunState runState, IReadOnlyList<Creature> desiredAllies)
    {
        foreach (Player player in runState.Players)
        {
            PlayerCombatState? playerCombatState = player.PlayerCombatState;
            if (playerCombatState == null)
                continue;

            List<Creature> desiredPets = desiredAllies
                .Where(creature => creature.PetOwner == player)
                .ToList();
            if (FindField(typeof(PlayerCombatState), "_pets")?.GetValue(playerCombatState) is not System.Collections.IList petList)
                continue;

            for (int i = petList.Count - 1; i >= 0; i--)
            {
                if (petList[i] is Creature pet && !desiredPets.Contains(pet))
                    petList.RemoveAt(i);
            }

            foreach (Creature pet in desiredPets)
            {
                if (!petList.Contains(pet))
                    playerCombatState.AddPetInternal(pet);
            }
        }
    }
    private static void RestorePowerRuntimeStates(RunState runState, CombatState combatState, IReadOnlyList<UndoPowerRuntimeState> runtimeStates)
    {
        if (runtimeStates.Count == 0)
            return;

        UndoRuntimeRestoreContext context = new()
        {
            RunState = runState,
            CombatState = combatState
        };

        Dictionary<string, Creature> creaturesByKey = BuildCreatureKeyMap(combatState.Creatures);
        for (int creatureIndex = 0; creatureIndex < combatState.Creatures.Count; creatureIndex++)
        {
            Creature creature = combatState.Creatures[creatureIndex];
            string ownerCreatureKey = BuildCreatureKey(creature, creatureIndex);
            Dictionary<ModelId, int> ordinalsByPowerId = [];
            foreach (PowerModel power in creature.Powers)
            {
                int ordinal = ordinalsByPowerId.TryGetValue(power.Id, out int existingOrdinal) ? existingOrdinal : 0;
                ordinalsByPowerId[power.Id] = ordinal + 1;
                UndoPowerRuntimeState? runtimeState = runtimeStates.FirstOrDefault(state =>
                    state.OwnerCreatureKey == ownerCreatureKey
                    && state.PowerId == power.Id
                    && state.Ordinal == ordinal);
                if (runtimeState == null)
                    continue;

                power.Target = ResolveCreatureByKey(creaturesByKey, runtimeState.TargetCreatureKey);
                power.Applier = ResolveCreatureByKey(creaturesByKey, runtimeState.ApplierCreatureKey);
                RestoreRuntimeBoolProperties(power, runtimeState.BoolProperties);
                RestoreRuntimeIntProperties(power, runtimeState.IntProperties);
                RestoreRuntimeEnumProperties(power, runtimeState.EnumProperties);
                RestoreSpecialPowerRuntimeState(power, runtimeState);
                UndoRuntimeStateCodecRegistry.RestorePowerStates(power, runtimeState.ComplexStates, context);
                if (power is not SwipePower swipe)
                    continue;

                if (runtimeState.StolenCard == null)
                {
                    swipe.StolenCard = null;
                    continue;
                }

                CardModel stolenCard = CardModel.FromSerializable(ClonePacketSerializable(runtimeState.StolenCard));
                if (power.Target?.Player != null)
                    stolenCard.Owner = power.Target.Player;
                swipe.StolenCard = stolenCard;
            }
        }
    }
    private static Dictionary<string, Creature> BuildCreatureKeyMap(IReadOnlyList<Creature> creatures)
    {
        Dictionary<string, Creature> creaturesByKey = [];
        for (int i = 0; i < creatures.Count; i++)
            creaturesByKey[BuildCreatureKey(creatures[i], i)] = creatures[i];

        return creaturesByKey;
    }

    private static Creature? ResolveCreatureByKey(IReadOnlyDictionary<string, Creature> creaturesByKey, string? creatureKey)
    {
        if (string.IsNullOrWhiteSpace(creatureKey))
            return null;

        return creaturesByKey.TryGetValue(creatureKey, out Creature? creature) ? creature : null;
    }

    private static void ReplaceCombatCreatureList(CombatState combatState, string fieldName, IEnumerable<Creature> desiredCreatures)
    {
        if (FindField(typeof(CombatState), fieldName)?.GetValue(combatState) is not System.Collections.IList list)
            throw new InvalidOperationException($"Could not access CombatState.{fieldName}.");

        list.Clear();
        foreach (Creature creature in desiredCreatures)
            list.Add(creature);
    }

    private static void NotifyCombatCreaturesChanged(CombatState combatState)
    {
        if (FindField(typeof(CombatState), "CreaturesChanged")?.GetValue(combatState) is Action<CombatState> creaturesChanged)
            creaturesChanged(combatState);
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

    private static void RestoreMonsterState(MonsterModel monster, UndoMonsterState state)
    {
        MonsterMoveStateMachine moveStateMachine = monster.MoveStateMachine;
        if (moveStateMachine == null)
            return;

        monster.Creature.SlotName = state.SlotName;
        UndoReflectionUtil.TrySetPropertyValue(monster, "SpawnedThisTurn", state.SpawnedThisTurn);
        UndoReflectionUtil.TrySetFieldValue(moveStateMachine, "_performedFirstMove", state.PerformedFirstMove);
        if (moveStateMachine.StateLog is List<MonsterState> stateLog)
        {
            stateLog.Clear();
            foreach (string stateId in state.StateLogIds)
            {
                if (moveStateMachine.States.TryGetValue(stateId, out MonsterState? loggedState))
                    stateLog.Add(loggedState);
            }
        }

        bool isReattaching = monster.Creature.GetPower<ReattachPower>() is ReattachPower reattachPower
            && FindProperty(reattachPower.GetType(), "IsReviving")?.GetValue(reattachPower) is bool isReviving
            && isReviving;

        if ((monster.Creature.IsDead || isReattaching)
            && FindProperty(monster.GetType(), "DeadState")?.GetValue(monster) is MoveState deadState)
        {
            monster.SetMoveImmediate(deadState, true);
            moveStateMachine.ForceCurrentState(deadState);
            SetPrivateFieldValue(deadState, "_performedAtLeastOnce", state.NextMovePerformedAtLeastOnce);
            NCombatRoom.Instance?.SetCreatureIsInteractable(monster.Creature, false);
            return;
        }

        if (state.CurrentStateId != null && moveStateMachine.States.TryGetValue(state.CurrentStateId, out MonsterState? currentState))
            moveStateMachine.ForceCurrentState(currentState);

        // Transient stunned moves are recreated during creature reconciliation
        // because the live MoveState carries monster-specific callbacks.
        if (state.NextMoveId == MonsterModel.stunnedMoveId)
            return;

        if (state.NextMoveId != null && moveStateMachine.States.TryGetValue(state.NextMoveId, out MonsterState? nextState) && nextState is MoveState moveState)
        {
            monster.SetMoveImmediate(moveState, true);
            if (state.CurrentStateId == null)
                moveStateMachine.ForceCurrentState(moveState);
            SetPrivateFieldValue(moveState, "_performedAtLeastOnce", state.NextMovePerformedAtLeastOnce);
        }
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
        UndoRuntimeStateCodecRegistry.RefreshPowerDisplays(combatState);
        if (NCombatRoom.Instance != null)
            UndoSpecialCreatureVisualNormalizer.DetachStateDisplayTracking(NCombatRoom.Instance);
        ClearTransientCardVisuals();
        NormalizeCombatInteractionState(combatState);
        RebuildCombatCreatureNodesIfNeeded(combatState);
        RefreshPlayerOrbManagers(combatState);
        RestoreThievingHopperDisplayCards(combatState);
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
                if (creatureNode == null)
                    continue;

                bool isReattaching = creature.GetPower<ReattachPower>() is ReattachPower reattachPower
                    && FindProperty(reattachPower.GetType(), "IsReviving")?.GetValue(reattachPower) is bool isReviving
                    && isReviving;

                if (creature.IsDead || isReattaching)
                    ClearCreatureIntentUi(creatureNode);
                else
                    await creatureNode.RefreshIntents();
            }

            SnapEnemyCreatureNodesToSlots(combatState);
            UndoSpecialCreatureVisualNormalizer.Refresh(combatState, combatRoom);
        }
        await WaitOneFrameAsync();
        if (NCombatRoom.Instance != null)
        {
            ForceCombatUiInteractiveState(NCombatRoom.Instance.Ui, combatState, LocalContext.GetMe(combatState));
            SnapEnemyCreatureNodesToSlots(combatState);
            UndoSpecialCreatureVisualNormalizer.Refresh(combatState, combatRoom);
        }

        NotifyCombatStateChangedMethod?.Invoke(CombatManager.Instance.StateTracker, ["UndoRefreshCombatUiAsync"]);
    }

    private static void RefreshPlayerOrbManagers(CombatState combatState)
    {
        NCombatRoom? combatRoom = NCombatRoom.Instance;
        if (combatRoom == null)
            return;

        foreach (Player player in combatState.Players)
        {
            if (player.PlayerCombatState == null)
                continue;

            NOrbManager? orbManager = combatRoom.GetCreatureNode(player.Creature)?.OrbManager;
            if (orbManager == null)
                continue;

            RebuildOrbManagerNodes(orbManager, player.PlayerCombatState.OrbQueue);
        }
    }

    private static void RebuildOrbManagerNodes(NOrbManager orbManager, OrbQueue orbQueue)
    {
        Control? orbContainer = GetPrivateFieldValue<Control>(orbManager, "_orbContainer");
        System.Collections.IList? orbNodes = GetPrivateFieldValue<System.Collections.IList>(orbManager, "_orbs");
        if (orbContainer == null || orbNodes == null)
            return;

        if (OrbManagerMatchesState(orbNodes, orbQueue))
        {
            orbManager.UpdateVisuals(OrbEvokeType.None);
            return;
        }

        GetPrivateFieldValue<Tween>(orbManager, "_curTween")?.Kill();
        SetPrivateFieldValue(orbManager, "_curTween", null);
        ClearNodeChildren(orbContainer);
        orbNodes.Clear();

        for (int i = 0; i < orbQueue.Capacity; i++)
        {
            NOrb orbNode = NOrb.Create(orbManager.IsLocal);
            orbContainer.AddChildSafely(orbNode);
            orbNodes.Add(orbNode);
            orbNode.Position = Vector2.Zero;
        }

        for (int i = 0; i < orbQueue.Orbs.Count; i++)
        {
            if (orbNodes[i] is not NOrb orbNode)
                continue;

            orbNode.ReplaceOrb(orbQueue.Orbs[i]);
            FinalizeOrbNodeVisuals(orbNode);
        }

        LayoutOrbManagerNodesInstantly(orbManager, orbNodes, orbQueue.Capacity);
        InvokePrivateMethod(orbManager, "UpdateControllerNavigation");
        orbManager.UpdateVisuals(OrbEvokeType.None);
    }

    private static void LayoutOrbManagerNodesInstantly(NOrbManager orbManager, System.Collections.IList orbNodes, int capacity)
    {
        if (capacity <= 0)
            return;

        float spread = 125f;
        float step = capacity > 1 ? spread / (capacity - 1) : 0f;
        float radius = Mathf.Lerp(225f, 300f, (capacity - 3f) / 7f);
        if (!orbManager.IsLocal)
            radius *= 0.75f;

        for (int i = 0; i < capacity; i++)
        {
            if (orbNodes[i] is not NOrb orbNode)
                continue;

            float angle = float.DegreesToRadians(-25f - spread);
            orbNode.Position = new Vector2(-Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
            spread -= step;
        }
    }

    private static void FinalizeOrbNodeVisuals(NOrb orbNode)
    {
        GetPrivateFieldValue<Tween>(orbNode, "_curTween")?.Kill();
        SetPrivateFieldValue(orbNode, "_curTween", null);
        if (GetPrivateFieldValue<Node2D>(orbNode, "_sprite") is Node2D sprite)
            sprite.Scale = Vector2.One;
    }

    private static bool OrbManagerMatchesState(System.Collections.IList orbNodes, OrbQueue orbQueue)
    {
        if (orbNodes.Count != orbQueue.Capacity)
            return false;

        for (int i = 0; i < orbQueue.Orbs.Count; i++)
        {
            if (orbNodes[i] is not NOrb orbNode || !ReferenceEquals(orbNode.Model, orbQueue.Orbs[i]))
                return false;
        }

        for (int i = orbQueue.Orbs.Count; i < orbNodes.Count; i++)
        {
            if (orbNodes[i] is not NOrb orbNode || orbNode.Model != null)
                return false;
        }

        return true;
    }

    private static void RestoreThievingHopperDisplayCards(CombatState combatState)
    {
        NCombatRoom? combatRoom = NCombatRoom.Instance;
        if (combatRoom == null)
            return;

        foreach (Creature creature in combatState.Enemies)
        {
            NCreature? creatureNode = combatRoom.GetCreatureNode(creature);
            if (creatureNode == null)
                continue;

            if (!creatureNode.HasNode("%StolenCardPos"))
                continue;

            Marker2D? stolenCardPos = creatureNode.GetNodeOrNull<Marker2D>("%StolenCardPos");
            if (stolenCardPos == null)
                continue;

            ClearNodeChildren(stolenCardPos);
            SwipePower? swipePower = creature.Powers.OfType<SwipePower>().FirstOrDefault(static power => power.StolenCard != null);
            if (swipePower?.StolenCard == null)
                continue;

            if (swipePower.StolenCard.Owner == null && swipePower.Target?.Player != null)
                swipePower.StolenCard.Owner = swipePower.Target.Player;

            NCard? cardNode = NCard.Create(swipePower.StolenCard, ModelVisibility.Visible);
            if (cardNode == null)
                continue;

            stolenCardPos.AddChild(cardNode);
            cardNode.Position += cardNode.Size * 0.5f;
            cardNode.UpdateVisuals(PileType.Deck, CardPreviewMode.Normal);
        }
    }
    private static void NormalizeCombatInteractionState(CombatState combatState)
    {
        NTargetManager.Instance?.CancelTargeting();
        RunManager.Instance.HoveredModelTracker.OnLocalCardDeselected();
        RunManager.Instance.HoveredModelTracker.OnLocalCardUnhovered();
        ClearCombatManagerCollection("_playersReadyToEndTurn");
        ClearCombatManagerCollection("_playersReadyToBeginEnemyTurn");
        ClearCombatManagerCollection("_playersTakingExtraTurn");
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

    private static void RebuildCombatCreatureNodesIfNeeded(CombatState combatState)
    {
        NCombatRoom? combatRoom = NCombatRoom.Instance;
        if (combatRoom == null)
            return;

        List<Creature> creatures = combatState.Creatures.ToList();
        List<NCreature> creatureNodes = combatRoom.CreatureNodes.ToList();
        List<NCreature> removingNodes = combatRoom.RemovingCreatureNodes.ToList();
        Control? encounterSlots = GetPrivateFieldValue<Control>(combatRoom, "<EncounterSlots>k__BackingField")
            ?? GetPrivateFieldValue<Control>(combatRoom, "EncounterSlots");
        bool hasInvalidEnemySlots = combatState.Enemies.Any(creature =>
            !string.IsNullOrWhiteSpace(creature.SlotName)
            && (encounterSlots == null || !encounterSlots.HasNode(creature.SlotName)));
        bool forceRebuild = combatState.Enemies.Any(static creature => creature.HasPower<ReattachPower>());
        bool slotPositionMismatch = false;
        if (!hasInvalidEnemySlots && encounterSlots != null)
        {
            foreach (Creature creature in combatState.Enemies)
            {
                if (string.IsNullOrWhiteSpace(creature.SlotName))
                    continue;

                NCreature? node = combatRoom.GetCreatureNode(creature);
                if (node == null || !encounterSlots.HasNode(creature.SlotName))
                    continue;

                Vector2 expectedPosition = encounterSlots.GetNode<Marker2D>(creature.SlotName).GlobalPosition;
                if (node.GlobalPosition.DistanceTo(expectedPosition) > 1f)
                {
                    slotPositionMismatch = true;
                    break;
                }
            }
        }

        bool topologyMismatch = forceRebuild
            || creatureNodes.Count != creatures.Count
            || removingNodes.Count > 0
            || creatures.Any(creature => combatRoom.GetCreatureNode(creature) == null)
            || slotPositionMismatch
            || hasInvalidEnemySlots;
        if (!topologyMismatch)
            return;

        foreach (NCreature node in creatureNodes.Concat(removingNodes).Distinct())
        {
            node.GetParent()?.RemoveChild(node);
            node.QueueFreeSafely();
        }

        GetPrivateFieldValue<System.Collections.IList>(combatRoom, "_creatureNodes")?.Clear();
        GetPrivateFieldValue<System.Collections.IList>(combatRoom, "_removingCreatureNodes")?.Clear();

        if (GetPrivateFieldValue<Control>(combatRoom, "_allyContainer") is { } allyContainer)
            ClearNodeChildren(allyContainer);

        if (GetPrivateFieldValue<Control>(combatRoom, "_enemyContainer") is { } enemyContainer)
            ClearNodeChildren(enemyContainer);

        SetPrivatePropertyValue(combatRoom, "EncounterSlots", null);
        InvokePrivateMethod(combatRoom, "CreateAllyNodes");
        if (hasInvalidEnemySlots)
            RebuildEnemyNodesWithFallbackLayout(combatRoom, combatState);
        else
            InvokePrivateMethod(combatRoom, "CreateEnemyNodes");
        InvokePrivateMethod(combatRoom, "AdjustCreatureScaleForAspectRatio");
        InvokePrivateMethod(combatRoom, "UpdateCreatureNavigation");
    }

    private static void RebuildEnemyNodesWithFallbackLayout(NCombatRoom combatRoom, CombatState combatState)
    {
        Dictionary<Creature, string?> originalSlots = combatState.Enemies.ToDictionary(static creature => creature, static creature => creature.SlotName);
        foreach (Creature creature in combatState.Enemies)
            creature.SlotName = null;

        foreach (Creature creature in combatState.Enemies)
            combatRoom.AddCreature(creature);

        foreach ((Creature creature, string? slotName) in originalSlots)
            creature.SlotName = slotName;

        List<NCreature> enemyNodes = combatState.Enemies
            .Select(combatRoom.GetCreatureNode)
            .Where(static node => node != null)
            .Cast<NCreature>()
            .ToList();
        InvokePrivateMethod(combatRoom, "PositionEnemies", enemyNodes, GetCombatRoomEncounterScaling(combatRoom));
        InvokePrivateMethod(combatRoom, "RandomizeEnemyScalesAndHues");
    }

    private static float GetCombatRoomEncounterScaling(NCombatRoom combatRoom)
    {
        object? visuals = GetPrivateFieldValue<object>(combatRoom, "_visuals");
        object? encounter = visuals == null ? null : FindProperty(visuals.GetType(), "Encounter")?.GetValue(visuals);
        object? scaling = encounter == null ? null : FindMethod(encounter.GetType(), "GetCameraScaling")?.Invoke(encounter, null);
        return scaling is float floatScaling ? floatScaling : 1f;
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
        RestoreSelectedHandCardsToHand(hand);
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
        SetPrivateFieldValue(hand, "_selectionCompletionSource", null);
        SetPrivateFieldValue(hand, "_currentSelectionFilter", null);
        SetPrivateFieldValue(hand, "_prefs", default(CardSelectorPrefs));
        SetPrivatePropertyValue(hand, "FocusedHolder", null);
        hand.CardHolderContainer.FocusMode = Control.FocusModeEnum.All;
        hand.Position = GetStaticFieldValue<Vector2>(typeof(NPlayerHand), "_showPosition");
        hand.Modulate = Colors.White;
        ClearNodeChildren(hand.CardHolderContainer);
        ResetSelectedHandCardContainerState(hand);
        HideControl(hand, "%SelectModeBackstop", Control.MouseFilterEnum.Ignore);
        HideControl(hand, "%UpgradePreviewContainer");
        HideControl(hand, "%SelectionHeader");
        if (GetPrivateFieldValue<object>(hand, "_upgradePreview") is { } upgradePreview)
            SetPrivatePropertyValue(upgradePreview, "Card", null);
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

    private static bool IsTweenRunning(object instance, string fieldName)
    {
        if (GetPrivateFieldValue<Tween>(instance, fieldName) is not { } tween)
            return false;

        return GodotObject.IsInstanceValid(tween) && tween.IsRunning();
    }

    private static bool ContainsDescendantOfType<TNode>(Node? node) where TNode : Node
    {
        if (node == null || !GodotObject.IsInstanceValid(node))
            return false;

        foreach (Node child in node.GetChildren())
        {
            if (child is TNode)
                return true;

            if (ContainsDescendantOfType<TNode>(child))
                return true;
        }

        return false;
    }
    private static void CaptureSelectedHandContainerDefaults(NPlayerHand hand)
    {
        NSelectedHandCardContainer? selectedContainer = GetPrivateFieldValue<NSelectedHandCardContainer>(hand, "_selectedHandCardContainer")
            ?? hand.GetNodeOrNull<NSelectedHandCardContainer>("%SelectedHandCardContainer");
        if (selectedContainer == null || !GodotObject.IsInstanceValid(selectedContainer))
            return;

        ulong id = selectedContainer.GetInstanceId();
        if (!SelectedHandContainerDefaultPositions.ContainsKey(id))
            SelectedHandContainerDefaultPositions[id] = selectedContainer.Position;
        if (!SelectedHandContainerDefaultScales.ContainsKey(id))
            SelectedHandContainerDefaultScales[id] = selectedContainer.Scale;
    }

    private static void ClearCreatureIntentUi(NCreature creatureNode)
    {
        creatureNode.AnimHideIntent(0f);
        ClearNodeChildren(creatureNode.IntentContainer);
    }

    private static void SnapEnemyCreatureNodesToSlots(CombatState combatState)
    {
        NCombatRoom? combatRoom = NCombatRoom.Instance;
        if (combatRoom == null)
            return;

        Control? encounterSlots = GetPrivateFieldValue<Control>(combatRoom, "<EncounterSlots>k__BackingField")
            ?? GetPrivateFieldValue<Control>(combatRoom, "EncounterSlots");
        if (encounterSlots == null)
            return;

        foreach (Creature creature in combatState.Enemies)
        {
            if (string.IsNullOrWhiteSpace(creature.SlotName) || !encounterSlots.HasNode(creature.SlotName))
                continue;

            NCreature? node = combatRoom.GetCreatureNode(creature);
            if (node == null)
                continue;

            node.GlobalPosition = encounterSlots.GetNode<Marker2D>(creature.SlotName).GlobalPosition;
        }
    }
    private static void ClearTransientCardVisuals()
    {
        NCombatRoom? combatRoom = NCombatRoom.Instance;
        if (combatRoom != null)
        {
            RemoveCardFlyVfxNodes(combatRoom.CombatVfxContainer);
            ClearNodeChildren(combatRoom.Ui.CardPreviewContainer);
            ClearNodeChildren(combatRoom.Ui.MessyCardPreviewContainer);
        }

        var globalUi = NRun.Instance?.GlobalUi;
        if (globalUi == null)
            return;

        ClearNodeChildren(globalUi.CardPreviewContainer);
        ClearNodeChildren(globalUi.MessyCardPreviewContainer);
        ClearNodeChildren(globalUi.GridCardPreviewContainer);
        ClearNodeChildren(globalUi.EventCardPreviewContainer);
        RemoveCardFlyVfxNodes(globalUi.TopBar?.TrailContainer);
    }

    private static void RemoveCardFlyVfxNodes(Node? root)
    {
        if (root == null || !GodotObject.IsInstanceValid(root))
            return;

        foreach (Node child in root.GetChildren().Cast<Node>().ToList())
        {
            RemoveCardFlyVfxNodes(child);
            if (child is not NCardFlyVfx flyVfx)
                continue;

            if (GetPrivateFieldValue<Node>(flyVfx, "_vfx") is { } trailVfx && GodotObject.IsInstanceValid(trailVfx))
            {
                trailVfx.GetParent()?.RemoveChild(trailVfx);
                trailVfx.QueueFreeSafely();
            }

            if (GetPrivateFieldValue<NCard>(flyVfx, "_card") is { } cardNode && GodotObject.IsInstanceValid(cardNode))
            {
                cardNode.GetParent()?.RemoveChild(cardNode);
                cardNode.QueueFreeSafely();
            }

            flyVfx.GetParent()?.RemoveChild(flyVfx);
            flyVfx.QueueFreeSafely();
        }
    }

    private void DetachPendingHandSelectionSource(NPlayerHand hand)
    {
        if (_pendingHandChoiceSource == null)
            return;

        MethodInfo? handlerMethod = FindMethod(hand.GetType(), "OnSelectModeSourceFinished");
        if (handlerMethod == null)
        {
            _pendingHandChoiceSource = null;
            return;
        }

        try
        {
            Action<AbstractModel> handler = (Action<AbstractModel>)handlerMethod.CreateDelegate(typeof(Action<AbstractModel>), hand);
            _pendingHandChoiceSource.ExecutionFinished -= handler;
        }
        catch
        {
        }

        _pendingHandChoiceSource = null;
    }
    private static void RestoreSelectedHandCardsToHand(NPlayerHand hand)
    {
        NSelectedHandCardContainer? selectedContainer = GetPrivateFieldValue<NSelectedHandCardContainer>(hand, "_selectedHandCardContainer")
            ?? hand.GetNodeOrNull<NSelectedHandCardContainer>("%SelectedHandCardContainer");
        if (selectedContainer == null || !GodotObject.IsInstanceValid(selectedContainer))
            return;

        foreach (NSelectedHandCardHolder selectedHolder in selectedContainer.Holders.ToList())
        {
            NCard? cardNode = selectedHolder.CardNode;
            selectedHolder.GetParent()?.RemoveChild(selectedHolder);
            selectedHolder.QueueFreeSafely();
            if (cardNode == null || !GodotObject.IsInstanceValid(cardNode))
                continue;

            try
            {
                hand.Add(cardNode, -1);
            }
            catch
            {
                cardNode.GetParent()?.RemoveChild(cardNode);
                cardNode.QueueFreeSafely();
            }
        }
    }

    private static void ResetSelectedHandCardContainerState(NPlayerHand hand)
    {
        NSelectedHandCardContainer? selectedContainer = GetPrivateFieldValue<NSelectedHandCardContainer>(hand, "_selectedHandCardContainer")
            ?? hand.GetNodeOrNull<NSelectedHandCardContainer>("%SelectedHandCardContainer");
        if (selectedContainer == null || !GodotObject.IsInstanceValid(selectedContainer))
            return;

        selectedContainer.Hand = hand;
        ulong id = selectedContainer.GetInstanceId();
        if (SelectedHandContainerDefaultPositions.TryGetValue(id, out Vector2 defaultPosition))
            selectedContainer.Position = defaultPosition;
        if (SelectedHandContainerDefaultScales.TryGetValue(id, out Vector2 defaultScale))
            selectedContainer.Scale = defaultScale;
        else
            selectedContainer.Scale = Vector2.One;
        selectedContainer.FocusMode = Control.FocusModeEnum.None;
        ClearNodeChildren(selectedContainer);
        InvokePrivateMethod(selectedContainer, "RefreshHolderPositions");
        InvokePrivateMethod(hand, "UpdateSelectedCardContainer", 0);
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
        NCard? cardNode = NCard.Create(card, ModelVisibility.Visible);
        if (cardNode == null)
            throw new InvalidOperationException("Failed to create NCard.");

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
        PropertyInfo? property = FindProperty(instance.GetType(), propertyName);
        MethodInfo? setter = property?.GetSetMethod(true);
        if (setter != null)
        {
            setter.Invoke(instance, [value]);
            return;
        }

                property?.SetValue(instance, value);
    }

    private static bool TrySetRuntimePropertyValue(object instance, PropertyInfo property, string propertyName, object? value)
    {
        if (ShouldSkipRuntimeStateProperty(instance.GetType(), propertyName))
            return false;

        if (TrySetPrivateAutoPropertyBackingField(instance, propertyName, value))
            return true;

        MethodInfo? setter = property.GetSetMethod(true);
        if (setter == null)
            return false;

        setter.Invoke(instance, [value]);
        return true;
    }

    private static bool TrySetPrivateAutoPropertyBackingField(object instance, string propertyName, object? value)
    {
        FieldInfo? backingField = FindField(instance.GetType(), $"<{propertyName}>k__BackingField");
        if (backingField == null)
            return false;

        backingField.SetValue(instance, value);
        return true;
    }

    private static object? InvokePrivateMethod(object instance, string methodName, params object?[]? args)
    {
        return FindMethod(instance.GetType(), methodName)?.Invoke(instance, args);
    }

    private static string DescribeException(Exception ex)
    {
        List<string> parts = [];
        Exception? current = ex;
        while (current != null)
        {
            parts.Add($"{current.GetType().Name}: {current.Message}");
            current = current.InnerException;
        }

        return string.Join(" | ", parts);
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

    private static UndoCombatFullState CreateDerivedCombatState(
        UndoCombatFullState source,
        NetFullCombatState fullState,
        IReadOnlyList<UndoPlayerPileCardCostState>? cardCostStates = null,
        IReadOnlyList<UndoPlayerPileCardRuntimeState>? cardRuntimeStates = null)
    {
        IReadOnlyList<UndoPlayerPileCardCostState> effectiveCardCostStates = cardCostStates ?? source.CardCostStates;
        IReadOnlyList<UndoPlayerPileCardRuntimeState> effectiveCardRuntimeStates = cardRuntimeStates ?? source.CardRuntimeStates;
        return new UndoCombatFullState(
            fullState,
            source.RoundNumber,
            source.CurrentSide,
            source.SynchronizerCombatState,
            source.NextActionId,
            source.NextHookId,
            source.NextChecksumId,
            source.CombatHistoryState,
            source.ActionKernelState,
            source.MonsterStates,
            effectiveCardCostStates,
            effectiveCardRuntimeStates,
            source.PowerRuntimeStates,
            source.RelicRuntimeStates,
            source.SelectionSessionState,
            source.FirstInSeriesPlayCounts,
            new RuntimeGraphState
            {
                CardRuntimeStates = effectiveCardRuntimeStates,
                PowerRuntimeStates = source.PowerRuntimeStates,
                RelicRuntimeStates = source.RelicRuntimeStates
            },
            new PresentationHints
            {
                SelectionSessionState = source.SelectionSessionState
            },
            source.CreatureTopologyStates,
            source.CreatureStatusRuntimeStates,
            source.CombatCardDbState,
            source.PlayerOrbStates,
            source.PlayerDeckStates,
            source.SchemaVersion);
    }

    private static UndoCombatFullState CreateDerivedCombatState(
        UndoCombatFullState source,
        UndoCombatFullState? fallbackSource,
        NetFullCombatState fullState)
    {
        RebuildDerivedCardSupplementalStates(source, fallbackSource, fullState, out IReadOnlyList<UndoPlayerPileCardCostState> cardCostStates, out IReadOnlyList<UndoPlayerPileCardRuntimeState> cardRuntimeStates);
        return CreateDerivedCombatState(source, fullState, cardCostStates, cardRuntimeStates);
    }

    private static void RebuildDerivedCardSupplementalStates(
        UndoCombatFullState primarySource,
        UndoCombatFullState? fallbackSource,
        NetFullCombatState fullState,
        out IReadOnlyList<UndoPlayerPileCardCostState> cardCostStates,
        out IReadOnlyList<UndoPlayerPileCardRuntimeState> cardRuntimeStates)
    {
        List<UndoPlayerPileCardCostState> rebuiltCostStates = [];
        List<UndoPlayerPileCardRuntimeState> rebuiltRuntimeStates = [];

        foreach (NetFullCombatState.PlayerState playerState in fullState.Players)
        {
            List<DerivedCardSupplementalCandidate> primaryCandidates = BuildDerivedCardSupplementalCandidates(primarySource, playerState.playerId);
            List<DerivedCardSupplementalCandidate> fallbackCandidates = fallbackSource == null
                ? []
                : BuildDerivedCardSupplementalCandidates(fallbackSource, playerState.playerId);
            Dictionary<PileType, NetFullCombatState.CombatPileState> pilesByType = playerState.piles.ToDictionary(static pile => pile.pileType);

            foreach (PileType pileType in CombatPileOrder)
            {
                List<UndoCardCostState> pileCostStates = [];
                List<UndoCardRuntimeState> pileRuntimeStates = [];
                if (pilesByType.TryGetValue(pileType, out NetFullCombatState.CombatPileState pileState))
                {
                    for (int cardIndex = 0; cardIndex < pileState.cards.Count; cardIndex++)
                    {
                        NetFullCombatState.CardState cardState = pileState.cards[cardIndex];
                        if (!TryTakeMatchingCardSupplementalState(primaryCandidates, pileType, cardIndex, cardState.card, out UndoCardCostState? costState, out UndoCardRuntimeState? runtimeState)
                            && !TryTakeMatchingCardSupplementalState(fallbackCandidates, pileType, cardIndex, cardState.card, out costState, out runtimeState))
                        {
                            CardModel defaultCard = CardModel.FromSerializable(ClonePacketSerializable(cardState.card));
                            costState = CaptureCardCostState(defaultCard);
                            runtimeState = CaptureDefaultCardRuntimeState(defaultCard);
                        }

                        pileCostStates.Add(costState ?? CaptureCardCostState(CardModel.FromSerializable(ClonePacketSerializable(cardState.card))));
                        pileRuntimeStates.Add(runtimeState ?? CaptureDefaultCardRuntimeState(CardModel.FromSerializable(ClonePacketSerializable(cardState.card))));
                    }
                }

                rebuiltCostStates.Add(new UndoPlayerPileCardCostState
                {
                    PlayerNetId = playerState.playerId,
                    PileType = pileType,
                    Cards = pileCostStates
                });
                rebuiltRuntimeStates.Add(new UndoPlayerPileCardRuntimeState
                {
                    PlayerNetId = playerState.playerId,
                    PileType = pileType,
                    Cards = pileRuntimeStates
                });
            }
        }

        cardCostStates = rebuiltCostStates;
        cardRuntimeStates = rebuiltRuntimeStates;
    }

    private static List<DerivedCardSupplementalCandidate> BuildDerivedCardSupplementalCandidates(UndoCombatFullState source, ulong playerNetId)
    {
        NetFullCombatState.PlayerState playerState = default;
        bool foundPlayer = false;
        foreach (NetFullCombatState.PlayerState candidate in source.FullState.Players)
        {
            if (candidate.playerId != playerNetId)
                continue;

            playerState = candidate;
            foundPlayer = true;
            break;
        }

        if (!foundPlayer)
            return [];

        IReadOnlyDictionary<PileType, IReadOnlyList<UndoCardCostState>>? costStatesByPile = GetCardCostStatesForPlayer(source, playerNetId);
        IReadOnlyDictionary<PileType, IReadOnlyList<UndoCardRuntimeState>>? runtimeStatesByPile = GetCardRuntimeStatesForPlayer(source, playerNetId);
        Dictionary<PileType, NetFullCombatState.CombatPileState> pilesByType = playerState.piles.ToDictionary(static pile => pile.pileType);
        List<DerivedCardSupplementalCandidate> candidates = [];

        foreach (PileType pileType in CombatPileOrder)
        {
            if (!pilesByType.TryGetValue(pileType, out NetFullCombatState.CombatPileState pileState))
                continue;

            IReadOnlyList<UndoCardCostState>? pileCostStates = null;
            IReadOnlyList<UndoCardRuntimeState>? pileRuntimeStates = null;
            costStatesByPile?.TryGetValue(pileType, out pileCostStates);
            runtimeStatesByPile?.TryGetValue(pileType, out pileRuntimeStates);
            for (int cardIndex = 0; cardIndex < pileState.cards.Count; cardIndex++)
            {
                candidates.Add(new DerivedCardSupplementalCandidate
                {
                    PileType = pileType,
                    CardIndex = cardIndex,
                    Card = ClonePacketSerializable(pileState.cards[cardIndex].card),
                    CostState = pileCostStates != null && cardIndex < pileCostStates.Count ? pileCostStates[cardIndex] : null,
                    RuntimeState = pileRuntimeStates != null && cardIndex < pileRuntimeStates.Count ? pileRuntimeStates[cardIndex] : null
                });
            }
        }

        return candidates;
    }

    private static bool TryTakeMatchingCardSupplementalState(
        List<DerivedCardSupplementalCandidate> candidates,
        PileType pileType,
        int cardIndex,
        SerializableCard card,
        out UndoCardCostState? costState,
        out UndoCardRuntimeState? runtimeState)
    {
        for (int i = 0; i < candidates.Count; i++)
        {
            DerivedCardSupplementalCandidate candidate = candidates[i];
            if (candidate.Used
                || candidate.PileType != pileType
                || candidate.CardIndex != cardIndex
                || !PacketDataEquals(candidate.Card, card))
            {
                continue;
            }

            candidate.Used = true;
            costState = candidate.CostState;
            runtimeState = candidate.RuntimeState;
            return true;
        }

        for (int i = 0; i < candidates.Count; i++)
        {
            DerivedCardSupplementalCandidate candidate = candidates[i];
            if (candidate.Used || !PacketDataEquals(candidate.Card, card))
                continue;

            candidate.Used = true;
            costState = candidate.CostState;
            runtimeState = candidate.RuntimeState;
            return true;
        }

        costState = null;
        runtimeState = null;
        return false;
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
        return ClonePacketSerializable(state);
    }

    private static T ClonePacketSerializable<T>(T value) where T : IPacketSerializable, new()
    {
        PacketReader reader = new();
        reader.Reset(SerializePacketSerializable(value));
        return reader.Read<T>();
    }

    private static bool PacketDataEquals<T>(T left, T right) where T : IPacketSerializable
    {
        return SerializePacketSerializable(left).AsSpan().SequenceEqual(SerializePacketSerializable(right));
    }

    private static byte[] SerializePacketSerializable<T>(T value) where T : IPacketSerializable
    {
        PacketWriter writer = new() { WarnOnGrow = false };
        writer.Write(value);
        writer.ZeroByteRemainder();

        byte[] buffer = new byte[writer.BytePosition];
        Array.Copy(writer.Buffer, buffer, writer.BytePosition);
        return buffer;
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

    private static Task<T?> RunOnMainThreadAsync<T>(Func<T?> action)
    {
        if (NGame.IsMainThread())
            return Task.FromResult(action());

        TaskCompletionSource<T?> completionSource = new();
        Node? dispatcher = NGame.Instance ?? (Node?)NRun.Instance ?? NCombatRoom.Instance;
        if (dispatcher == null)
        {
            completionSource.SetException(new InvalidOperationException("No main-thread dispatcher was available."));
            return completionSource.Task;
        }

        Callable.From(() =>
        {
            try
            {
                completionSource.SetResult(action());
            }
            catch (Exception ex)
            {
                completionSource.SetException(ex);
            }
        }).CallDeferred();

        return completionSource.Task;
    }

    private sealed class DerivedCardSupplementalCandidate
    {
        public required PileType PileType { get; init; }

        public required int CardIndex { get; init; }

        public required SerializableCard Card { get; init; }

        public UndoCardCostState? CostState { get; init; }

        public UndoCardRuntimeState? RuntimeState { get; init; }

        public bool Used { get; set; }
    }

    private sealed class SyntheticChoiceVfxRequest
    {
        public List<SyntheticExhaustVfxCard> ExhaustCards { get; } = [];

        public PileType? TransformPileType { get; set; }

        public List<SyntheticTransformVfxCard> TransformCards { get; } = [];

        public bool HasEffects => ExhaustCards.Count > 0 || TransformCards.Count > 0;
    }

    private sealed record SyntheticExhaustVfxCard(SerializableCard Card, Vector2 GlobalPosition);

    private sealed record SyntheticTransformVfxCard(SerializableCard Card, int SourcePileIndex);
}





























































































































