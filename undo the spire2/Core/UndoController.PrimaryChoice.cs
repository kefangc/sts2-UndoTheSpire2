// 文件说明：承载 primary choice 恢复、live/custom 分支提交和相关辅助逻辑。
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
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
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

        if (pausedChoiceState != null
            && IsTrackedOfficialFromHandDiscardChoice(pausedChoiceState, choiceSpec)
            && !CanResumeTrackedOfficialHandDiscardLive(pausedChoiceState, choiceSpec, out string? liveUnavailableReason))
        {
            UndoDebugLog.Write(
                $"official_hand_choice_primary_restore_unavailable source={choiceSpec.SourceModelTypeName ?? "unknown"}"
                + $" reason={liveUnavailableReason ?? "missing_live_choice_state"}"
                + $" replayEvents={snapshot.ReplayEventCount}->{GetCurrentReplayEventCount()}");
            return false;
        }

        UndoSyntheticChoiceSession primarySession = new(
            snapshot,
            choiceSpec,
            branchSnapshot,
            requiresAuthoritativeBranchExecution: ShouldRequireAuthoritativeSyntheticChoiceExecution(choiceSpec));
        _syntheticChoiceSession = primarySession;
        RememberResolvedChoiceBranch(choiceSpec, null, allowImmediateContinuation: false);
        RememberSavedChoiceBranches(primarySession, snapshot.CombatState.ChoiceBranchStates);
        if (branchSnapshot?.ChoiceResultKey != null)
            primarySession.RememberBranch(branchSnapshot.ChoiceResultKey, branchSnapshot);
        bool shouldHandleSelectionAsync =
            choiceSpec.Kind is UndoChoiceKind.ChooseACard or UndoChoiceKind.SimpleGridSelection
            || (choiceSpec.Kind == UndoChoiceKind.HandSelection
                && pausedChoiceState != null
                && !ShouldUseSyntheticEntropyChoice(pausedChoiceState, choiceSpec));
        if (shouldHandleSelectionAsync)
        {
            Task<UndoChoiceResultKey?> selectionTask;
            if (pausedChoiceState != null)
                selectionTask = UndoActionCodecRegistry.RestoreAsync(pausedChoiceState, runState);
            else
                selectionTask = RestorePrimaryChoiceAnchorAsync(choiceSpec, runState);
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
                if (IsOfficialFromHandDiscardChoice(primarySession.ChoiceSpec))
                    DiscardDeferredActionSnapshots("choice_anchor_reopen_failed");
                _syntheticChoiceSession = null;
                UndoDebugLog.Write($"primary_choice_restore_selected_key:null replayEvents={snapshot.ReplayEventCount} label={snapshot.ActionLabel} codec={pausedChoiceState?.SourceActionCodecId ?? "action:choose-a-card"} stage=anchor_reopen_failed");
                return false;
            }

            WriteInteractionLog("primary_restore_anchor_reopened", $"label={snapshot.ActionLabel} replayEvents={snapshot.ReplayEventCount} codec={pausedChoiceState?.SourceActionCodecId ?? "action:choose-a-card"}");
            NotifyStateChanged();
            // 对 from-hand 这类手牌选择，不要在 restore 事务里等待玩家选完。
            // 只要 choice UI 已经真正回到场上，就立刻结束 restore，让 HUD 和二次 undo 恢复可用。
            TaskHelper.RunSafely(HandlePrimaryChoiceSelectionAsync(
                primarySession,
                selectionTask,
                ShouldPreferLiveBranchCommit(pausedChoiceState, choiceSpec, stateAlreadyApplied)));
            return true;
        }

        UndoChoiceResultKey? selectedKey;

        try
        {
            selectedKey = ShouldUseSyntheticEntropyChoice(pausedChoiceState, choiceSpec)
                ? await ShowSyntheticChoiceSelectionAsync(primarySession)
                : await UndoActionCodecRegistry.RestoreAsync(pausedChoiceState!, runState);
        }
        catch (TaskCanceledException)
        {
            UndoDebugLog.Write($"primary_choice_restore_selected_key:canceled replayEvents={snapshot.ReplayEventCount} label={snapshot.ActionLabel}");
            return false;
        }

        if (selectedKey == null)
        {
            if (IsOfficialFromHandDiscardChoice(primarySession.ChoiceSpec))
                DiscardDeferredActionSnapshots("primary_choice_selected_key_null");
            _syntheticChoiceSession = null;
            UndoDebugLog.Write($"primary_choice_restore_selected_key:null replayEvents={snapshot.ReplayEventCount} label={snapshot.ActionLabel} codec={pausedChoiceState?.SourceActionCodecId ?? "action:choose-a-card"}");
            return false;
        }

        UndoDebugLog.Write($"primary_choice_restore_selected_key:{selectedKey} replayEvents={snapshot.ReplayEventCount} label={snapshot.ActionLabel} codec={pausedChoiceState?.SourceActionCodecId ?? "action:choose-a-card"}");
        // 只有真正恢复了官方 paused action，才能直接接 live branch。
        if (stateAlreadyApplied && await TryCommitLiveChoiceBranchAsync(primarySession, selectedKey))
            return true;

        if (ShouldPreferCustomChoiceBeforeCached(primarySession)
            && await TryCommitCustomChoiceBranchAsync(primarySession, selectedKey))
            return true;

        if (primarySession.RequiresAuthoritativeBranchExecution)
        {
            UndoDebugLog.Write(
                $"authoritative_choice_branch_unavailable choice={selectedKey}"
                + $" label={snapshot.ActionLabel}"
                + $" replayEvents={snapshot.ReplayEventCount}"
                + $" source={primarySession.ChoiceSpec.SourceModelTypeName ?? "unknown"}"
                + " stage=primary_sync");
            return false;
        }

        if (primarySession.CachedBranches.TryGetValue(selectedKey, out UndoSnapshot? cachedBranchSnapshot))
        {
            SyntheticChoiceVfxRequest? cachedVfxRequest = CaptureSyntheticChoiceVfxRequest(primarySession, cachedBranchSnapshot, selectedKey);
            if (!await TryApplySynthesizedChoiceBranchAsync(primarySession, cachedBranchSnapshot, selectedKey, cachedVfxRequest))
                return false;

            return true;
        }

        if (await TryCommitCustomChoiceBranchAsync(primarySession, selectedKey))
            return true;

        UndoDebugLog.Write($"primary_choice_branch_miss:{selectedKey} replayEvents={snapshot.ReplayEventCount} label={snapshot.ActionLabel} cached={primarySession.CachedBranches.Count}");
        if (!TryCreateSyntheticChoiceBranchSnapshot(primarySession, selectedKey, out UndoSnapshot? synthesizedBranch))
        {
            MainFile.Logger.Warn($"Could not synthesize primary branch for restored choice {selectedKey}.");
            return false;
        }

        SyntheticChoiceVfxRequest? synthesizedVfxRequest = CaptureSyntheticChoiceVfxRequest(primarySession, synthesizedBranch!, selectedKey);
        return await TryApplySynthesizedChoiceBranchAsync(primarySession, synthesizedBranch!, selectedKey, synthesizedVfxRequest);
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
                IReadOnlyList<CardModel> options = choiceSpec.BuildOptionCards(player);
                NChooseACardSelectionScreen screen = NChooseACardSelectionScreen.ShowScreen(options, choiceSpec.CanSkip);
                CardModel? selected = (await screen.CardsSelected()).FirstOrDefault();
                return choiceSpec.TryMapDisplayedOptionSelection(options, selected == null ? [] : [selected]);
            }
            case UndoChoiceKind.SimpleGridSelection:
            {
                // 这里只负责把 choice UI 重新打开，后续分支提交仍由外层统一分流。
                IReadOnlyList<CardModel> options = choiceSpec.BuildOptionCards(player);
                NSimpleCardSelectScreen screen = NSimpleCardSelectScreen.Create(options, choiceSpec.SelectionPrefs);
                NOverlayStack.Instance.Push(screen);
                IEnumerable<CardModel> selected = await screen.CardsSelected();
                return choiceSpec.TryMapDisplayedSimpleGridSelection(options, selected);
            }
            default:
                return null;
        }
    }

    private static bool ShouldUseChoiceAnchorReplay(PausedChoiceState pausedChoiceState)
    {
        // choose-a-card 这类三选一在 replay 路径下会先恢复一遍 full-state，再让原 action 继续跑。
        // 对药水等 UsePotionAction 来源，这会把已经脱离队列的 action 恢复成“活着”的 paused action，
        // 后续 undo 时就可能出现 pop 不到队列、选择界面叠层和选项重掷。
        if (string.Equals(pausedChoiceState.SourceActionCodecId, "action:choose-a-card", StringComparison.Ordinal))
            return false;

        UndoChoiceSpec? choiceSpec = pausedChoiceState.ChoiceSpec;
        // 其他 from-hand 选择依然尽量复用 live paused action，避免 full-state restore 时
        // 命中手牌 holder/PlayCardAction 的中间态。
        if (string.Equals(pausedChoiceState.SourceActionCodecId, "action:from-hand", StringComparison.Ordinal))
        {
            if (choiceSpec != null
                && choiceSpec.SourcePileType == PileType.Hand
                && IsDiscardSelection(choiceSpec.SelectionPrefs))
            {
                return false;
            }

            return false;
        }

        // simple-grid 类选牌即使能 replay 回到 choice 点，也会先经历一轮 full-state
        // 还原，体感上会闪黑。对这类选择优先走直接 reopen + 分支提交。
        if (pausedChoiceState.ChoiceKind == UndoChoiceKind.SimpleGridSelection)
            return false;

        if (string.Equals(pausedChoiceState.ChoiceSpec?.SourceModelTypeName, typeof(EntropyPower).FullName, StringComparison.Ordinal))
            return false;

        if (string.Equals(pausedChoiceState.ChoiceSpec?.SourceModelTypeName, typeof(StratagemPower).FullName, StringComparison.Ordinal))
            return false;

        // Toolbox 的 choose-a-card 挂在 hook-choice 下面，但它后续只需要让官方把
        // 选中的牌 AddGeneratedCardToCombat。这里保留 replay，能恢复到真正活着的
        // paused action，避免后面再退回纯合成分支时因为没有模板 branch 而卡住。
        if (string.Equals(pausedChoiceState.SourceActionCodecId, "action:hook-choice", StringComparison.Ordinal)
            && string.Equals(pausedChoiceState.ChoiceSpec?.SourceModelTypeName, "MegaCrit.Sts2.Core.Models.Relics.Toolbox", StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(pausedChoiceState.SourceActionCodecId, "action:hook-choice", StringComparison.Ordinal))
            return false;

        return true;
    }

    private static bool ShouldUseSyntheticEntropyChoice(PausedChoiceState? pausedChoiceState, UndoChoiceSpec choiceSpec)
    {
        return string.Equals(pausedChoiceState?.SourceActionCodecId, "action:hook-choice", StringComparison.Ordinal)
            && choiceSpec.Kind == UndoChoiceKind.HandSelection
            && IsSourceChoice(choiceSpec, typeof(EntropyPower));
    }

    private async Task HandlePrimaryChoiceSelectionAsync(UndoSyntheticChoiceSession session, Task<UndoChoiceResultKey?> selectionTask, bool preferLiveBranchCommit)
    {
        try
        {
            UndoChoiceResultKey? selectedKey = await selectionTask;
            if (ShouldAbortStaleSyntheticChoiceSession(session, "selection_completed"))
                return;

            if (selectedKey == null)
            {
                UndoDebugLog.Write($"primary_choice_restore_selected_key:null replayEvents={session.AnchorSnapshot.ReplayEventCount} label={session.AnchorSnapshot.ActionLabel} codec={session.ChoiceSpec.Kind}");
                if (IsOfficialFromHandDiscardChoice(session.ChoiceSpec))
                    DiscardDeferredActionSnapshots("primary_choice_selected_key_null");
                if (ReferenceEquals(_syntheticChoiceSession, session))
                    _syntheticChoiceSession = null;
                NotifyStateChanged();
                return;
            }

            UndoDebugLog.Write($"primary_choice_restore_selected_key:{selectedKey} replayEvents={session.AnchorSnapshot.ReplayEventCount} label={session.AnchorSnapshot.ActionLabel} codec={session.ChoiceSpec.Kind}");
            if (preferLiveBranchCommit)
            {
                if (ShouldAbortStaleSyntheticChoiceSession(session, "before_live_commit"))
                    return;

                if (await TryCommitLiveChoiceBranchAsync(session, selectedKey))
                {
                    WriteInteractionLog("branch_commit_after_reselect", $"choice={selectedKey} label={session.AnchorSnapshot.ActionLabel} replayEvents={session.AnchorSnapshot.ReplayEventCount} source=live");
                    return;
                }

                if (ShouldAbortStaleSyntheticChoiceSession(session, "after_live_commit"))
                    return;
            }

            if (ShouldPreferCustomChoiceBeforeCached(session)
                && await TryCommitCustomChoiceBranchAsync(session, selectedKey))
            {
                WriteInteractionLog("branch_commit_after_reselect", $"choice={selectedKey} label={session.AnchorSnapshot.ActionLabel} replayEvents={session.AnchorSnapshot.ReplayEventCount} source=custom");
                return;
            }

            if (ShouldAbortStaleSyntheticChoiceSession(session, "after_preferred_custom_commit"))
                return;

            if (session.RequiresAuthoritativeBranchExecution)
            {
                UndoDebugLog.Write(
                    $"authoritative_choice_branch_unavailable choice={selectedKey}"
                    + $" label={session.AnchorSnapshot.ActionLabel}"
                    + $" replayEvents={session.AnchorSnapshot.ReplayEventCount}"
                    + $" source={session.ChoiceSpec.SourceModelTypeName ?? "unknown"}"
                    + " stage=primary");
                OpenSyntheticChoiceSession(session.AnchorSnapshot, session.TemplateSnapshot ?? _futureSnapshots.First?.Value);
                return;
            }

            if (session.CachedBranches.TryGetValue(selectedKey, out UndoSnapshot? cachedBranchSnapshot))
            {
                SyntheticChoiceVfxRequest? cachedVfxRequest = CaptureSyntheticChoiceVfxRequest(session, cachedBranchSnapshot, selectedKey);
                if (await TryApplySynthesizedChoiceBranchAsync(session, cachedBranchSnapshot, selectedKey, cachedVfxRequest))
                    WriteInteractionLog("branch_commit_after_reselect", $"choice={selectedKey} label={session.AnchorSnapshot.ActionLabel} replayEvents={session.AnchorSnapshot.ReplayEventCount} source=cached");
                return;
            }

            if (ShouldAbortStaleSyntheticChoiceSession(session, "before_final_custom_commit"))
                return;

            if (await TryCommitCustomChoiceBranchAsync(session, selectedKey))
            {
                WriteInteractionLog("branch_commit_after_reselect", $"choice={selectedKey} label={session.AnchorSnapshot.ActionLabel} replayEvents={session.AnchorSnapshot.ReplayEventCount} source=custom");
                return;
            }

            UndoDebugLog.Write($"primary_choice_branch_miss:{selectedKey} replayEvents={session.AnchorSnapshot.ReplayEventCount} label={session.AnchorSnapshot.ActionLabel} cached={session.CachedBranches.Count}");
            if (!TryCreateSyntheticChoiceBranchSnapshot(session, selectedKey, out UndoSnapshot? synthesizedBranch))
            {
                MainFile.Logger.Warn($"Could not synthesize primary branch for restored choice {selectedKey}.");
                if (IsOfficialFromHandDiscardChoice(session.ChoiceSpec))
                    DiscardDeferredActionSnapshots("primary_choice_branch_synthesis_failed");
                if (ReferenceEquals(_syntheticChoiceSession, session))
                    _syntheticChoiceSession = null;
                NotifyStateChanged();
                return;
            }

            SyntheticChoiceVfxRequest? synthesizedVfxRequest = CaptureSyntheticChoiceVfxRequest(session, synthesizedBranch!, selectedKey);
            if (!ShouldAbortStaleSyntheticChoiceSession(session, "before_synthesized_commit")
                && await TryApplySynthesizedChoiceBranchAsync(session, synthesizedBranch!, selectedKey, synthesizedVfxRequest))
            {
                WriteInteractionLog("branch_commit_after_reselect", $"choice={selectedKey} label={session.AnchorSnapshot.ActionLabel} replayEvents={session.AnchorSnapshot.ReplayEventCount} source=synthesized");
            }
        }
        catch (TaskCanceledException)
        {
            if (_syntheticChoiceSession == null || _syntheticChoiceSession == session)
            {
                if (IsOfficialFromHandDiscardChoice(session.ChoiceSpec))
                    DiscardDeferredActionSnapshots("primary_choice_selection_canceled");
                if (ReferenceEquals(_syntheticChoiceSession, session))
                    _syntheticChoiceSession = null;
                UndoDebugLog.Write($"primary_choice_restore_selected_key:canceled replayEvents={session.AnchorSnapshot.ReplayEventCount} label={session.AnchorSnapshot.ActionLabel}");
                NotifyStateChanged();
            }
        }
    }

    private bool ShouldAbortStaleSyntheticChoiceSession(UndoSyntheticChoiceSession session, string stage)
    {
        if (ReferenceEquals(_syntheticChoiceSession, session))
            return false;

        UndoDebugLog.Write(
            $"primary_choice_session_stale stage={stage}"
            + $" anchorReplayEvents={session.AnchorSnapshot.ReplayEventCount}"
            + $" currentReplayEvents={_syntheticChoiceSession?.AnchorSnapshot.ReplayEventCount.ToString() ?? "null"}"
            + $" currentLabel={_syntheticChoiceSession?.AnchorSnapshot.ActionLabel ?? "null"}");
        return true;
    }

    private async Task<bool> TryCommitLiveChoiceBranchAsync(UndoSyntheticChoiceSession session, UndoChoiceResultKey selectedKey)
    {
        if (_combatReplay == null || !CombatManager.Instance.IsInProgress)
            return false;

        if (ShouldAbortStaleSyntheticChoiceSession(session, "live_commit_start"))
            return false;

        bool isTrackedOfficialHandDiscard = IsTrackedOfficialFromHandDiscardChoice(
            session.AnchorSnapshot.CombatState.ActionKernelState.PausedChoiceState,
            session.ChoiceSpec);
        if (isTrackedOfficialHandDiscard)
        {
            if (!CanResumeTrackedOfficialHandDiscardLive(session, out string? reason))
            {
                UndoDebugLog.Write(
                    $"official_hand_choice_live_unavailable source={session.ChoiceSpec.SourceModelTypeName ?? "unknown"}"
                    + $" reason={reason ?? "missing_live_choice_state"}"
                    + $" replayEvents={session.AnchorSnapshot.ReplayEventCount}->{GetCurrentReplayEventCount()}");
                return false;
            }

            if (!await WaitForOfficialHandDiscardLiveResumeAsync(session))
                return false;
        }
        else
        {
            await WaitForReplayBranchAdvanceAsync(session.AnchorSnapshot.ReplayEventCount);
            await WaitForReplayToSettleAsync();
            if (RunManager.Instance.ActionExecutor.IsPaused
                && RunManager.Instance.ActionExecutor.CurrentlyRunningAction == null
                && RunManager.Instance.ActionQueueSet.IsEmpty
                && !IsChoiceUiReady())
            {
                RunManager.Instance.ActionExecutor.Unpause();
                await WaitOneFrameAsync();
            }
        }

        if (ShouldAbortStaleSyntheticChoiceSession(session, "live_commit_after_wait"))
            return false;

        int replayEventCount = GetCurrentReplayEventCount();
        if (replayEventCount <= session.AnchorSnapshot.ReplayEventCount)
            return false;

        if (!DidLiveBranchResumePastChoice(session, replayEventCount))
            return false;

        if (ShouldAbortStaleSyntheticChoiceSession(session, "live_commit_before_capture"))
            return false;

        UndoSnapshot branchSnapshot = new(
            CaptureCurrentCombatFullState(),
            replayEventCount,
            session.TemplateSnapshot?.ActionKind ?? session.AnchorSnapshot.ActionKind,
            _nextSequenceId++,
            session.TemplateSnapshot?.ActionLabel ?? session.AnchorSnapshot.ActionLabel,
            choiceResultKey: selectedKey);

        if (ReferenceEquals(_syntheticChoiceSession, session))
            _syntheticChoiceSession = null;
        session.RememberBranch(selectedKey, branchSnapshot);
        _futureSnapshots.Clear();
        RewriteReplayChoiceBranch(session.AnchorSnapshot, branchSnapshot);
        _combatReplay.ActiveEventCount = branchSnapshot.ReplayEventCount;
        TruncateReplayChecksumsFrom(branchSnapshot.CombatState.NextChecksumId);
        DisableReplayChecksumComparison(branchSnapshot.CombatState.NextChecksumId);
        FlushDeferredActionSnapshots(branchSnapshot.ReplayEventCount);

        UndoChoiceSpec? continuationChoiceSpec = GetSnapshotChoiceSpec(branchSnapshot);
        bool rearmedNestedChoice = false;
        RememberResolvedChoiceBranch(
            session.ChoiceSpec,
            selectedKey,
            allowImmediateContinuation: branchSnapshot.ReplayEventCount > session.AnchorSnapshot.ReplayEventCount
                && IsSupportedChoiceAnchorKind(continuationChoiceSpec));
        if (branchSnapshot.ReplayEventCount > session.AnchorSnapshot.ReplayEventCount
            && IsSupportedChoiceAnchorKind(continuationChoiceSpec))
        {
            PreserveChoiceAnchorInPastHistory(
                session.AnchorSnapshot,
                WithChoiceBranchStates(session.AnchorSnapshot.CombatState, CaptureSavedChoiceBranchStates(session)));
            EnsurePlayerChoiceUndoAnchor(
                branchSnapshot,
                continuationChoiceSpec,
                forceRefresh: true,
                anchorCombatStateOverride: branchSnapshot.CombatState);
            PrioritizeChoiceAnchorHistoryNode(branchSnapshot.ActionLabel, continuationChoiceSpec, branchSnapshot.ReplayEventCount);
            rearmedNestedChoice = true;
        }
        else
        {
            if (_pastSnapshots.First?.Value is UndoSnapshot existing
                && existing.IsChoiceAnchor
                && existing.ReplayEventCount == session.AnchorSnapshot.ReplayEventCount)
            {
                _pastSnapshots.RemoveFirst();
            }

            UndoSnapshot rearmedAnchor = new(
                WithChoiceBranchStates(session.AnchorSnapshot.CombatState, CaptureSavedChoiceBranchStates(session)),
                session.AnchorSnapshot.ReplayEventCount,
                UndoActionKind.PlayerChoice,
                _nextSequenceId++,
                session.AnchorSnapshot.ActionLabel,
                isChoiceAnchor: true,
                choiceSpec: session.ChoiceSpec);

            _pastSnapshots.AddFirst(rearmedAnchor);
            TrimSnapshots(_pastSnapshots);
            PrioritizeChoiceAnchorHistoryNode(rearmedAnchor.ActionLabel, session.ChoiceSpec, rearmedAnchor.ReplayEventCount);
            MainFile.Logger.Info($"Re-armed player choice undo anchor. ReplayEvents={rearmedAnchor.ReplayEventCount}, UndoCount={_pastSnapshots.Count}, ChoiceKind={session.ChoiceSpec.Kind} forceRefresh=True");
        }

        if (isTrackedOfficialHandDiscard && CombatManager.Instance.DebugOnlyGetState() is CombatState liveCombatState)
            await RefreshCombatUiAfterHandDiscardChoiceAsync(liveCombatState, officialHandChoiceUiSettled: true);
        if (rearmedNestedChoice)
        {
            UndoDebugLog.Write(
                $"nested_choice_anchor_rearmed parentReplayEvents={session.AnchorSnapshot.ReplayEventCount}"
                + $" childReplayEvents={branchSnapshot.ReplayEventCount}"
                + $" childKind={continuationChoiceSpec?.Kind}");
        }
        NotifyStateChanged();
        return true;
    }

    private static bool CanResumeTrackedOfficialHandDiscardLive(PausedChoiceState? pausedChoiceState, UndoChoiceSpec choiceSpec, out string? reason)
    {
        reason = null;
        if (!IsTrackedOfficialFromHandDiscardChoice(pausedChoiceState, choiceSpec))
            return false;

        return UndoActionCodecRegistry.CanResumeRestoredChoiceSourceAction(pausedChoiceState, out reason);
    }

    private static bool CanResumeTrackedOfficialHandDiscardLive(UndoSyntheticChoiceSession session, out string? reason)
    {
        return CanResumeTrackedOfficialHandDiscardLive(
            session.AnchorSnapshot.CombatState.ActionKernelState.PausedChoiceState,
            session.ChoiceSpec,
            out reason);
    }

    private async Task<bool> WaitForOfficialHandDiscardLiveResumeAsync(UndoSyntheticChoiceSession session, int maxFrames = 180)
    {
        PausedChoiceState? pausedChoiceState = session.AnchorSnapshot.CombatState.ActionKernelState.PausedChoiceState;
        if (!IsTrackedOfficialFromHandDiscardChoice(pausedChoiceState, session.ChoiceSpec))
            return false;

        if (!CanResumeTrackedOfficialHandDiscardLive(session, out string? unavailableReason))
        {
            UndoDebugLog.Write(
                $"official_hand_choice_live_unavailable source={session.ChoiceSpec.SourceModelTypeName ?? "unknown"}"
                + $" reason={unavailableReason ?? "missing_live_choice_state"}"
                + $" replayEvents={session.AnchorSnapshot.ReplayEventCount}->{GetCurrentReplayEventCount()}");
            return false;
        }

        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        Player? player = combatState == null ? null : LocalContext.GetMe(combatState);
        string sourceName = session.ChoiceSpec.SourceModelTypeName ?? "unknown";
        for (int frame = 0; frame < maxFrames; frame++)
        {
            if (ShouldAbortStaleSyntheticChoiceSession(session, "live_resume_wait"))
                return false;

            if (!IsChoiceUiReady())
            {
                ActionSynchronizerCombatState targetSynchronizerState = combatState?.CurrentSide == CombatSide.Player
                    ? ActionSynchronizerCombatState.PlayPhase
                    : ActionSynchronizerCombatState.NotPlayPhase;
                RestoreActionSynchronizationState(targetSynchronizerState, ActionKernelBoundaryKind.StableBoundary, out _);
                RunManager.Instance.ActionQueueSet.UnpauseAllPlayerQueues();
                RunManager.Instance.ActionExecutor.Unpause();
            }

            int replayEventCount = GetCurrentReplayEventCount();
            if (replayEventCount > session.AnchorSnapshot.ReplayEventCount)
            {
                await WaitForReplayToSettleAsync();
                if (ShouldAbortStaleSyntheticChoiceSession(session, "live_resume_after_replay_settle"))
                    return false;

                replayEventCount = GetCurrentReplayEventCount();
                NPlayerHand? hand = NCombatRoom.Instance?.Ui?.Hand;
                bool officialHandChoiceUiSettled = hand == null || await WaitForOfficialHandChoiceUiSettleAsync(hand, player, maxFrames: 4);
                if (ShouldAbortStaleSyntheticChoiceSession(session, "live_resume_after_ui_settle"))
                    return false;

                if (DidLiveBranchResumePastChoice(session, replayEventCount) && officialHandChoiceUiSettled)
                {
                    if (hand != null)
                        TryCompletePendingHandChoiceUiInstantly(hand, player);

                    UndoDebugLog.Write(
                        $"official_hand_choice_live_resume source={sourceName}"
                        + $" replayEvents={session.AnchorSnapshot.ReplayEventCount}->{replayEventCount}"
                        + $" sourceActionId={pausedChoiceState!.SourceActionRef?.ActionId?.ToString() ?? "null"}"
                        + $" resumeActionId={pausedChoiceState.ResumeActionId?.ToString() ?? "null"}");
                    return true;
                }
            }

            await WaitOneFrameAsync();
        }

        UndoDebugLog.Write(
            $"official_hand_choice_resume_timeout source={sourceName}"
            + $" replayEvents={session.AnchorSnapshot.ReplayEventCount}->{GetCurrentReplayEventCount()}"
            + $" sourceActionPresent={IsTrackedActionPresent(pausedChoiceState!.SourceActionRef?.ActionId)}"
            + $" resumeActionPresent={IsTrackedActionPresent(pausedChoiceState.ResumeActionId)}"
            + $" resumePending={IsResumeActionPending(pausedChoiceState)}"
            + $" callbackObserved={_pendingHandChoiceUiSettle?.CallbackObserved.ToString() ?? "null"}"
            + $" handSelecting={(NCombatRoom.Instance?.Ui?.Hand.IsInCardSelection == true)}");
        return false;
    }

    private bool DidLiveBranchResumePastChoice(UndoSyntheticChoiceSession session, int replayEventCount)
    {
        if (_combatReplay == null)
            return false;

        PausedChoiceState? pausedChoiceState = session.AnchorSnapshot.CombatState.ActionKernelState.PausedChoiceState;
        if (!IsTrackedOfficialFromHandDiscardChoice(pausedChoiceState, session.ChoiceSpec))
        {
            return true;
        }

        bool replayAdvanced = replayEventCount > session.AnchorSnapshot.ReplayEventCount;
        bool resumeEventObserved = HasReplayResumeEvent(session, replayEventCount, pausedChoiceState.ResumeActionId);
        bool resumePending = IsResumeActionPending(pausedChoiceState);
        return replayAdvanced
            && (resumeEventObserved || pausedChoiceState.ResumeActionId == null)
            && !resumePending
            && !IsChoiceUiReady()
            && !IsTrackedActionExecuting(pausedChoiceState.SourceActionRef?.ActionId)
            && !IsTrackedActionExecuting(pausedChoiceState.ResumeActionId);
    }

    private async Task<bool> TryCommitCustomChoiceBranchAsync(UndoSyntheticChoiceSession session, UndoChoiceResultKey selectedKey)
    {
        if (!CombatManager.Instance.IsInProgress)
            return false;

        if (ShouldAbortStaleSyntheticChoiceSession(session, "custom_commit_start"))
            return false;

        if (await TryExecuteRetainChoiceAsync(session, selectedKey))
        {
            if (ShouldAbortStaleSyntheticChoiceSession(session, "custom_commit_after_retain"))
                return false;

            await FinalizeCustomChoiceBranchAsync(session, selectedKey, stabilizeQueues: false);
            return true;
        }

        if (await TryExecuteHandDiscardChoiceAsync(session, selectedKey))
        {
            try
            {
                if (ShouldAbortStaleSyntheticChoiceSession(session, "custom_commit_after_hand_discard"))
                    return false;

                await FinalizeCustomChoiceBranchAsync(session, selectedKey, stabilizeQueues: false);
                return true;
            }
            finally
            {
                ReleaseDetachedHandDiscardExecutionGuard("custom_commit_after_hand_discard");
            }
        }

        if (await TryExecuteHandExhaustChoiceAsync(session, selectedKey))
        {
            if (ShouldAbortStaleSyntheticChoiceSession(session, "custom_commit_after_hand_exhaust"))
                return false;

            await FinalizeCustomChoiceBranchAsync(session, selectedKey);
            return true;
        }

        if (await TryExecuteSimpleGridAddToHandChoiceAsync(session, selectedKey))
        {
            if (ShouldAbortStaleSyntheticChoiceSession(session, "custom_commit_after_grid_to_hand"))
                return false;

            await FinalizeCustomChoiceBranchAsync(session, selectedKey);
            return true;
        }

        if (await TryExecuteGeneratedGridToHandChoiceAsync(session, selectedKey))
        {
            if (ShouldAbortStaleSyntheticChoiceSession(session, "custom_commit_after_generated_grid"))
                return false;

            await FinalizeCustomChoiceBranchAsync(session, selectedKey);
            return true;
        }

        if (await TryExecuteSelectedCardMutationChoiceAsync(session, selectedKey))
        {
            if (ShouldAbortStaleSyntheticChoiceSession(session, "custom_commit_after_card_mutation"))
                return false;

            await FinalizeCustomChoiceBranchAsync(session, selectedKey);
            return true;
        }

        if (await TryExecuteDecisionsDecisionsChoiceAsync(session, selectedKey))
        {
            if (ShouldAbortStaleSyntheticChoiceSession(session, "custom_commit_after_decisions"))
                return false;

            await FinalizeCustomChoiceBranchAsync(session, selectedKey);
            return true;
        }

        if (await TryExecuteEntropyChoiceAsync(session, selectedKey))
        {
            if (ShouldAbortStaleSyntheticChoiceSession(session, "custom_commit_after_entropy"))
                return false;

            await FinalizeCustomChoiceBranchAsync(session, selectedKey);
            return true;
        }

        return false;
    }

    // 手牌弃牌类选择在重选时必须优先回到官方案例路径，
    // 否则 cached/synthesized branch 会跳过真实 discard side effect，
    // 导致 Sly、AfterCardDiscarded 等效果在切换目标后漂移。
    private static bool ShouldPreferCustomChoiceBeforeCached(UndoSyntheticChoiceSession session)
    {
        return session.RequiresAuthoritativeBranchExecution;
    }

    private static bool ShouldRequireAuthoritativeSyntheticChoiceExecution(UndoChoiceSpec choiceSpec)
    {
        return IsRetainChoiceSource(choiceSpec)
            || IsOfficialFromHandDiscardChoice(choiceSpec)
            || IsHandExhaustChoiceSource(choiceSpec)
            || IsSelectedCardMutationSource(choiceSpec)
            || IsSimpleGridAddToHandChoiceSource(choiceSpec)
            || IsGeneratedGridToHandChoiceSource(choiceSpec)
            || IsSourceChoice(choiceSpec, "MegaCrit.Sts2.Core.Models.Cards.DecisionsDecisions")
            || IsSourceChoice(choiceSpec, typeof(EntropyPower));
    }

    private async Task FinalizeCustomChoiceBranchAsync(UndoSyntheticChoiceSession session, UndoChoiceResultKey selectedKey, bool stabilizeQueues = true)
    {
        if (ShouldAbortStaleSyntheticChoiceSession(session, "custom_finalize_start"))
            return;

        bool isOfficialHandDiscardChoice = IsOfficialFromHandDiscardChoice(session.ChoiceSpec);
        bool officialHandChoiceUiSettled = true;
        if (stabilizeQueues)
        {
            await StabilizeAfterCustomChoiceExecutionAsync(session);
        }
        else if (isOfficialHandDiscardChoice)
        {
            CombatState? liveCombatState = CombatManager.Instance.DebugOnlyGetState();
            Player? livePlayer = liveCombatState == null ? null : LocalContext.GetMe(liveCombatState);
            NPlayerHand? hand = NCombatRoom.Instance?.Ui?.Hand;
            officialHandChoiceUiSettled = await WaitForOfficialHandChoiceUiSettleAsync(hand, livePlayer);
            if (!officialHandChoiceUiSettled && hand != null && TryCompletePendingHandDiscardChoiceUiViaOfficialPath(hand))
            {
                await WaitOneFrameAsync();
                hand = NCombatRoom.Instance?.Ui?.Hand ?? hand;
                officialHandChoiceUiSettled = await WaitForOfficialHandChoiceUiSettleAsync(hand, livePlayer, maxFrames: 60);
            }

            if (!officialHandChoiceUiSettled && liveCombatState != null)
            {
                RecoverHandDiscardChoiceUiIfNeeded(liveCombatState);
                hand = NCombatRoom.Instance?.Ui?.Hand ?? hand;
                officialHandChoiceUiSettled = await WaitForOfficialHandChoiceUiSettleAsync(hand, livePlayer, maxFrames: 30);
            }

            if (!officialHandChoiceUiSettled && hand != null)
            {
                TryCompletePendingHandChoiceUiInstantly(hand, livePlayer);
                await WaitOneFrameAsync();
                hand = NCombatRoom.Instance?.Ui?.Hand ?? hand;
                officialHandChoiceUiSettled = await WaitForOfficialHandChoiceUiSettleAsync(hand, livePlayer, maxFrames: 15);
            }

            if (!officialHandChoiceUiSettled && hand != null && livePlayer != null)
            {
                bool forcedSynchronized = TrySyncExistingHandUi(hand, livePlayer, normalizeLayout: true);
                if (forcedSynchronized)
                {
                    string sourceName = _pendingHandChoiceUiSettle?.Source.GetType().Name
                        ?? _pendingHandChoiceSource?.GetType().Name
                        ?? session.ChoiceSpec.SourceModelTypeName
                        ?? "unknown";
                    UndoDebugLog.Write(
                        $"official_hand_choice_ui_forced_settle source={sourceName}"
                        + $" expectedHand={PileType.Hand.GetPile(livePlayer).Cards.Count}"
                        + $" holders={hand.CardHolderContainer.GetChildCount()}");
                    ClearPendingHandChoiceSourceTracking();
                    officialHandChoiceUiSettled = true;
                }
            }
        }
        else
        {
            // 生存者这类“官方 discard + Sly 自动打出”已经在 live 状态里完整执行，
            // 这里只等飞牌/打出动画收尾，避免过早 reset 队列把官方 VFX 截断。
            await WaitForTransientCardFlyVfxToSettleAsync();
            await WaitOneFrameAsync();
        }

        if (ShouldAbortStaleSyntheticChoiceSession(session, "custom_finalize_after_waits"))
            return;

        int branchReplayEventCount = isOfficialHandDiscardChoice
            ? Math.Max(GetCurrentReplayEventCount(), session.AnchorSnapshot.ReplayEventCount)
            : session.TemplateSnapshot?.ReplayEventCount ?? session.AnchorSnapshot.ReplayEventCount;
        UndoSnapshot branchSnapshot = new(
            CaptureCurrentCombatFullState(),
            branchReplayEventCount,
            session.TemplateSnapshot?.ActionKind ?? session.AnchorSnapshot.ActionKind,
            _nextSequenceId++,
            session.TemplateSnapshot?.ActionLabel ?? session.AnchorSnapshot.ActionLabel,
            choiceResultKey: selectedKey);

        _syntheticChoiceSession = null;
        session.RememberBranch(selectedKey, branchSnapshot);
        _futureSnapshots.Clear();
        RewriteReplayChoiceBranch(session.AnchorSnapshot, branchSnapshot);
        _combatReplay!.ActiveEventCount = branchSnapshot.ReplayEventCount;
        TruncateReplayChecksumsFrom(branchSnapshot.CombatState.NextChecksumId);
        DisableReplayChecksumComparison(branchSnapshot.CombatState.NextChecksumId);
        FlushDeferredActionSnapshots(branchSnapshot.ReplayEventCount);

        // 手牌弃牌类 custom choice 会直接复用当前 live 模型状态，而不会经过一次完整的 full-state UI 重建。
        // 如果这里不主动把 UI 对齐到最新模型，屏幕中央的当前打牌节点、左侧预览和旧 selected-holder
        // 可能继续残留到下一次 undo，随后在清理时把已经回收到池里的 NCard 再 free 一次。
        if (ShouldRefreshCombatUiAfterCustomChoice(session))
        {
            CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
            if (combatState != null)
            {
                if (isOfficialHandDiscardChoice)
                    await RefreshCombatUiAfterHandDiscardChoiceAsync(combatState, officialHandChoiceUiSettled);
                else if (ShouldUseLightweightCustomChoiceUiRefresh(session.ChoiceSpec))
                    await RefreshCombatUiAfterDetachedPlayedCardChoiceAsync(combatState);
                else
                    await RefreshCombatUiAsync(combatState);
            }
        }

        UndoChoiceSpec? continuationChoiceSpec = GetSnapshotChoiceSpec(branchSnapshot);
        RememberResolvedChoiceBranch(
            session.ChoiceSpec,
            selectedKey,
            allowImmediateContinuation: branchSnapshot.ReplayEventCount > session.AnchorSnapshot.ReplayEventCount
                && IsSupportedChoiceAnchorKind(continuationChoiceSpec));
        if (branchSnapshot.ReplayEventCount > session.AnchorSnapshot.ReplayEventCount
            && IsSupportedChoiceAnchorKind(continuationChoiceSpec))
        {
            PreserveChoiceAnchorInPastHistory(
                session.AnchorSnapshot,
                WithChoiceBranchStates(session.AnchorSnapshot.CombatState, CaptureSavedChoiceBranchStates(session)));
            EnsurePlayerChoiceUndoAnchor(
                branchSnapshot,
                continuationChoiceSpec,
                forceRefresh: true,
                anchorCombatStateOverride: branchSnapshot.CombatState);
            PrioritizeChoiceAnchorHistoryNode(branchSnapshot.ActionLabel, continuationChoiceSpec, branchSnapshot.ReplayEventCount);
            UndoDebugLog.Write(
                $"nested_choice_anchor_rearmed parentReplayEvents={session.AnchorSnapshot.ReplayEventCount}"
                + $" childReplayEvents={branchSnapshot.ReplayEventCount}"
                + $" childKind={continuationChoiceSpec?.Kind}");
        }
        else
        {
            EnsurePlayerChoiceUndoAnchor(
                session.AnchorSnapshot,
                session.ChoiceSpec,
                forceRefresh: true,
                anchorCombatStateOverride: WithChoiceBranchStates(session.AnchorSnapshot.CombatState, CaptureSavedChoiceBranchStates(session)));
            PrioritizeChoiceAnchorHistoryNode(session.AnchorSnapshot.ActionLabel, session.ChoiceSpec, session.AnchorSnapshot.ReplayEventCount);
        }
        NotifyStateChanged();
        await WaitOneFrameAsync();
    }

    private static bool ShouldRefreshCombatUiAfterCustomChoice(UndoSyntheticChoiceSession session)
    {
        return true;
    }

    private static bool ShouldUseLightweightCustomChoiceUiRefresh(UndoChoiceSpec choiceSpec)
    {
        return choiceSpec.SourceCombatCard != null
            || IsHandExhaustChoiceSource(choiceSpec)
            || IsSimpleGridAddToHandChoiceSource(choiceSpec)
            || IsGeneratedGridToHandChoiceSource(choiceSpec)
            || IsSelectedCardMutationSource(choiceSpec)
            || IsSourceChoice(choiceSpec, "MegaCrit.Sts2.Core.Models.Cards.DecisionsDecisions");
    }

    private static bool ShouldPreferLiveBranchCommit(PausedChoiceState? pausedChoiceState, UndoChoiceSpec choiceSpec, bool stateAlreadyApplied)
    {
        if (pausedChoiceState == null)
            return false;

        if (IsTrackedOfficialFromHandDiscardChoice(pausedChoiceState, choiceSpec))
            return true;

        if (IsOfficialFromHandDiscardChoice(choiceSpec))
            return false;

        if (UndoActionCodecRegistry.CanResumeRestoredChoiceSourceAction(pausedChoiceState, out _))
            return true;

        if (stateAlreadyApplied)
            return true;

        return false;
    }

    private async Task WaitForTransientCardFlyVfxToSettleAsync(int maxFrames = 60)
    {
        for (int frame = 0; frame < maxFrames; frame++)
        {
            if (!HasTransientCardFlyVfx(NCombatRoom.Instance?.CombatVfxContainer)
                && !HasTransientCardFlyVfx(NRun.Instance?.GlobalUi?.TopBar?.TrailContainer))
            {
                break;
            }

            await WaitOneFrameAsync();
        }
    }

    private static bool HasTransientCardFlyVfx(Node? root)
    {
        if (root == null || !GodotObject.IsInstanceValid(root))
            return false;

        foreach (Node child in root.GetChildren().Cast<Node>())
        {
            if (child is NCardFlyVfx or NCard or NCardTrailVfx)
                return true;

            if (HasTransientCardFlyVfx(child))
                return true;
        }

        return false;
    }

    private async Task StabilizeAfterCustomChoiceExecutionAsync(UndoSyntheticChoiceSession session)
    {
        await WaitForTransientCardFlyVfxToSettleAsync();
        await WaitOneFrameAsync();

        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null || !CombatManager.Instance.IsInProgress)
            return;

        await DismissSupportedChoiceUiIfPresentAsync(session);
        ResetActionExecutorForRestore();
        RunManager.Instance.ActionQueueSet.Reset();
        ResetActionSynchronizerForRestore();
        RebuildActionQueues(runState.Players);

        UndoCombatFullState targetSnapshotState = session.TemplateSnapshot?.CombatState ?? session.AnchorSnapshot.CombatState;
        ActionSynchronizerCombatState targetSynchronizerState = GetEffectiveSynchronizerState(targetSnapshotState);
        RestoreActionSynchronizationState(targetSynchronizerState, ActionKernelBoundaryKind.StableBoundary, out _);
        await WaitOneFrameAsync();
    }

    private async Task StabilizeAfterFlowAdvancingChoiceExecutionAsync()
    {
        await WaitOneFrameAsync();

        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        if (runState == null || combatState == null || !CombatManager.Instance.IsInProgress)
            return;

        await DismissSupportedChoiceUiIfPresentAsync();
        ResetActionExecutorForRestore();
        RunManager.Instance.ActionQueueSet.Reset();
        ResetActionSynchronizerForRestore();
        RebuildActionQueues(runState.Players);

        ActionSynchronizerCombatState targetSynchronizerState = combatState.CurrentSide == CombatSide.Player
            && CombatManager.Instance.IsPlayPhase
                ? ActionSynchronizerCombatState.PlayPhase
                : ActionSynchronizerCombatState.NotPlayPhase;
        RestoreActionSynchronizationState(targetSynchronizerState, ActionKernelBoundaryKind.StableBoundary, out _);
        await WaitOneFrameAsync();
    }

    private static bool IsSourceChoice(UndoChoiceSpec choiceSpec, Type sourceType)
    {
        return string.Equals(choiceSpec.SourceModelTypeName, sourceType.FullName, StringComparison.Ordinal);
    }

    private static bool IsSourceChoice(UndoChoiceSpec choiceSpec, string sourceTypeName)
    {
        return string.Equals(choiceSpec.SourceModelTypeName, sourceTypeName, StringComparison.Ordinal);
    }

    private static bool IsRetainChoiceSource(UndoChoiceSpec choiceSpec)
    {
        return IsSourceChoice(choiceSpec, typeof(WellLaidPlansPower));
    }

    private static bool IsSelectedCardMutationSource(UndoChoiceSpec choiceSpec)
    {
        return IsSourceChoice(choiceSpec, typeof(Nightmare))
            || IsSourceChoice(choiceSpec, typeof(HandTrick))
            || IsSourceChoice(choiceSpec, typeof(Snap))
            || IsSourceChoice(choiceSpec, typeof(SculptingStrike))
            || IsSourceChoice(choiceSpec, typeof(Transfigure))
            || IsSourceChoice(choiceSpec, typeof(TouchOfInsanity));
    }

    private static bool IsHandExhaustChoiceSource(UndoChoiceSpec choiceSpec)
    {
        return IsSourceChoice(choiceSpec, typeof(BurningPact))
            || IsSourceChoice(choiceSpec, typeof(Brand))
            || IsSourceChoice(choiceSpec, typeof(Purity))
            || IsSourceChoice(choiceSpec, typeof(Scavenge))
            || IsSourceChoice(choiceSpec, typeof(TrueGrit))
            || IsSourceChoice(choiceSpec, typeof(TyrannyPower))
            || IsSourceChoice(choiceSpec, typeof(Ashwater));
    }

    private static bool IsSimpleGridAddToHandChoiceSource(UndoChoiceSpec choiceSpec)
    {
        return IsSourceChoice(choiceSpec, typeof(StratagemPower))
            || IsSourceChoice(choiceSpec, typeof(ForegoneConclusionPower))
            || IsSourceChoice(choiceSpec, typeof(DropletOfPrecognition))
            || IsSourceChoice(choiceSpec, typeof(LiquidMemories))
            || IsSourceChoice(choiceSpec, typeof(Dredge));
    }

    private static bool IsGeneratedGridToHandChoiceSource(UndoChoiceSpec choiceSpec)
    {
        return IsSourceChoice(choiceSpec, typeof(ChoicesParadox));
    }

    private static void DisableReplayChecksumComparison(uint nextChecksumId)
    {
        ChecksumTracker checksumTracker = RunManager.Instance.ChecksumTracker;
        checksumTracker.LoadReplayChecksums([], nextChecksumId);
        SetPrivateFieldValue(checksumTracker, "_replayChecksums", null);
    }

    private void TruncateReplayChecksumsFrom(uint nextChecksumId)
    {
        if (_combatReplay == null)
            return;

        _combatReplay.ChecksumData.RemoveAll(checksum => checksum.checksumData.id >= nextChecksumId);
    }

    private List<ReplayChecksumData> GetReplayChecksumsFrom(uint nextChecksumId)
    {
        if (_combatReplay == null)
            return [];

        return [.. _combatReplay.ChecksumData
            .Where(checksum => checksum.checksumData.id >= nextChecksumId)];
    }

    private async Task<bool> TryExecuteRetainChoiceAsync(UndoSyntheticChoiceSession session, UndoChoiceResultKey selectedKey)
    {
        UndoChoiceSpec choiceSpec = session.ChoiceSpec;
        if (choiceSpec.Kind != UndoChoiceKind.HandSelection
            || choiceSpec.SourcePileType != PileType.Hand
            || !IsRetainChoiceSource(choiceSpec))
        {
            return false;
        }

        DisableReplayChecksumComparison(session.AnchorSnapshot.CombatState.NextChecksumId);

        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        Player? player = combatState == null ? null : LocalContext.GetMe(combatState);
        if (combatState == null || player == null || combatState.CurrentSide != CombatSide.Player)
            return false;

        IReadOnlyList<CardModel> handCards = PileType.Hand.GetPile(player).Cards;
        foreach (int optionIndex in selectedKey.OptionIndexes)
        {
            if (optionIndex < 0 || optionIndex >= choiceSpec.SourcePileOptionIndexes.Count)
                return false;

            int handIndex = choiceSpec.SourcePileOptionIndexes[optionIndex];
            if (handIndex < 0 || handIndex >= handCards.Count)
                return false;

            handCards[handIndex].GiveSingleTurnRetain();
        }

        RunManager.Instance.ActionQueueSynchronizer.SetCombatState(ActionSynchronizerCombatState.NotPlayPhase);
        await CombatManager.Instance.EndPlayerTurnPhaseTwoInternal();
        await CombatManager.Instance.SwitchFromPlayerToEnemySide();
        await StabilizeAfterFlowAdvancingChoiceExecutionAsync();
        return true;
    }

    private async Task<bool> TryExecuteHandDiscardChoiceAsync(UndoSyntheticChoiceSession session, UndoChoiceResultKey selectedKey)
    {
        UndoChoiceSpec choiceSpec = session.ChoiceSpec;
        if (choiceSpec.Kind != UndoChoiceKind.HandSelection
            || choiceSpec.SourcePileType != PileType.Hand
            || !IsOfficialFromHandDiscardChoice(choiceSpec))
        {
            return false;
        }

        if (!TryDetachOfficialHandDiscardSourceAction(session))
        {
            UndoDebugLog.Write(
                $"official_hand_choice_detached_fallback_failed source={choiceSpec.SourceModelTypeName ?? "unknown"}"
                + $" sourceActionId={session.AnchorSnapshot.CombatState.ActionKernelState.PausedChoiceState?.SourceActionRef?.ActionId?.ToString() ?? "null"}"
                + $" resumeActionId={session.AnchorSnapshot.CombatState.ActionKernelState.PausedChoiceState?.ResumeActionId?.ToString() ?? "null"}");
            return false;
        }

        bool completed = false;
        try
        {
            DisableReplayChecksumComparison(session.AnchorSnapshot.CombatState.NextChecksumId);

            CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
            Player? player = combatState == null ? null : LocalContext.GetMe(combatState);
            if (player == null)
                return false;

            if (!TryResolveSelectedHandCards(choiceSpec, selectedKey, player, out List<CardModel> selectedCards))
                return false;

            PausedChoiceState? pausedChoiceState = session.AnchorSnapshot.CombatState.ActionKernelState.PausedChoiceState;
            bool shouldFinalizeDetachedPlayedCard = ShouldFinalizeDetachedPlayedCardChoice(pausedChoiceState, choiceSpec);
            BlockingPlayerChoiceContext choiceContext = new();
            CardModel? detachedSourceCard = BeginDetachedPlayedCardChoiceContext(player, choiceSpec, pausedChoiceState, choiceContext);
            try
            {
                if (IsSourceChoice(choiceSpec, typeof(GamblingChip))
                    || IsSourceChoice(choiceSpec, typeof(GamblersBrew)))
                {
                    await CardCmd.DiscardAndDraw(choiceContext, selectedCards, selectedCards.Count);
                }
                else
                {
                    await CardCmd.Discard(choiceContext, selectedCards);
                }

                if (IsSourceChoice(choiceSpec, typeof(HiddenDaggers)))
                {
                    CombatState? shivCombatState = detachedSourceCard?.CombatState ?? player.Creature.CombatState;
                    if (shivCombatState == null)
                        return false;

                    int shivCount = detachedSourceCard?.DynamicVars["Shivs"].IntValue ?? 0;
                    IEnumerable<CardModel> shivs = await Shiv.CreateInHand(player, shivCount, shivCombatState);
                    if (detachedSourceCard?.IsUpgraded == true)
                    {
                        foreach (CardModel shiv in shivs)
                            CardCmd.Upgrade(shiv, CardPreviewStyle.HorizontalLayout);
                    }
                }

                if (shouldFinalizeDetachedPlayedCard)
                    await FinalizeDetachedPlayedCardAsync(player, choiceContext, choiceSpec, detachedSourceCard, sourceActionDetached: true);
            }
            finally
            {
                EndDetachedPlayedCardChoiceContext(choiceContext, detachedSourceCard);
            }

            completed = true;
            return true;
        }
        finally
        {
            if (!completed)
                ReleaseDetachedHandDiscardExecutionGuard("hand_discard_execute_failed");
        }
    }

    private async Task<bool> TryExecuteSelectedCardMutationChoiceAsync(UndoSyntheticChoiceSession session, UndoChoiceResultKey selectedKey)
    {
        UndoChoiceSpec choiceSpec = session.ChoiceSpec;
        if (choiceSpec.Kind != UndoChoiceKind.HandSelection
            || choiceSpec.SourcePileType != PileType.Hand
            || selectedKey.OptionIndexes.Count != 1
            || !IsSelectedCardMutationSource(choiceSpec))
        {
            return false;
        }

        DisableReplayChecksumComparison(session.AnchorSnapshot.CombatState.NextChecksumId);

        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        Player? player = combatState == null ? null : LocalContext.GetMe(combatState);
        if (player == null)
            return false;

        if (!TryResolveSelectedHandCard(choiceSpec, selectedKey, player, out CardModel? selectedCard) || selectedCard == null)
            return false;

        PausedChoiceState? pausedChoiceState = session.AnchorSnapshot.CombatState.ActionKernelState.PausedChoiceState;
        bool shouldFinalizeDetachedPlayedCard = ShouldFinalizeDetachedPlayedCardChoice(pausedChoiceState, choiceSpec);
        BlockingPlayerChoiceContext choiceContext = new();
        CardModel? sourceCard = BeginDetachedPlayedCardChoiceContext(player, choiceSpec, pausedChoiceState, choiceContext);
        try
        {
            if (IsSourceChoice(choiceSpec, typeof(Nightmare)))
            {
                NightmarePower nightmarePower = await PowerCmd.Apply<NightmarePower>(player.Creature, 3m, player.Creature, sourceCard, false);
                nightmarePower.SetSelectedCard(selectedCard);
            }
            else if (IsSourceChoice(choiceSpec, typeof(HandTrick)))
            {
                CardCmd.ApplySingleTurnSly(selectedCard);
            }
            else if (IsSourceChoice(choiceSpec, typeof(Snap)))
            {
                CardCmd.ApplyKeyword(selectedCard, CardKeyword.Retain);
            }
            else if (IsSourceChoice(choiceSpec, typeof(SculptingStrike)))
            {
                CardCmd.ApplyKeyword(selectedCard, CardKeyword.Ethereal);
            }
            else if (IsSourceChoice(choiceSpec, typeof(Transfigure)))
            {
                if (!selectedCard.EnergyCost.CostsX && selectedCard.EnergyCost.GetWithModifiers(CostModifiers.None) >= 0)
                    selectedCard.EnergyCost.AddThisCombat(1, false);

                selectedCard.BaseReplayCount++;
            }
            else if (IsSourceChoice(choiceSpec, typeof(TouchOfInsanity)))
            {
                selectedCard.SetToFreeThisCombat();
            }
            else
            {
                return false;
            }

            if (shouldFinalizeDetachedPlayedCard)
                await FinalizeDetachedPlayedCardAsync(player, choiceContext, choiceSpec, sourceCard);
        }
        finally
        {
            EndDetachedPlayedCardChoiceContext(choiceContext, sourceCard);
        }

        return true;
    }

    private static bool TryResolveSelectedHandCard(UndoChoiceSpec choiceSpec, UndoChoiceResultKey selectedKey, Player player, out CardModel? selectedCard)
    {
        selectedCard = null;
        if (selectedKey.OptionIndexes.Count != 1)
            return false;

        int optionIndex = selectedKey.OptionIndexes[0];
        if (optionIndex < 0 || optionIndex >= choiceSpec.SourcePileOptionIndexes.Count)
            return false;

        IReadOnlyList<CardModel> handCards = PileType.Hand.GetPile(player).Cards;
        int handIndex = choiceSpec.SourcePileOptionIndexes[optionIndex];
        if (handIndex < 0 || handIndex >= handCards.Count)
            return false;

        selectedCard = handCards[handIndex];
        return true;
    }

    private static CardModel? ResolveSourceCardFromPlayPile(Player player, UndoChoiceSpec choiceSpec)
    {
        IReadOnlyList<CardModel> playCards = PileType.Play.GetPile(player).Cards;
        if (choiceSpec.SourceCombatCard != null)
        {
            CardModel? matchedCard = playCards.FirstOrDefault(card => Equals(NetCombatCard.FromModel(card), choiceSpec.SourceCombatCard));
            if (matchedCard != null)
                return matchedCard;
        }

        return playCards.LastOrDefault();
    }

    private static CardModel? BeginDetachedPlayedCardChoiceContext(
        Player player,
        UndoChoiceSpec choiceSpec,
        PausedChoiceState? pausedChoiceState,
        PlayerChoiceContext choiceContext)
    {
        CardModel? sourceCard = ResolveDetachedPlayedCardChoiceSource(player, choiceSpec, pausedChoiceState);
        if (sourceCard != null)
            choiceContext.PushModel(sourceCard);

        return sourceCard;
    }

    private static CardModel? ResolveDetachedPlayedCardChoiceSource(
        Player player,
        UndoChoiceSpec choiceSpec,
        PausedChoiceState? pausedChoiceState)
    {
        IReadOnlyList<CardModel> playCards = PileType.Play.GetPile(player).Cards;
        if (playCards.Count == 0)
            return null;

        if (choiceSpec.SourceCombatCard != null)
            return ResolveSourceCardFromPlayPile(player, choiceSpec);

        if (!ShouldFinalizeDetachedPlayedCardChoice(pausedChoiceState, choiceSpec))
            return null;

        CardModel sourceCard = playCards[^1];
        UndoDebugLog.Write(
            $"detached_choice_source_resolved source={choiceSpec.SourceModelTypeName ?? "unknown"}"
            + $" sourceActionType={pausedChoiceState?.SourceActionRef?.TypeName ?? "null"}"
            + $" card={sourceCard.Id.Entry}");
        return sourceCard;
    }

    private static bool ShouldFinalizeDetachedPlayedCardChoice(PausedChoiceState? pausedChoiceState, UndoChoiceSpec choiceSpec)
    {
        if (choiceSpec.SourceCombatCard != null)
            return true;

        if (pausedChoiceState == null)
            return false;

        if (string.Equals(pausedChoiceState.SourceActionRef?.TypeName, typeof(PlayCardAction).FullName, StringComparison.Ordinal))
            return true;

        return FindTrackedAction(pausedChoiceState.SourceActionRef?.ActionId) is PlayCardAction
            || FindTrackedAction(pausedChoiceState.ResumeActionId) is PlayCardAction;
    }

    private static void EndDetachedPlayedCardChoiceContext(PlayerChoiceContext choiceContext, CardModel? sourceCard)
    {
        if (sourceCard != null && ReferenceEquals(choiceContext.LastInvolvedModel, sourceCard))
            choiceContext.PopModel(sourceCard);
    }

    private static async Task FinalizeDetachedPlayedCardAsync(
        Player player,
        PlayerChoiceContext choiceContext,
        UndoChoiceSpec choiceSpec,
        CardModel? playedCard = null,
        bool sourceActionDetached = true)
    {
        if (!sourceActionDetached)
            return;

        playedCard ??= ResolveSourceCardFromPlayPile(player, choiceSpec);
        if (playedCard == null)
            return;

        CombatState? combatState = playedCard.CombatState ?? player.Creature.CombatState;
        CardPlay? pendingCardPlay = combatState == null ? null : TryResolveDetachedPendingCardPlay(combatState, playedCard);

        playedCard.InvokeExecutionFinished();
        if (combatState != null && pendingCardPlay != null)
        {
            CombatManager.Instance.History.CardPlayFinished(combatState, pendingCardPlay);
            if (CombatManager.Instance.IsInProgress)
                await Hook.AfterCardPlayed(combatState, choiceContext, pendingCardPlay);
        }

        await playedCard.MoveToResultPileWithoutPlaying(choiceContext);
        await CombatManager.Instance.CheckForEmptyHand(choiceContext, player);
        CleanupDetachedPlayedCardTail(playedCard);
        EndDetachedPlayedCardChoiceContext(choiceContext, playedCard);
    }

    private static CardPlay? TryResolveDetachedPendingCardPlay(CombatState combatState, CardModel playedCard)
    {
        List<CardPlay> startedCardPlays =
        [
            .. CombatManager.Instance.History.CardPlaysStarted
                .Where(entry => entry.HappenedThisTurn(combatState) && ReferenceEquals(entry.CardPlay.Card, playedCard))
                .Select(entry => entry.CardPlay)
        ];
        if (startedCardPlays.Count == 0)
            return null;

        int finishedCardPlayCount = CombatManager.Instance.History.CardPlaysFinished.Count(entry =>
            entry.HappenedThisTurn(combatState)
            && ReferenceEquals(entry.CardPlay.Card, playedCard));
        if (finishedCardPlayCount < startedCardPlays.Count)
            return startedCardPlays[finishedCardPlayCount];

        return startedCardPlays[^1];
    }

    private static void CleanupDetachedPlayedCardTail(CardModel playedCard)
    {
        if (playedCard.EnergyCost.AfterCardPlayedCleanup())
            playedCard.InvokeEnergyCostChanged();

        List<TemporaryCardCost>? temporaryStarCosts = FindField(typeof(CardModel), "_temporaryStarCosts")?.GetValue(playedCard) as List<TemporaryCardCost>;
        if (temporaryStarCosts != null && temporaryStarCosts.RemoveAll(static cost => cost.ClearsWhenCardIsPlayed) > 0)
            (FindField(typeof(CardModel), "StarCostChanged")?.GetValue(playedCard) as Action)?.Invoke();

        SetPrivatePropertyValue(playedCard, "CurrentTarget", null);
        (FindField(typeof(CardModel), "Played")?.GetValue(playedCard) as Action)?.Invoke();
    }

    private async Task<bool> TryExecuteDecisionsDecisionsChoiceAsync(UndoSyntheticChoiceSession session, UndoChoiceResultKey selectedKey)
    {
        UndoChoiceSpec choiceSpec = session.ChoiceSpec;
        if (!IsSourceChoice(choiceSpec, "MegaCrit.Sts2.Core.Models.Cards.DecisionsDecisions")
            || choiceSpec.Kind != UndoChoiceKind.HandSelection
            || choiceSpec.SourcePileType != PileType.Hand
            || selectedKey.OptionIndexes.Count != 1)
        {
            return false;
        }

        DisableReplayChecksumComparison(session.AnchorSnapshot.CombatState.NextChecksumId);

        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        Player? player = combatState == null ? null : LocalContext.GetMe(combatState);
        if (player == null)
            return false;

        int optionIndex = selectedKey.OptionIndexes[0];
        if (optionIndex < 0 || optionIndex >= choiceSpec.SourcePileOptionIndexes.Count)
            return false;

        IReadOnlyList<CardModel> handCards = PileType.Hand.GetPile(player).Cards;
        int handIndex = choiceSpec.SourcePileOptionIndexes[optionIndex];
        if (handIndex < 0 || handIndex >= handCards.Count)
            return false;

        CardModel selectedCard = handCards[handIndex];
        PausedChoiceState? pausedChoiceState = session.AnchorSnapshot.CombatState.ActionKernelState.PausedChoiceState;
        BlockingPlayerChoiceContext choiceContext = new();
        CardModel? sourceCard = BeginDetachedPlayedCardChoiceContext(player, choiceSpec, pausedChoiceState, choiceContext);

        // 这张牌的选择结果不会写进独立的 runtime 字段，而是直接把选中的技能连续自动打出 3 次。
        // 因此 undo 重选时不能再走 hand-selection 的合成分支，而要把官方 AutoPlay 链真正执行一遍。
        try
        {
            for (int i = 0; i < 3; i++)
                await CardCmd.AutoPlay(choiceContext, selectedCard, null, AutoPlayType.Default, false, false);

            await FinalizeDetachedPlayedCardAsync(player, choiceContext, choiceSpec, sourceCard);
        }
        finally
        {
            EndDetachedPlayedCardChoiceContext(choiceContext, sourceCard);
        }
        return true;
    }

    private async Task<bool> TryExecuteEntropyChoiceAsync(UndoSyntheticChoiceSession session, UndoChoiceResultKey selectedKey)
    {
        UndoChoiceSpec choiceSpec = session.ChoiceSpec;
        if (!IsSourceChoice(choiceSpec, typeof(EntropyPower))
            || choiceSpec.Kind != UndoChoiceKind.HandSelection
            || choiceSpec.SourcePileType != PileType.Hand
            || selectedKey.OptionIndexes.Count == 0)
        {
            return false;
        }

        DisableReplayChecksumComparison(session.AnchorSnapshot.CombatState.NextChecksumId);

        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        Player? player = combatState == null ? null : LocalContext.GetMe(combatState);
        if (player == null)
            return false;

        IReadOnlyList<CardModel> handCards = PileType.Hand.GetPile(player).Cards;
        List<CardModel> selectedCards = [];
        foreach (int optionIndex in selectedKey.OptionIndexes)
        {
            if (optionIndex < 0 || optionIndex >= choiceSpec.SourcePileOptionIndexes.Count)
                return false;

            int handIndex = choiceSpec.SourcePileOptionIndexes[optionIndex];
            if (handIndex < 0 || handIndex >= handCards.Count)
                return false;

            selectedCards.Add(handCards[handIndex]);
        }

        foreach (CardModel selectedCard in selectedCards)
            await CardCmd.TransformToRandom(selectedCard, player.RunState.Rng.CombatCardSelection, CardPreviewStyle.HorizontalLayout);

        return true;
    }

    private async Task<bool> TryExecuteHandExhaustChoiceAsync(UndoSyntheticChoiceSession session, UndoChoiceResultKey selectedKey)
    {
        UndoChoiceSpec choiceSpec = session.ChoiceSpec;
        if (choiceSpec.Kind != UndoChoiceKind.HandSelection
            || choiceSpec.SourcePileType != PileType.Hand
            || !IsHandExhaustChoiceSource(choiceSpec))
        {
            return false;
        }

        DisableReplayChecksumComparison(session.AnchorSnapshot.CombatState.NextChecksumId);

        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        Player? player = combatState == null ? null : LocalContext.GetMe(combatState);
        if (player == null)
            return false;

        if (!TryResolveSelectedHandCards(choiceSpec, selectedKey, player, out List<CardModel> selectedCards))
            return false;

        PausedChoiceState? pausedChoiceState = session.AnchorSnapshot.CombatState.ActionKernelState.PausedChoiceState;
        bool shouldFinalizeDetachedPlayedCard = ShouldFinalizeDetachedPlayedCardChoice(pausedChoiceState, choiceSpec);
        BlockingPlayerChoiceContext choiceContext = new();
        CardModel? sourceCard = BeginDetachedPlayedCardChoiceContext(player, choiceSpec, pausedChoiceState, choiceContext);
        try
        {
            foreach (CardModel selectedCard in selectedCards)
                await CardCmd.Exhaust(choiceContext, selectedCard, false, false);

            if (IsSourceChoice(choiceSpec, typeof(BurningPact)))
            {
                int drawCount = sourceCard?.DynamicVars.Cards.IntValue ?? 0;
                await CreatureCmd.TriggerAnim(player.Creature, "Cast", player.Character.CastAnimDelay);
                await CardPileCmd.Draw(choiceContext, drawCount, player, false);
            }
            else if (IsSourceChoice(choiceSpec, typeof(Scavenge)))
            {
                int energyNextTurn = sourceCard?.DynamicVars.Energy.IntValue ?? 0;
                await PowerCmd.Apply<EnergyNextTurnPower>(player.Creature, energyNextTurn, player.Creature, sourceCard, false);
            }
            else if (IsSourceChoice(choiceSpec, typeof(Brand)))
            {
                NCombatRoom.Instance?.CombatVfxContainer.AddChildSafely(NGroundFireVfx.Create(player.Creature, VfxColor.Red));
                SfxCmd.Play("event:/sfx/characters/attack_fire", 1f);
                decimal strengthAmount = sourceCard?.DynamicVars.Strength.BaseValue ?? 0m;
                await PowerCmd.Apply<StrengthPower>(player.Creature, strengthAmount, player.Creature, sourceCard, false);
            }

            if (shouldFinalizeDetachedPlayedCard)
                await FinalizeDetachedPlayedCardAsync(player, choiceContext, choiceSpec, sourceCard);
        }
        finally
        {
            EndDetachedPlayedCardChoiceContext(choiceContext, sourceCard);
        }

        return true;
    }

    private async Task<bool> TryExecuteSimpleGridAddToHandChoiceAsync(UndoSyntheticChoiceSession session, UndoChoiceResultKey selectedKey)
    {
        UndoChoiceSpec choiceSpec = session.ChoiceSpec;
        if (choiceSpec.Kind != UndoChoiceKind.SimpleGridSelection
            || !IsSimpleGridAddToHandChoiceSource(choiceSpec))
        {
            return false;
        }

        DisableReplayChecksumComparison(session.AnchorSnapshot.CombatState.NextChecksumId);

        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        Player? player = combatState == null ? null : LocalContext.GetMe(combatState);
        if (player == null)
            return false;

        if (!TryResolveSelectedSourcePileCards(choiceSpec, selectedKey, player, out List<CardModel> selectedCards))
            return false;

        PausedChoiceState? pausedChoiceState = session.AnchorSnapshot.CombatState.ActionKernelState.PausedChoiceState;
        bool shouldFinalizeDetachedPlayedCard = ShouldFinalizeDetachedPlayedCardChoice(pausedChoiceState, choiceSpec);
        BlockingPlayerChoiceContext choiceContext = new();
        CardModel? sourceCard = BeginDetachedPlayedCardChoiceContext(player, choiceSpec, pausedChoiceState, choiceContext);
        try
        {
            if (IsSourceChoice(choiceSpec, typeof(LiquidMemories)) && selectedCards.Count > 0)
                selectedCards[0].EnergyCost.SetThisTurnOrUntilPlayed(0, false);

            if (selectedCards.Count > 0)
                await CardPileCmd.Add(selectedCards, PileType.Hand, CardPilePosition.Bottom, null, false);

            if (IsSourceChoice(choiceSpec, typeof(ForegoneConclusionPower))
                && ResolveSyntheticHandChoiceSourceModel(player, choiceSpec) is PowerModel foregoneConclusion)
            {
                await PowerCmd.Remove(foregoneConclusion);
            }

            if (shouldFinalizeDetachedPlayedCard)
                await FinalizeDetachedPlayedCardAsync(player, choiceContext, choiceSpec, sourceCard);
        }
        finally
        {
            EndDetachedPlayedCardChoiceContext(choiceContext, sourceCard);
        }

        return true;
    }

    private async Task<bool> TryExecuteGeneratedGridToHandChoiceAsync(UndoSyntheticChoiceSession session, UndoChoiceResultKey selectedKey)
    {
        UndoChoiceSpec choiceSpec = session.ChoiceSpec;
        if (choiceSpec.Kind != UndoChoiceKind.SimpleGridSelection
            || !IsGeneratedGridToHandChoiceSource(choiceSpec))
        {
            return false;
        }

        DisableReplayChecksumComparison(session.AnchorSnapshot.CombatState.NextChecksumId);

        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        Player? player = combatState == null ? null : LocalContext.GetMe(combatState);
        if (player == null)
            return false;

        if (!TryResolveSelectedDisplayedOptionCards(choiceSpec, selectedKey, player, out List<CardModel> selectedCards))
            return false;

        PausedChoiceState? pausedChoiceState = session.AnchorSnapshot.CombatState.ActionKernelState.PausedChoiceState;
        bool shouldFinalizeDetachedPlayedCard = ShouldFinalizeDetachedPlayedCardChoice(pausedChoiceState, choiceSpec);
        BlockingPlayerChoiceContext choiceContext = new();
        CardModel? sourceCard = BeginDetachedPlayedCardChoiceContext(player, choiceSpec, pausedChoiceState, choiceContext);
        try
        {
            foreach (CardModel selectedCard in selectedCards)
                await CardPileCmd.AddGeneratedCardToCombat(selectedCard, PileType.Hand, true, CardPilePosition.Bottom);

            if (shouldFinalizeDetachedPlayedCard)
                await FinalizeDetachedPlayedCardAsync(player, choiceContext, choiceSpec, sourceCard);
        }
        finally
        {
            EndDetachedPlayedCardChoiceContext(choiceContext, sourceCard);
        }

        return true;
    }

    private static bool IsDiscardSelection(CardSelectorPrefs prefs)
    {
        return string.Equals(prefs.Prompt.LocTable, "card_selection", StringComparison.Ordinal)
            && string.Equals(prefs.Prompt.LocEntryKey, "TO_DISCARD", StringComparison.Ordinal);
    }

    private static bool IsOfficialFromHandDiscardChoice(UndoChoiceSpec choiceSpec)
    {
        return choiceSpec.Kind == UndoChoiceKind.HandSelection
            && choiceSpec.SourcePileType == PileType.Hand
            && IsDiscardSelection(choiceSpec.SelectionPrefs);
    }

    private static bool IsTrackedOfficialFromHandDiscardChoice(PausedChoiceState? pausedChoiceState, UndoChoiceSpec choiceSpec)
    {
        return pausedChoiceState?.SourceActionRef?.ActionId != null
            && string.Equals(pausedChoiceState.SourceActionCodecId, "action:from-hand", StringComparison.Ordinal)
            && IsOfficialFromHandDiscardChoice(choiceSpec);
    }

    private static bool HasDetachableOfficialHandDiscardChoiceSource(PausedChoiceState? pausedChoiceState, UndoChoiceSpec choiceSpec)
    {
        return pausedChoiceState?.SourceActionRef?.ActionId != null
            && IsOfficialFromHandDiscardChoice(choiceSpec);
    }

    private static bool MatchesOfficialHandDiscardActionCandidate(
        GameAction action,
        PausedChoiceState? pausedChoiceState,
        IReadOnlySet<uint> trackedIds)
    {
        if (action.Id is uint actionId && trackedIds.Contains(actionId))
            return true;

        if (action.State != GameActionState.GatheringPlayerChoice)
            return false;

        if (pausedChoiceState?.OwnerNetId is ulong ownerNetId && action.OwnerId != ownerNetId)
            return false;

        string? expectedTypeName = pausedChoiceState?.SourceActionRef?.TypeName;
        if (!string.IsNullOrWhiteSpace(expectedTypeName))
            return string.Equals(action.GetType().FullName, expectedTypeName, StringComparison.Ordinal);

        return true;
    }

    private static bool HasPendingOfficialHandDiscardChoiceSource(PausedChoiceState? pausedChoiceState, IReadOnlySet<uint> trackedIds)
    {
        ActionExecutor actionExecutor = RunManager.Instance.ActionExecutor;
        if (actionExecutor.CurrentlyRunningAction is { } currentAction
            && MatchesOfficialHandDiscardActionCandidate(currentAction, pausedChoiceState, trackedIds))
        {
            return true;
        }

        if (FindField(RunManager.Instance.ActionQueueSet.GetType(), "_actionQueues")?.GetValue(RunManager.Instance.ActionQueueSet) is System.Collections.IEnumerable rawQueues)
        {
            foreach (object rawQueue in rawQueues)
            {
                if (FindField(rawQueue.GetType(), "actions")?.GetValue(rawQueue) is not System.Collections.IEnumerable actions)
                    continue;

                foreach (GameAction action in actions.OfType<GameAction>())
                {
                    if (MatchesOfficialHandDiscardActionCandidate(action, pausedChoiceState, trackedIds))
                        return true;
                }
            }
        }

        if (FindField(RunManager.Instance.ActionQueueSet.GetType(), "_actionsWaitingForResumption")?.GetValue(RunManager.Instance.ActionQueueSet) is not System.Collections.IEnumerable rawWaiting)
            return false;

        foreach (object waiting in rawWaiting)
        {
            object? oldIdValue = FindField(waiting.GetType(), "oldId")?.GetValue(waiting);
            object? newIdValue = FindField(waiting.GetType(), "newId")?.GetValue(waiting);
            if ((oldIdValue is uint oldId && trackedIds.Contains(oldId))
                || (newIdValue is uint newId && trackedIds.Contains(newId)))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryDetachOfficialHandDiscardSourceAction(UndoSyntheticChoiceSession session)
    {
        PausedChoiceState? pausedChoiceState = session.AnchorSnapshot.CombatState.ActionKernelState.PausedChoiceState;
        if (!HasDetachableOfficialHandDiscardChoiceSource(pausedChoiceState, session.ChoiceSpec))
            return false;

        uint? sourceActionId = pausedChoiceState!.SourceActionRef?.ActionId;
        uint? resumeActionId = pausedChoiceState.ResumeActionId;
        HashSet<uint> trackedIds = [];
        if (sourceActionId is uint sourceId)
            trackedIds.Add(sourceId);
        if (resumeActionId is uint resumeId)
            trackedIds.Add(resumeId);

        bool removedAny = false;
        ActionExecutor actionExecutor = RunManager.Instance.ActionExecutor;
        if (actionExecutor.CurrentlyRunningAction is { } currentAction
            && MatchesOfficialHandDiscardActionCandidate(currentAction, pausedChoiceState, trackedIds))
        {
            if (currentAction.Id is uint currentActionId)
                trackedIds.Add(currentActionId);

            QuarantineActionForRestore(currentAction);
            UndoReflectionUtil.TrySetPropertyValue(actionExecutor, "CurrentlyRunningAction", null);
            removedAny = true;
        }

        if (FindField(RunManager.Instance.ActionQueueSet.GetType(), "_actionQueues")?.GetValue(RunManager.Instance.ActionQueueSet) is System.Collections.IEnumerable rawQueues)
        {
            foreach (object rawQueue in rawQueues)
            {
                if (FindField(rawQueue.GetType(), "actions")?.GetValue(rawQueue) is not System.Collections.IList actions)
                    continue;

                for (int i = actions.Count - 1; i >= 0; i--)
                {
                    if (actions[i] is not GameAction action
                        || !MatchesOfficialHandDiscardActionCandidate(action, pausedChoiceState, trackedIds))
                    {
                        continue;
                    }

                    if (action.Id is uint actionId)
                        trackedIds.Add(actionId);

                    QuarantineActionForRestore(action);
                    actions.RemoveAt(i);
                    removedAny = true;
                }
            }
        }

        if (FindField(RunManager.Instance.ActionQueueSet.GetType(), "_actionsWaitingForResumption")?.GetValue(RunManager.Instance.ActionQueueSet) is System.Collections.IList waitingForResumption)
        {
            for (int i = waitingForResumption.Count - 1; i >= 0; i--)
            {
                object waiting = waitingForResumption[i];
                object? oldIdValue = FindField(waiting.GetType(), "oldId")?.GetValue(waiting);
                object? newIdValue = FindField(waiting.GetType(), "newId")?.GetValue(waiting);
                uint? oldId = oldIdValue is uint oldIdTyped ? oldIdTyped : null;
                uint? newId = newIdValue is uint newIdTyped ? newIdTyped : null;
                if ((oldId != null && trackedIds.Contains(oldId.Value))
                    || (newId != null && trackedIds.Contains(newId.Value)))
                {
                    waitingForResumption.RemoveAt(i);
                    removedAny = true;
                }
            }
        }

        bool detached = removedAny
            || !HasPendingOfficialHandDiscardChoiceSource(pausedChoiceState, trackedIds);
        if (!detached)
            return false;

        ActionSynchronizerCombatState targetSynchronizerState = CombatManager.Instance.DebugOnlyGetState()?.CurrentSide == CombatSide.Player
            ? ActionSynchronizerCombatState.PlayPhase
            : ActionSynchronizerCombatState.NotPlayPhase;
        RestoreActionSynchronizationState(targetSynchronizerState, ActionKernelBoundaryKind.StableBoundary, out _);
        EnterDetachedHandDiscardExecutionGuard(session.ChoiceSpec);
        ClearPendingHandChoiceSourceTracking(canceled: true);
        UndoDebugLog.Write(
            $"official_hand_choice_detached_fallback source={session.ChoiceSpec.SourceModelTypeName ?? "unknown"}"
            + $" sourceActionId={sourceActionId?.ToString() ?? "null"}"
            + $" resumeActionId={resumeActionId?.ToString() ?? "null"}");
        return true;
    }

    private void EnterDetachedHandDiscardExecutionGuard(UndoChoiceSpec choiceSpec)
    {
        _detachedHandDiscardExecutionGuardDepth++;
        if (_detachedHandDiscardExecutionGuardDepth != 1)
            return;

        RunManager.Instance.ActionQueueSet.PauseAllPlayerQueues();
        RunManager.Instance.ActionExecutor.Unpause();
        UndoDebugLog.Write(
            $"official_hand_choice_queue_guard_enter source={choiceSpec.SourceModelTypeName ?? "unknown"}"
            + $" replayEvents={GetCurrentReplayEventCount()}");
    }

    private void ReleaseDetachedHandDiscardExecutionGuard(string stage)
    {
        if (_detachedHandDiscardExecutionGuardDepth <= 0)
            return;

        _detachedHandDiscardExecutionGuardDepth--;
        if (_detachedHandDiscardExecutionGuardDepth != 0)
            return;

        RunManager.Instance.ActionQueueSet.UnpauseAllPlayerQueues();
        RunManager.Instance.ActionExecutor.Unpause();
        UndoDebugLog.Write(
            $"official_hand_choice_queue_guard_exit stage={stage}"
            + $" replayEvents={GetCurrentReplayEventCount()}");
    }

    private bool HasReplayResumeEvent(UndoSyntheticChoiceSession session, int replayEventCount, uint? expectedResumeActionId)
    {
        if (_combatReplay == null)
            return false;

        int startIndex = Math.Max(0, session.AnchorSnapshot.ReplayEventCount);
        int endIndex = Math.Min(replayEventCount, _combatReplay.Events.Count);
        for (int i = startIndex; i < endIndex; i++)
        {
            CombatReplayEvent replayEvent = _combatReplay.Events[i];
            if (replayEvent.eventType != CombatReplayEventType.ResumeAction)
                continue;

            if (expectedResumeActionId == null || replayEvent.actionId == expectedResumeActionId.Value)
                return true;
        }

        return false;
    }

    private static bool MatchesTrackedAction(GameAction action, uint? sourceActionId, uint? resumeActionId)
    {
        uint? actionId = action.Id;
        return actionId != null && (actionId == sourceActionId || actionId == resumeActionId);
    }

    private static GameAction? FindTrackedAction(uint? actionId)
    {
        if (actionId == null)
            return null;

        if (RunManager.Instance.ActionExecutor.CurrentlyRunningAction?.Id == actionId.Value)
            return RunManager.Instance.ActionExecutor.CurrentlyRunningAction;

        if (FindField(RunManager.Instance.ActionQueueSet.GetType(), "_actionQueues")?.GetValue(RunManager.Instance.ActionQueueSet) is not System.Collections.IEnumerable rawQueues)
            return null;

        foreach (object rawQueue in rawQueues)
        {
            if (FindField(rawQueue.GetType(), "actions")?.GetValue(rawQueue) is not System.Collections.IEnumerable actions)
                continue;

            foreach (GameAction action in actions.OfType<GameAction>())
            {
                if (action.Id == actionId.Value)
                    return action;
            }
        }

        return null;
    }

    private static bool IsTrackedActionPresent(uint? actionId)
    {
        return FindTrackedAction(actionId) != null;
    }

    private static bool IsTrackedActionExecuting(uint? actionId)
    {
        if (actionId == null)
            return false;

        return RunManager.Instance.ActionExecutor.CurrentlyRunningAction?.Id == actionId.Value;
    }

    private static bool IsResumeActionPending(PausedChoiceState pausedChoiceState)
    {
        if (FindField(RunManager.Instance.ActionQueueSet.GetType(), "_actionsWaitingForResumption")?.GetValue(RunManager.Instance.ActionQueueSet) is not System.Collections.IEnumerable rawWaiting)
            return false;

        uint? sourceActionId = pausedChoiceState.SourceActionRef?.ActionId;
        uint? resumeActionId = pausedChoiceState.ResumeActionId;
        foreach (object waiting in rawWaiting)
        {
            object? oldIdValue = FindField(waiting.GetType(), "oldId")?.GetValue(waiting);
            object? newIdValue = FindField(waiting.GetType(), "newId")?.GetValue(waiting);
            uint? oldId = oldIdValue is uint oldIdTyped ? oldIdTyped : null;
            uint? newId = newIdValue is uint newIdTyped ? newIdTyped : null;
            if ((sourceActionId != null && (oldId == sourceActionId || newId == sourceActionId))
                || (resumeActionId != null && (oldId == resumeActionId || newId == resumeActionId)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveSelectedHandCards(UndoChoiceSpec choiceSpec, UndoChoiceResultKey selectedKey, Player player, out List<CardModel> selectedCards)
    {
        selectedCards = [];
        if (selectedKey.OptionIndexes.Count == 0)
            return choiceSpec.CanSkip;

        IReadOnlyList<CardModel> handCards = PileType.Hand.GetPile(player).Cards;
        foreach (int optionIndex in selectedKey.OptionIndexes)
        {
            if (optionIndex < 0 || optionIndex >= choiceSpec.SourcePileOptionIndexes.Count)
                return false;

            int handIndex = choiceSpec.SourcePileOptionIndexes[optionIndex];
            if (handIndex < 0 || handIndex >= handCards.Count)
                return false;

            selectedCards.Add(handCards[handIndex]);
        }

        return true;
    }

    private static bool TryResolveSelectedSourcePileCards(UndoChoiceSpec choiceSpec, UndoChoiceResultKey selectedKey, Player player, out List<CardModel> selectedCards)
    {
        selectedCards = [];
        if (selectedKey.OptionIndexes.Count == 0)
            return choiceSpec.CanSkip;

        if (choiceSpec.SourcePileType == null)
            return false;

        IReadOnlyList<CardModel> sourceCards = choiceSpec.SourcePileType.Value.GetPile(player).Cards;
        foreach (int optionIndex in selectedKey.OptionIndexes)
        {
            if (optionIndex < 0 || optionIndex >= choiceSpec.SourcePileOptionIndexes.Count)
                return false;

            int sourceIndex = choiceSpec.SourcePileOptionIndexes[optionIndex];
            if (sourceIndex < 0 || sourceIndex >= sourceCards.Count)
                return false;

            selectedCards.Add(sourceCards[sourceIndex]);
        }

        return true;
    }

    private static bool TryResolveSelectedDisplayedOptionCards(UndoChoiceSpec choiceSpec, UndoChoiceResultKey selectedKey, Player player, out List<CardModel> selectedCards)
    {
        selectedCards = [];
        if (selectedKey.OptionIndexes.Count == 0)
            return choiceSpec.CanSkip;

        IReadOnlyList<CardModel> displayedOptions =
            NOverlayStack.Instance?.Peek() is NCardGridSelectionScreen screen
            && GetPrivateFieldValue<IReadOnlyList<CardModel>>(screen, "_cards") is { } liveOptions
            && liveOptions.Count > 0
                ? liveOptions
                : choiceSpec.BuildOptionCards(player);
        foreach (int optionIndex in selectedKey.OptionIndexes)
        {
            if (optionIndex < 0 || optionIndex >= displayedOptions.Count)
                return false;

            selectedCards.Add(displayedOptions[optionIndex]);
        }

        return true;
    }

    private void PrioritizeChoiceAnchorHistoryNode(string actionLabel, UndoChoiceSpec? choiceSpec, int replayEventCount)
    {
        if (choiceSpec == null)
            return;

        for (LinkedListNode<UndoSnapshot>? node = _pastSnapshots.First; node != null; node = node.Next)
        {
            if (!node.Value.IsChoiceAnchor
                || node.Value.ReplayEventCount != replayEventCount
                || !string.Equals(node.Value.ActionLabel, actionLabel, StringComparison.Ordinal)
                || !AreEquivalentChoiceSpecs(node.Value.ChoiceSpec, choiceSpec))
            {
                continue;
            }

            if (node != _pastSnapshots.First)
            {
                UndoSnapshot prioritizedAnchor = node.Value;
                _pastSnapshots.Remove(node);
                _pastSnapshots.AddFirst(prioritizedAnchor);
                UndoDebugLog.Write(
                    $"choice_anchor_prioritized replayEvents={replayEventCount}"
                    + $" label={actionLabel}");
            }

            return;
        }
    }

    private void RememberSavedChoiceBranches(UndoSyntheticChoiceSession session, IReadOnlyList<UndoChoiceBranchState> branchStates)
    {
        foreach (UndoChoiceBranchState branchState in branchStates)
            session.RememberBranch(branchState.ChoiceResultKey, MaterializeChoiceBranchSnapshot(branchState), preferAsTemplate: false);
    }

    private void TryPersistImmediateChoiceBranchSnapshot(
        UndoSnapshot snapshot,
        UndoChoiceSpec? anchorChoiceSpecOverride = null,
        UndoChoiceResultKey? choiceResultKeyOverride = null)
    {
        UndoChoiceResultKey? resolvedChoiceResultKey = choiceResultKeyOverride ?? _lastResolvedChoiceResultKey;
        if (resolvedChoiceResultKey == null)
            return;

        LinkedListNode<UndoSnapshot>? anchorNode = ResolveImmediateChoiceBranchAnchorNode(snapshot, anchorChoiceSpecOverride);
        if (anchorNode == null)
            return;

        UndoSnapshot branchSnapshot = new(
            snapshot.CombatState,
            snapshot.ReplayEventCount,
            snapshot.ActionKind,
            snapshot.SequenceId,
            snapshot.ActionLabel,
            snapshot.IsChoiceAnchor,
            snapshot.ChoiceSpec,
            resolvedChoiceResultKey,
            snapshot.HistoryOrderReplayEventCount);
        Dictionary<UndoChoiceResultKey, UndoChoiceBranchState> savedBranches = anchorNode.Value.CombatState.ChoiceBranchStates
            .ToDictionary(static branch => branch.ChoiceResultKey, static branch => branch);
        savedBranches[resolvedChoiceResultKey] = CaptureChoiceBranchState(branchSnapshot);
        anchorNode.Value = new UndoSnapshot(
            WithChoiceBranchStates(anchorNode.Value.CombatState, [.. savedBranches.Values.OrderBy(static branch => branch.ReplayEventCount)]),
            anchorNode.Value.ReplayEventCount,
            anchorNode.Value.ActionKind,
            anchorNode.Value.SequenceId,
            anchorNode.Value.ActionLabel,
            isChoiceAnchor: true,
            choiceSpec: anchorNode.Value.ChoiceSpec,
            historyOrderReplayEventCount: anchorNode.Value.HistoryOrderReplayEventCount);
    }

    private LinkedListNode<UndoSnapshot>? ResolveImmediateChoiceBranchAnchorNode(UndoSnapshot snapshot, UndoChoiceSpec? anchorChoiceSpecOverride)
    {
        LinkedListNode<UndoSnapshot>? node = _pastSnapshots.First?.Next;
        while (node != null)
        {
            if (!node.Value.IsChoiceAnchor
                || !IsSupportedChoiceAnchorKind(node.Value.ChoiceSpec)
                || snapshot.ReplayEventCount < node.Value.ReplayEventCount)
            {
                node = node.Next;
                continue;
            }

            if (anchorChoiceSpecOverride == null || AreEquivalentChoiceSpecs(node.Value.ChoiceSpec, anchorChoiceSpecOverride))
                return node;

            node = node.Next;
        }

        return null;
    }

    private IReadOnlyList<UndoChoiceBranchState> CaptureSavedChoiceBranchStates(UndoSyntheticChoiceSession session)
    {
        Dictionary<UndoChoiceResultKey, UndoChoiceBranchState> savedBranches = new();
        foreach (UndoChoiceBranchState branchState in session.AnchorSnapshot.CombatState.ChoiceBranchStates)
            savedBranches[branchState.ChoiceResultKey] = branchState;
        foreach (KeyValuePair<UndoChoiceResultKey, UndoSnapshot> entry in session.CachedBranches)
            savedBranches[entry.Key] = CaptureChoiceBranchState(entry.Value);

        return [.. savedBranches.Values.OrderBy(static branch => branch.ReplayEventCount)];
    }

    private static UndoChoiceBranchState CaptureChoiceBranchState(UndoSnapshot snapshot)
    {
        if (snapshot.ChoiceResultKey == null)
            throw new InvalidOperationException("Choice branch snapshot must have a choice result key.");

        return new UndoChoiceBranchState
        {
            ChoiceResultKey = snapshot.ChoiceResultKey,
            CombatState = snapshot.CombatState,
            ReplayEventCount = snapshot.ReplayEventCount,
            HistoryOrderReplayEventCount = snapshot.HistoryOrderReplayEventCount,
            ActionKind = snapshot.ActionKind,
            ActionLabel = snapshot.ActionLabel,
            IsChoiceAnchor = snapshot.IsChoiceAnchor,
            ChoiceSpec = snapshot.ChoiceSpec
        };
    }

    private UndoSnapshot MaterializeChoiceBranchSnapshot(UndoChoiceBranchState branchState)
    {
        return new UndoSnapshot(
            branchState.CombatState,
            branchState.ReplayEventCount,
            branchState.ActionKind,
            _nextSequenceId++,
            branchState.ActionLabel,
            branchState.IsChoiceAnchor,
            branchState.ChoiceSpec,
            branchState.ChoiceResultKey,
            branchState.HistoryOrderReplayEventCount);
    }

    private void PersistChoiceAnchorBranches(UndoSnapshot anchorSnapshot, IReadOnlyList<UndoChoiceBranchState> branchStates)
    {
        LinkedListNode<UndoSnapshot>? anchorNode = FindChoiceAnchorNode(anchorSnapshot);
        if (anchorNode == null)
            return;

        UndoSnapshot existingAnchor = anchorNode.Value;
        anchorNode.Value = new UndoSnapshot(
            WithChoiceBranchStates(existingAnchor.CombatState, branchStates),
            existingAnchor.ReplayEventCount,
            existingAnchor.ActionKind,
            existingAnchor.SequenceId,
            existingAnchor.ActionLabel,
            existingAnchor.IsChoiceAnchor,
            existingAnchor.ChoiceSpec,
            existingAnchor.ChoiceResultKey,
            existingAnchor.HistoryOrderReplayEventCount);
    }

    private LinkedListNode<UndoSnapshot>? FindChoiceAnchorNode(UndoSnapshot anchorSnapshot)
    {
        for (LinkedListNode<UndoSnapshot>? node = _pastSnapshots.First; node != null; node = node.Next)
        {
            if (node.Value.SequenceId == anchorSnapshot.SequenceId)
                return node;
        }

        for (LinkedListNode<UndoSnapshot>? node = _pastSnapshots.First; node != null; node = node.Next)
        {
            if (node.Value.IsChoiceAnchor
                && node.Value.ReplayEventCount == anchorSnapshot.ReplayEventCount
                && string.Equals(node.Value.ActionLabel, anchorSnapshot.ActionLabel, StringComparison.Ordinal)
                && AreEquivalentChoiceSpecs(node.Value.ChoiceSpec, anchorSnapshot.ChoiceSpec))
            {
                return node;
            }
        }

        return null;
    }

    private static UndoCombatFullState WithChoiceBranchStates(UndoCombatFullState source, IReadOnlyList<UndoChoiceBranchState> choiceBranchStates)
    {
        return new UndoCombatFullState(
            source.FullState,
            source.RoundNumber,
            source.CurrentSide,
            source.SynchronizerCombatState,
            source.NextActionId,
            source.NextHookId,
            source.NextChecksumId,
            source.CombatHistoryState,
            source.ActionKernelState,
            source.MonsterStates,
            source.CardCostStates,
            source.CardRuntimeStates,
            source.PowerRuntimeStates,
            source.RelicRuntimeStates,
            source.SelectionSessionState,
            source.FirstInSeriesPlayCounts,
            source.RuntimeGraphState,
            source.PresentationHints,
            source.CreatureTopologyStates,
            source.CreatureStatusRuntimeStates,
            source.CreatureVisualStates,
            source.CombatCardDbState,
            source.PlayerOrbStates,
            source.PlayerDeckStates,
            source.PlayerPotionStates,
            source.AudioLoopStates,
            source.SchemaVersion,
            choiceBranchStates);
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

        if (choiceSpec.Kind == UndoChoiceKind.ChooseACard)
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
}
