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
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Potions;
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
        _syntheticChoiceSession = primarySession;
        _lastResolvedChoiceSpec = choiceSpec;
        _lastResolvedChoiceResultKey = null;
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
                return choiceSpec.TryMapDisplayedOptionSelection(options, selected);
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

        // from-hand 的弃牌类选择，优先回到官方 play snapshot 再 replay 到 choice 点。
        // 这样可以复用 Survivor 这类 still-live 的官方收尾路径，避免直接对 choice snapshot
        // 做 full-state restore 时命中手牌 holder/PlayCardAction 中间态。
        if (string.Equals(pausedChoiceState.SourceActionCodecId, "action:from-hand", StringComparison.Ordinal))
        {
            UndoChoiceSpec? choiceSpec = pausedChoiceState.ChoiceSpec;
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
            if (_syntheticChoiceSession != null && _syntheticChoiceSession != session)
                return;

            if (selectedKey == null)
            {
                UndoDebugLog.Write($"primary_choice_restore_selected_key:null replayEvents={session.AnchorSnapshot.ReplayEventCount} label={session.AnchorSnapshot.ActionLabel} codec={session.ChoiceSpec.Kind}");
                if (ReferenceEquals(_syntheticChoiceSession, session))
                    _syntheticChoiceSession = null;
                NotifyStateChanged();
                return;
            }

            UndoDebugLog.Write($"primary_choice_restore_selected_key:{selectedKey} replayEvents={session.AnchorSnapshot.ReplayEventCount} label={session.AnchorSnapshot.ActionLabel} codec={session.ChoiceSpec.Kind}");
            if (preferLiveBranchCommit && await TryCommitLiveChoiceBranchAsync(session, selectedKey))
            {
                WriteInteractionLog("branch_commit_after_reselect", $"choice={selectedKey} label={session.AnchorSnapshot.ActionLabel} replayEvents={session.AnchorSnapshot.ReplayEventCount} source=live");
                return;
            }

            if (ShouldPreferCustomChoiceBeforeCached(session)
                && await TryCommitCustomChoiceBranchAsync(session, selectedKey))
            {
                WriteInteractionLog("branch_commit_after_reselect", $"choice={selectedKey} label={session.AnchorSnapshot.ActionLabel} replayEvents={session.AnchorSnapshot.ReplayEventCount} source=custom");
                return;
            }

            if (session.CachedBranches.TryGetValue(selectedKey, out UndoSnapshot? cachedBranchSnapshot))
            {
                SyntheticChoiceVfxRequest? cachedVfxRequest = CaptureSyntheticChoiceVfxRequest(session, cachedBranchSnapshot, selectedKey);
                if (await TryApplySynthesizedChoiceBranchAsync(session, cachedBranchSnapshot, selectedKey, cachedVfxRequest))
                    WriteInteractionLog("branch_commit_after_reselect", $"choice={selectedKey} label={session.AnchorSnapshot.ActionLabel} replayEvents={session.AnchorSnapshot.ReplayEventCount} source=cached");
                return;
            }

            if (await TryCommitCustomChoiceBranchAsync(session, selectedKey))
            {
                WriteInteractionLog("branch_commit_after_reselect", $"choice={selectedKey} label={session.AnchorSnapshot.ActionLabel} replayEvents={session.AnchorSnapshot.ReplayEventCount} source=custom");
                return;
            }

            UndoDebugLog.Write($"primary_choice_branch_miss:{selectedKey} replayEvents={session.AnchorSnapshot.ReplayEventCount} label={session.AnchorSnapshot.ActionLabel} cached={session.CachedBranches.Count}");
            if (!TryCreateSyntheticChoiceBranchSnapshot(session, selectedKey, out UndoSnapshot? synthesizedBranch))
            {
                MainFile.Logger.Warn($"Could not synthesize primary branch for restored choice {selectedKey}.");
                if (ReferenceEquals(_syntheticChoiceSession, session))
                    _syntheticChoiceSession = null;
                NotifyStateChanged();
                return;
            }

            SyntheticChoiceVfxRequest? synthesizedVfxRequest = CaptureSyntheticChoiceVfxRequest(session, synthesizedBranch!, selectedKey);
            if (await TryApplySynthesizedChoiceBranchAsync(session, synthesizedBranch!, selectedKey, synthesizedVfxRequest))
                WriteInteractionLog("branch_commit_after_reselect", $"choice={selectedKey} label={session.AnchorSnapshot.ActionLabel} replayEvents={session.AnchorSnapshot.ReplayEventCount} source=synthesized");
        }
        catch (TaskCanceledException)
        {
            if (_syntheticChoiceSession == null || _syntheticChoiceSession == session)
            {
                if (ReferenceEquals(_syntheticChoiceSession, session))
                    _syntheticChoiceSession = null;
                UndoDebugLog.Write($"primary_choice_restore_selected_key:canceled replayEvents={session.AnchorSnapshot.ReplayEventCount} label={session.AnchorSnapshot.ActionLabel}");
                NotifyStateChanged();
            }
        }
    }

    private async Task<bool> TryCommitLiveChoiceBranchAsync(UndoSyntheticChoiceSession session, UndoChoiceResultKey selectedKey)
    {
        if (_combatReplay == null || !CombatManager.Instance.IsInProgress)
            return false;

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

        int replayEventCount = GetCurrentReplayEventCount();
        if (replayEventCount <= session.AnchorSnapshot.ReplayEventCount)
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
        _lastResolvedChoiceSpec = session.ChoiceSpec;
        _lastResolvedChoiceResultKey = selectedKey;
        session.RememberBranch(selectedKey, branchSnapshot);
        _futureSnapshots.Clear();
        _combatReplay.ActiveEventCount = branchSnapshot.ReplayEventCount;

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
        MainFile.Logger.Info($"Re-armed player choice undo anchor. ReplayEvents={rearmedAnchor.ReplayEventCount}, UndoCount={_pastSnapshots.Count}, ChoiceKind={session.ChoiceSpec.Kind} forceRefresh=True");
        NotifyStateChanged();
        return true;
    }

    private async Task<bool> TryCommitCustomChoiceBranchAsync(UndoSyntheticChoiceSession session, UndoChoiceResultKey selectedKey)
    {
        if (!CombatManager.Instance.IsInProgress)
            return false;

        if (await TryExecuteRetainChoiceAsync(session, selectedKey))
        {
            await FinalizeCustomChoiceBranchAsync(session, selectedKey, stabilizeQueues: false);
            return true;
        }

        if (await TryExecuteHandDiscardChoiceAsync(session, selectedKey))
        {
            await FinalizeCustomChoiceBranchAsync(session, selectedKey);
            return true;
        }

        if (await TryExecuteSelectedCardMutationChoiceAsync(session, selectedKey))
        {
            await FinalizeCustomChoiceBranchAsync(session, selectedKey);
            return true;
        }

        if (await TryExecuteDecisionsDecisionsChoiceAsync(session, selectedKey))
        {
            await FinalizeCustomChoiceBranchAsync(session, selectedKey);
            return true;
        }

        if (await TryExecuteEntropyChoiceAsync(session, selectedKey))
        {
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
        UndoChoiceSpec choiceSpec = session.ChoiceSpec;
        return (choiceSpec.Kind == UndoChoiceKind.HandSelection
                && choiceSpec.SourcePileType == PileType.Hand
                && IsDiscardSelection(choiceSpec.SelectionPrefs))
            || IsSourceChoice(choiceSpec, "MegaCrit.Sts2.Core.Models.Cards.DecisionsDecisions");
    }

    private async Task FinalizeCustomChoiceBranchAsync(UndoSyntheticChoiceSession session, UndoChoiceResultKey selectedKey, bool stabilizeQueues = true)
    {
        if (stabilizeQueues)
        {
            await StabilizeAfterCustomChoiceExecutionAsync(session);
        }
        else
        {
            // 生存者这类“官方 discard + Sly 自动打出”已经在 live 状态里完整执行，
            // 这里只等飞牌/打出动画收尾，避免过早 reset 队列把官方 VFX 截断。
            await WaitForTransientCardFlyVfxToSettleAsync();
            await WaitOneFrameAsync();
        }

        UndoSnapshot branchSnapshot = new(
            CaptureCurrentCombatFullState(),
            session.TemplateSnapshot?.ReplayEventCount ?? session.AnchorSnapshot.ReplayEventCount,
            session.TemplateSnapshot?.ActionKind ?? session.AnchorSnapshot.ActionKind,
            _nextSequenceId++,
            session.TemplateSnapshot?.ActionLabel ?? session.AnchorSnapshot.ActionLabel,
            choiceResultKey: selectedKey);

        _syntheticChoiceSession = null;
        _lastResolvedChoiceSpec = session.ChoiceSpec;
        _lastResolvedChoiceResultKey = selectedKey;
        session.RememberBranch(selectedKey, branchSnapshot);
        _futureSnapshots.Clear();
        RewriteReplayChoiceBranch(session.AnchorSnapshot, branchSnapshot);
        _combatReplay!.ActiveEventCount = branchSnapshot.ReplayEventCount;
        TruncateReplayChecksumsFrom(branchSnapshot.CombatState.NextChecksumId);
        DisableReplayChecksumComparison(branchSnapshot.CombatState.NextChecksumId);

        // 手牌弃牌类 custom choice 会直接复用当前 live 模型状态，而不会经过一次完整的 full-state UI 重建。
        // 如果这里不主动把 UI 对齐到最新模型，屏幕中央的当前打牌节点、左侧预览和旧 selected-holder
        // 可能继续残留到下一次 undo，随后在清理时把已经回收到池里的 NCard 再 free 一次。
        if (ShouldRefreshCombatUiAfterCustomChoice(session))
        {
            CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
            if (combatState != null)
                await RefreshCombatUiAsync(combatState);
        }

        EnsurePlayerChoiceUndoAnchor(
            session.AnchorSnapshot,
            session.ChoiceSpec,
            forceRefresh: true,
            anchorCombatStateOverride: WithChoiceBranchStates(session.AnchorSnapshot.CombatState, CaptureSavedChoiceBranchStates(session)));
        NotifyStateChanged();
        await WaitOneFrameAsync();
    }

    private static bool ShouldRefreshCombatUiAfterCustomChoice(UndoSyntheticChoiceSession session)
    {
        UndoChoiceSpec choiceSpec = session.ChoiceSpec;
        return (choiceSpec.Kind == UndoChoiceKind.HandSelection
                && choiceSpec.SourcePileType == PileType.Hand
                && IsDiscardSelection(choiceSpec.SelectionPrefs))
            || IsSourceChoice(choiceSpec, "MegaCrit.Sts2.Core.Models.Cards.DecisionsDecisions");
    }

    private static bool ShouldPreferLiveBranchCommit(PausedChoiceState? pausedChoiceState, UndoChoiceSpec choiceSpec, bool stateAlreadyApplied)
    {
        if (pausedChoiceState == null)
            return false;

        if (stateAlreadyApplied)
            return true;

        return choiceSpec.Kind == UndoChoiceKind.HandSelection
            && choiceSpec.SourcePileType == PileType.Hand
            && IsDiscardSelection(choiceSpec.SelectionPrefs);
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

        await DismissSupportedChoiceUiIfPresentAsync();
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
            || selectedKey.OptionIndexes.Count == 0
            || !IsDiscardSelection(choiceSpec.SelectionPrefs))
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

        // 这类锚点已经处在“前置效果执行完，只差官方 discard 收尾”的位置；
        // 直接走 CardCmd.Discard 可以补回 AfterCardDiscarded、Sly 自动打出等副作用。
        BlockingPlayerChoiceContext choiceContext = new();
        await CardCmd.Discard(choiceContext, selectedCards);
        if (choiceSpec.SourceCombatCard != null)
            await FinalizeDetachedPlayedCardAsync(player, choiceContext, choiceSpec);
        return true;
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

        BlockingPlayerChoiceContext choiceContext = new();
        CardModel? sourceCard = ResolveSourceCardFromPlayPile(player, choiceSpec);
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

        if (choiceSpec.SourceCombatCard != null)
            await FinalizeDetachedPlayedCardAsync(player, choiceContext, choiceSpec);

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

    private static async Task FinalizeDetachedPlayedCardAsync(Player player, PlayerChoiceContext choiceContext, UndoChoiceSpec choiceSpec)
    {
        CardModel? playedCard = ResolveSourceCardFromPlayPile(player, choiceSpec);
        if (playedCard == null)
            return;

        playedCard.InvokeExecutionFinished();
        await playedCard.MoveToResultPileWithoutPlaying(choiceContext);
        await CombatManager.Instance.CheckForEmptyHand(choiceContext, player);
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
        BlockingPlayerChoiceContext choiceContext = new();

        // 这张牌的选择结果不会写进独立的 runtime 字段，而是直接把选中的技能连续自动打出 3 次。
        // 因此 undo 重选时不能再走 hand-selection 的合成分支，而要把官方 AutoPlay 链真正执行一遍。
        for (int i = 0; i < 3; i++)
            await CardCmd.AutoPlay(choiceContext, selectedCard, null, AutoPlayType.Default, false, false);

        await FinalizeDetachedPlayedCardAsync(player, choiceContext, choiceSpec);
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

    private async Task<bool> TryExecuteStratagemChoiceAsync(UndoSyntheticChoiceSession session, UndoChoiceResultKey selectedKey)
    {
        UndoChoiceSpec choiceSpec = session.ChoiceSpec;
        if (!IsSourceChoice(choiceSpec, typeof(StratagemPower))
            || choiceSpec.Kind != UndoChoiceKind.SimpleGridSelection
            || choiceSpec.SourcePileType != PileType.Draw
            || selectedKey.OptionIndexes.Count == 0)
        {
            return false;
        }

        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        Player? player = combatState == null ? null : LocalContext.GetMe(combatState);
        if (player == null)
            return false;

        IReadOnlyList<CardModel> drawCards = PileType.Draw.GetPile(player).Cards;
        List<CardModel> selectedCards = [];
        foreach (int optionIndex in selectedKey.OptionIndexes)
        {
            if (optionIndex < 0 || optionIndex >= choiceSpec.SourcePileOptionIndexes.Count)
                return false;

            int drawIndex = choiceSpec.SourcePileOptionIndexes[optionIndex];
            if (drawIndex < 0 || drawIndex >= drawCards.Count)
                return false;

            selectedCards.Add(drawCards[drawIndex]);
        }

        foreach (CardModel selectedCard in selectedCards)
            await CardPileCmd.Add(selectedCard, PileType.Hand, CardPilePosition.Bottom, null, false);

        return true;
    }

    private static bool IsDiscardSelection(CardSelectorPrefs prefs)
    {
        return string.Equals(prefs.Prompt.LocTable, "card_selection", StringComparison.Ordinal)
            && string.Equals(prefs.Prompt.LocEntryKey, "TO_DISCARD", StringComparison.Ordinal);
    }

    private void RememberSavedChoiceBranches(UndoSyntheticChoiceSession session, IReadOnlyList<UndoChoiceBranchState> branchStates)
    {
        foreach (UndoChoiceBranchState branchState in branchStates)
            session.RememberBranch(branchState.ChoiceResultKey, MaterializeChoiceBranchSnapshot(branchState), preferAsTemplate: false);
    }

    private void TryPersistImmediateChoiceBranchSnapshot(UndoSnapshot snapshot)
    {
        if (_lastResolvedChoiceResultKey == null)
            return;

        LinkedListNode<UndoSnapshot>? anchorNode = _pastSnapshots.First?.Next;
        if (anchorNode?.Value.IsChoiceAnchor != true
            || !IsSupportedChoiceAnchorKind(anchorNode.Value.ChoiceSpec)
            || snapshot.ReplayEventCount < anchorNode.Value.ReplayEventCount)
        {
            return;
        }

        UndoSnapshot branchSnapshot = new(
            snapshot.CombatState,
            snapshot.ReplayEventCount,
            snapshot.ActionKind,
            snapshot.SequenceId,
            snapshot.ActionLabel,
            choiceResultKey: _lastResolvedChoiceResultKey);
        Dictionary<UndoChoiceResultKey, UndoChoiceBranchState> savedBranches = anchorNode.Value.CombatState.ChoiceBranchStates
            .ToDictionary(static branch => branch.ChoiceResultKey, static branch => branch);
        savedBranches[_lastResolvedChoiceResultKey] = CaptureChoiceBranchState(branchSnapshot);
        anchorNode.Value = new UndoSnapshot(
            WithChoiceBranchStates(anchorNode.Value.CombatState, [.. savedBranches.Values.OrderBy(static branch => branch.ReplayEventCount)]),
            anchorNode.Value.ReplayEventCount,
            anchorNode.Value.ActionKind,
            anchorNode.Value.SequenceId,
            anchorNode.Value.ActionLabel,
            isChoiceAnchor: true,
            choiceSpec: anchorNode.Value.ChoiceSpec);
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
            ActionKind = snapshot.ActionKind,
            ActionLabel = snapshot.ActionLabel
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
            choiceResultKey: branchState.ChoiceResultKey);
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
            source.CombatCardDbState,
            source.PlayerOrbStates,
            source.PlayerDeckStates,
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
