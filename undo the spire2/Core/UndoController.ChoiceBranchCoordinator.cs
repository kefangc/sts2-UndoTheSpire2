using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace UndoTheSpire2;

public sealed partial class UndoController
{
    private enum ChoiceBranchCompletionMode
    {
        LiveResume,
        CustomExecution
    }

    private async Task FinalizeCustomChoiceBranchAsync(UndoSyntheticChoiceSession session, UndoChoiceResultKey selectedKey, bool stabilizeQueues = true)
    {
        await FinalizeChoiceBranchAsync(session, selectedKey, ChoiceBranchCompletionMode.CustomExecution, stabilizeQueues);
    }

    private async Task FinalizeChoiceBranchAsync(
        UndoSyntheticChoiceSession session,
        UndoChoiceResultKey selectedKey,
        ChoiceBranchCompletionMode completionMode,
        bool stabilizeQueues)
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
            officialHandChoiceUiSettled = IsCompletedHandChoiceUiSettleResult(
                await WaitForOfficialHandChoiceUiSettleAsync(hand, livePlayer));
            if (!officialHandChoiceUiSettled && hand != null && TryCompletePendingHandDiscardChoiceUiViaOfficialPath(hand))
            {
                await WaitOneFrameAsync();
                hand = NCombatRoom.Instance?.Ui?.Hand ?? hand;
                officialHandChoiceUiSettled = IsCompletedHandChoiceUiSettleResult(
                    await WaitForOfficialHandChoiceUiSettleAsync(hand, livePlayer, maxFrames: 60));
            }

            if (!officialHandChoiceUiSettled && liveCombatState != null)
            {
                RecoverHandDiscardChoiceUiIfNeeded(liveCombatState);
                hand = NCombatRoom.Instance?.Ui?.Hand ?? hand;
                officialHandChoiceUiSettled = IsCompletedHandChoiceUiSettleResult(
                    await WaitForOfficialHandChoiceUiSettleAsync(hand, livePlayer, maxFrames: 30));
            }

            if (!officialHandChoiceUiSettled && hand != null)
            {
                TryCompletePendingHandChoiceUiInstantly(hand, livePlayer);
                await WaitOneFrameAsync();
                hand = NCombatRoom.Instance?.Ui?.Hand ?? hand;
                officialHandChoiceUiSettled = IsCompletedHandChoiceUiSettleResult(
                    await WaitForOfficialHandChoiceUiSettleAsync(hand, livePlayer, maxFrames: 15));
            }

            if (!officialHandChoiceUiSettled && hand != null && livePlayer != null)
            {
                bool forcedSynchronized = TrySyncExistingHandUi(hand, livePlayer, normalizeLayout: true);
                if (forcedSynchronized)
                {
                    FinalizePendingHandChoiceAfterForcedSync(hand, livePlayer);
                    officialHandChoiceUiSettled = true;
                }
            }
        }
        else
        {
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

        UndoDebugLog.Write(
            $"choice_branch_finalize mode={completionMode}"
            + $" choice={selectedKey}"
            + $" replayEvents={session.AnchorSnapshot.ReplayEventCount}->{branchSnapshot.ReplayEventCount}"
            + $" source={session.ChoiceSpec.SourceModelTypeName ?? "unknown"}");
        NotifyStateChanged();
        await WaitOneFrameAsync();
    }
}
