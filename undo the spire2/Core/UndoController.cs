// 文件说明：协调撤销、重做、选牌分支和历史栈的主控制器。
// Coordinates undo/redo history and restore transactions.
// Capture/restore details should live in dedicated services; this type is the orchestrator.
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Commands;
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
using MegaCrit.Sts2.Core.Nodes.CommonUi;
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

public sealed partial class UndoController
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
        }

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
            [.. RunManager.Instance.PlayerChoiceSynchronizer.ChoiceIds],
            []);

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

    public void RecordReplayChecksum(NetChecksumData checksum, string context, NetFullCombatState fullCombatState)
    {
        if (_combatReplay == null || IsRestoring || !CombatManager.Instance.IsInProgress)
            return;

        _combatReplay.ChecksumData.Add(new ReplayChecksumData
        {
            checksumData = checksum,
            context = context,
            fullState = CloneFullState(fullCombatState)
        });
    }

    public void RegisterPendingChooseACardChoice(Player player, IReadOnlyList<CardModel> cards, bool canSkip, AbstractModel? source = null)
    {
        if (!ShouldTrackLocalChoice(player))
            return;

        _pendingChoiceSpec = UndoChoiceSpec.CreateChooseACard(cards, canSkip, source);
    }

    public void RegisterPendingHandChoice(Player player, CardSelectorPrefs prefs, Func<CardModel, bool>? filter, AbstractModel? source)
    {
        if (!ShouldTrackLocalChoice(player))
            return;

        _pendingChoiceSpec = UndoChoiceSpec.CreateHandSelection(player, prefs, filter, source);
        _pendingHandChoiceSource = source;
    }

    public void RegisterPendingSimpleGridChoice(Player player, IReadOnlyList<CardModel> cards, CardSelectorPrefs prefs, AbstractModel? source = null)
    {
        if (!ShouldTrackLocalChoice(player))
            return;

        _pendingChoiceSpec = UndoChoiceSpec.CreateSimpleGridSelection(player, cards, prefs, source);
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
                CaptureCurrentCombatFullState(isCurrentChoiceAnchor ? currentChoiceAnchor?.ChoiceSpec : null),
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
            if (restoreMode is "full_state" or "choice_skipped")
            {
                _combatReplay.ActiveEventCount = snapshot.ReplayEventCount;
                EnsurePlayerChoiceUndoAnchor(snapshot);
            }
            MainFile.Logger.Info($"{operation} completed. Restored={snapshot.ActionLabel}, ReplayEvents={snapshot.ReplayEventCount}, Mode={restoreMode}");
        }
                catch (Exception ex)
        {
            if (currentSnapshot != null)
            {
                if (destination.First?.Value == currentSnapshot)
                    destination.RemoveFirst();
                else
                    destination.Remove(currentSnapshot);
            }
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
                if (baseSnapshot != null
                    && snapshot.CombatState.ActionKernelState.PausedChoiceState is PausedChoiceState replayableChoice
                    && ShouldUseChoiceAnchorReplay(replayableChoice)
                    && await TryRestorePlayerChoiceInPlaceAsync(baseSnapshot, snapshot)
                    && await TryRestorePrimaryChoiceAsync(snapshot, choiceBranchTemplate, stateAlreadyApplied: true))
                {
                    WriteInteractionLog("primary_restore", $"label={snapshot.ActionLabel} replayEvents={snapshot.ReplayEventCount} mode=choice_anchor_replay");
                    return "primary_choice";
                }

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
    private async Task<bool> TryRestorePlayerChoiceInPlaceAsync(UndoSnapshot baseSnapshot, UndoSnapshot targetSnapshot)
    {
        if (baseSnapshot.ReplayEventCount > targetSnapshot.ReplayEventCount)
            return false;

        if (!await TryApplyFullStateInPlaceAsync(baseSnapshot.CombatState))
            return false;

        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
            return false;

        await ReplayEventsAsync(runState, baseSnapshot.ReplayEventCount, targetSnapshot.ReplayEventCount);
        await WaitForReplayToSettleAsync();
        await WaitForChoiceUiReadyAsync();
        return IsChoiceUiReady();
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
            ResetActionSynchronizerForRestore();
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
            RunManager.Instance.ChecksumTracker.LoadReplayChecksums(GetReplayChecksumsFrom(snapshot.NextChecksumId), snapshot.NextChecksumId);
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

            await RefreshCombatUiAsync(combatState, snapshot);
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
        RunManager.Instance.ChecksumTracker.LoadReplayChecksums(GetReplayChecksumsFrom(_combatReplay.InitialNextChecksumId), _combatReplay.InitialNextChecksumId);
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
                CaptureCurrentCombatFullState(isChoiceAnchor ? choiceSpec : null),
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
            if (!isChoiceAnchor)
                TryPersistImmediateChoiceBranchSnapshot(snapshot);
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
        return UndoChoiceSpec.CreateHandSelection(me, prefs, static card => !card.ShouldRetainThisTurn, wellLaidPlans);
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
        return UndoChoiceSpec.CreateHandSelection(me, prefs, null, entropy);
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
        while (NOverlayStack.Instance?.Peek() is IOverlayScreen choiceScreen
            && choiceScreen is NChooseACardSelectionScreen or NCardGridSelectionScreen)
        {
            RemoveChoiceOverlaySafely(choiceScreen);
            removedOverlay = true;
            await WaitOneFrameAsync();
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
                ResetPlayerHandUi(hand);
                _pendingHandChoiceSource = null;
            }

            if (ShouldForceResetHandUiForRestore(hand))
            {
                ResetPlayerHandUi(hand);
                await WaitOneFrameAsync();
                await WaitOneFrameAsync();
            }
        }

        if (removedOverlay)
            await WaitOneFrameAsync();
    }

    private static bool ShouldForceResetHandUiForRestore(NPlayerHand hand)
    {
        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        Player? me = combatState == null ? null : LocalContext.GetMe(combatState);
        int expectedHandCount = me == null ? -1 : PileType.Hand.GetPile(me).Cards.Count;
        int holderCount = hand.CardHolderContainer.GetChildCount();
        int selectedHolderCount = (GetPrivateFieldValue<NSelectedHandCardContainer>(hand, "_selectedHandCardContainer")
            ?? hand.GetNodeOrNull<NSelectedHandCardContainer>("%SelectedHandCardContainer"))
            ?.Holders.Count
            ?? 0;
        bool hasCurrentCardPlay = GetPrivateFieldValue<Node>(hand, "_currentCardPlay") is Node currentCardPlay
            && GodotObject.IsInstanceValid(currentCardPlay);

        if (holderCount > 10)
            return true;

        if (expectedHandCount >= 0 && holderCount > expectedHandCount + 1)
            return true;

        return selectedHolderCount > 0 || hasCurrentCardPlay;
    }

    private static void RemoveChoiceOverlaySafely(IOverlayScreen choiceScreen)
    {
        NOverlayStack? overlayStack = NOverlayStack.Instance;
        Node? choiceNode = choiceScreen as Node;
        if (overlayStack == null)
        {
            QueueFreeNodeSafelyNoPoolOnce(choiceNode);
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
            QueueFreeNodeSafelyNoPoolOnce(choiceNode);
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
        public List<SyntheticDiscardVfxCard> DiscardCards { get; } = [];

        public List<SyntheticExhaustVfxCard> ExhaustCards { get; } = [];

        public PileType? TransformPileType { get; set; }

        public List<SyntheticTransformVfxCard> TransformCards { get; } = [];

        public bool HasEffects => DiscardCards.Count > 0 || ExhaustCards.Count > 0 || TransformCards.Count > 0;
    }

    private sealed record SyntheticDiscardVfxCard(SerializableCard Card, Vector2 GlobalPosition);

    private sealed record SyntheticExhaustVfxCard(SerializableCard Card, Vector2 GlobalPosition);

    private sealed record SyntheticTransformVfxCard(SerializableCard Card, int SourcePileIndex);
}









