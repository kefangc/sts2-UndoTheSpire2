// 文件说明：承载 synthetic choice 的分支流转、会话切换和分支应用逻辑。
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
    private async Task<bool> TryApplySynthesizedChoiceBranchAsync(
        UndoSyntheticChoiceSession session,
        UndoSnapshot synthesizedBranch,
        UndoChoiceResultKey selectedKey,
        SyntheticChoiceVfxRequest? vfxRequest)
    {
        _syntheticChoiceSession = null;
        _futureSnapshots.Clear();

        RewriteReplayChoiceBranch(session.AnchorSnapshot, synthesizedBranch);
        if (!await TryApplyFullStateInPlaceAsync(synthesizedBranch.CombatState))
            throw new InvalidOperationException($"Failed to apply synthesized choice branch {selectedKey}.");

        synthesizedBranch = RefreshSynthesizedChoiceContinuationSnapshot(
            synthesizedBranch,
            session.TemplateSnapshot?.CombatState);

        _combatReplay!.ActiveEventCount = synthesizedBranch.ReplayEventCount;
        DisableReplayChecksumComparison(synthesizedBranch.CombatState.NextChecksumId);
        session.RememberBranch(selectedKey, synthesizedBranch);
        PersistChoiceAnchorBranches(session.AnchorSnapshot, CaptureSavedChoiceBranchStates(session));
        // 重新挂 choice anchor 时，手牌选择也必须保留原始锚点。
        // 像生存者这类 from-hand -> discard 的流程，如果把锚点挂到已提交的 synthesized branch 上，
        // 第二次 undo 回来的就会是“已经弃过牌”的状态，导致被弃的牌回不来，后续再选也会卡住。
        bool hasNestedChoiceContinuation = synthesizedBranch.ReplayEventCount > session.AnchorSnapshot.ReplayEventCount
            && IsSupportedChoiceAnchorKind(GetSnapshotChoiceSpec(synthesizedBranch));
        RememberResolvedChoiceBranch(
            session.ChoiceSpec,
            selectedKey,
            allowImmediateContinuation: hasNestedChoiceContinuation);
        if (hasNestedChoiceContinuation)
        {
            PreserveChoiceAnchorInPastHistory(
                session.AnchorSnapshot,
                WithChoiceBranchStates(session.AnchorSnapshot.CombatState, CaptureSavedChoiceBranchStates(session)));
        }

        bool activatedNestedChoice = synthesizedBranch.ReplayEventCount > session.AnchorSnapshot.ReplayEventCount
            && await TryActivateSynthesizedChoiceContinuationAsync(synthesizedBranch);
        if (!activatedNestedChoice)
        {
            bool preservePrimaryChoiceAnchor = session.ChoiceSpec.Kind is UndoChoiceKind.ChooseACard
                or UndoChoiceKind.SimpleGridSelection
                or UndoChoiceKind.HandSelection;
            UndoSnapshot anchorSnapshot = preservePrimaryChoiceAnchor ? session.AnchorSnapshot : synthesizedBranch;
            UndoCombatFullState? anchorCombatStateOverride = preservePrimaryChoiceAnchor
                ? WithChoiceBranchStates(session.AnchorSnapshot.CombatState, CaptureSavedChoiceBranchStates(session))
                : null;
            EnsurePlayerChoiceUndoAnchor(anchorSnapshot, session.ChoiceSpec, forceRefresh: true, anchorCombatStateOverride: anchorCombatStateOverride);
        }
        NotifyStateChanged();
        if (vfxRequest != null)
            _ = TaskHelper.RunSafely(PlaySyntheticChoiceVfxAsync(vfxRequest));

        return true;
    }

    private UndoSnapshot RefreshSynthesizedChoiceContinuationSnapshot(
        UndoSnapshot synthesizedBranch,
        UndoCombatFullState? templateCombatState)
    {
        UndoChoiceSpec? branchChoiceSpec = GetSnapshotChoiceSpec(synthesizedBranch);
        if (!IsSupportedChoiceAnchorKind(branchChoiceSpec))
            return synthesizedBranch;

        UndoChoiceSpec? refreshedChoiceSpec = TryRefreshSynthesizedChoiceSpecFromCurrentState(
            branchChoiceSpec!,
            templateCombatState ?? synthesizedBranch.CombatState);
        if (refreshedChoiceSpec == null || AreEquivalentChoiceSpecs(branchChoiceSpec, refreshedChoiceSpec))
            return synthesizedBranch;

        UndoCombatFullState refreshedCombatState = WithOverriddenChoiceSpec(
            synthesizedBranch.CombatState,
            refreshedChoiceSpec);
        UndoDebugLog.Write(
            $"synthesized_choice_spec_refreshed replayEvents={synthesizedBranch.ReplayEventCount}"
            + $" kind={refreshedChoiceSpec.Kind}"
            + $" source={refreshedChoiceSpec.SourceModelTypeName ?? "unknown"}"
            + $" eligible={refreshedChoiceSpec.SourcePileCombatCards.Count}");
        return new UndoSnapshot(
            refreshedCombatState,
            synthesizedBranch.ReplayEventCount,
            synthesizedBranch.ActionKind,
            synthesizedBranch.SequenceId,
            synthesizedBranch.ActionLabel,
            synthesizedBranch.IsChoiceAnchor,
            refreshedChoiceSpec,
            synthesizedBranch.ChoiceResultKey,
            synthesizedBranch.HistoryOrderReplayEventCount);
    }

    private UndoChoiceSpec? TryRefreshSynthesizedChoiceSpecFromCurrentState(
        UndoChoiceSpec templateChoiceSpec,
        UndoCombatFullState templateCombatState)
    {
        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        Player? me = combatState == null ? null : LocalContext.GetMe(combatState);
        if (me == null || templateChoiceSpec.SourcePileType == null)
            return null;

        return templateChoiceSpec.Kind switch
        {
            UndoChoiceKind.HandSelection => TryRefreshSynthesizedHandSelectionChoiceSpec(me, templateChoiceSpec, templateCombatState),
            UndoChoiceKind.SimpleGridSelection => TryRefreshSynthesizedSimpleGridChoiceSpec(me, templateChoiceSpec, templateCombatState),
            _ => null
        };
    }

    private UndoChoiceSpec? TryRefreshSynthesizedHandSelectionChoiceSpec(
        Player player,
        UndoChoiceSpec templateChoiceSpec,
        UndoCombatFullState templateCombatState)
    {
        if (templateChoiceSpec.SourcePileType != PileType.Hand)
            return null;

        AbstractModel? source = ResolveSyntheticHandChoiceSourceModel(player, templateChoiceSpec);
        if (DoesChoiceSpecCoverEntireSourcePile(templateChoiceSpec, templateCombatState, player.NetId))
            return UndoChoiceSpec.CreateHandSelection(player, templateChoiceSpec.SelectionPrefs, null, source);

        if (!TryResolveCurrentChoiceEligibleCards(player, templateChoiceSpec, out List<CardModel>? eligibleCards))
            return null;

        List<CardModel> resolvedEligibleCards = eligibleCards!;
        HashSet<CardModel> eligibleSet = [.. resolvedEligibleCards];
        return UndoChoiceSpec.CreateHandSelection(
            player,
            templateChoiceSpec.SelectionPrefs,
            card => eligibleSet.Contains(card),
            source);
    }

    private UndoChoiceSpec? TryRefreshSynthesizedSimpleGridChoiceSpec(
        Player player,
        UndoChoiceSpec templateChoiceSpec,
        UndoCombatFullState templateCombatState)
    {
        if (templateChoiceSpec.SourcePileType is not PileType sourcePileType)
            return null;

        AbstractModel? source = ResolveSyntheticHandChoiceSourceModel(player, templateChoiceSpec);
        IReadOnlyList<CardModel> sourceCards = sourcePileType.GetPile(player).Cards;
        if (DoesChoiceSpecCoverEntireSourcePile(templateChoiceSpec, templateCombatState, player.NetId))
            return UndoChoiceSpec.CreateSimpleGridSelection(player, sourceCards.ToList(), templateChoiceSpec.SelectionPrefs, source);

        if (!TryResolveCurrentChoiceEligibleCards(player, templateChoiceSpec, out List<CardModel>? eligibleCards))
            return null;

        List<CardModel> resolvedEligibleCards = eligibleCards!;
        return UndoChoiceSpec.CreateSimpleGridSelection(player, resolvedEligibleCards, templateChoiceSpec.SelectionPrefs, source);
    }

    private static bool TryResolveCurrentChoiceEligibleCards(
        Player player,
        UndoChoiceSpec choiceSpec,
        out List<CardModel>? eligibleCards)
    {
        eligibleCards = null;
        if (choiceSpec.SourcePileType is not PileType sourcePileType)
            return false;

        IReadOnlyList<CardModel> sourceCards = sourcePileType.GetPile(player).Cards;
        if (choiceSpec.SourcePileCombatCards.Count > 0)
        {
            bool[] usedSourceIndexes = new bool[sourceCards.Count];
            List<CardModel> remappedCards = [];
            foreach (NetCombatCard combatCard in choiceSpec.SourcePileCombatCards)
            {
                int matchedIndex = -1;
                for (int i = 0; i < sourceCards.Count; i++)
                {
                    if (usedSourceIndexes[i] || !combatCard.Equals(NetCombatCard.FromModel(sourceCards[i])))
                        continue;

                    matchedIndex = i;
                    break;
                }

                if (matchedIndex < 0)
                    return false;

                usedSourceIndexes[matchedIndex] = true;
                remappedCards.Add(sourceCards[matchedIndex]);
            }

            eligibleCards = remappedCards;
            return true;
        }

        List<CardModel> mappedByIndex = [];
        foreach (int sourceIndex in choiceSpec.SourcePileOptionIndexes)
        {
            if (sourceIndex < 0 || sourceIndex >= sourceCards.Count)
                return false;

            mappedByIndex.Add(sourceCards[sourceIndex]);
        }

        eligibleCards = mappedByIndex;
        return mappedByIndex.Count > 0;
    }

    private static bool DoesChoiceSpecCoverEntireSourcePile(
        UndoChoiceSpec choiceSpec,
        UndoCombatFullState templateCombatState,
        ulong playerNetId)
    {
        if (choiceSpec.SourcePileType is not PileType sourcePileType)
            return false;

        int sourcePileCount = GetSourcePileCardCount(templateCombatState.FullState, playerNetId, sourcePileType);
        if (sourcePileCount <= 0
            || choiceSpec.SourcePileOptionIndexes.Count != sourcePileCount
            || choiceSpec.SourcePileCombatCards.Count != sourcePileCount)
        {
            return false;
        }

        bool[] seenIndexes = new bool[sourcePileCount];
        foreach (int sourceIndex in choiceSpec.SourcePileOptionIndexes)
        {
            if (sourceIndex < 0 || sourceIndex >= sourcePileCount || seenIndexes[sourceIndex])
                return false;

            seenIndexes[sourceIndex] = true;
        }

        return seenIndexes.All(static seen => seen);
    }

    private static int GetSourcePileCardCount(NetFullCombatState fullState, ulong playerNetId, PileType pileType)
    {
        foreach (NetFullCombatState.PlayerState playerState in fullState.Players)
        {
            if (playerState.playerId != playerNetId)
                continue;

            int pileIndex = FindPileIndex(playerState.piles, pileType);
            if (pileIndex < 0)
                return -1;

            return playerState.piles[pileIndex].cards.Count;
        }

        return -1;
    }

    private static UndoCombatFullState WithOverriddenChoiceSpec(
        UndoCombatFullState source,
        UndoChoiceSpec choiceSpec)
    {
        PausedChoiceState? pausedChoiceState = source.ActionKernelState.PausedChoiceState == null
            ? null
            : new PausedChoiceState
            {
                ChoiceKind = source.ActionKernelState.PausedChoiceState.ChoiceKind,
                OwnerNetId = source.ActionKernelState.PausedChoiceState.OwnerNetId,
                ChoiceId = source.ActionKernelState.PausedChoiceState.ChoiceId,
                Prompt = source.ActionKernelState.PausedChoiceState.Prompt,
                MinSelections = source.ActionKernelState.PausedChoiceState.MinSelections,
                MaxSelections = source.ActionKernelState.PausedChoiceState.MaxSelections,
                CandidateCardRefs = source.ActionKernelState.PausedChoiceState.CandidateCardRefs,
                PreselectedCardRefs = source.ActionKernelState.PausedChoiceState.PreselectedCardRefs,
                SourceActionRef = source.ActionKernelState.PausedChoiceState.SourceActionRef,
                SourceActionCodecId = source.ActionKernelState.PausedChoiceState.SourceActionCodecId,
                SourceActionPayload = source.ActionKernelState.PausedChoiceState.SourceActionPayload,
                ResumeActionId = source.ActionKernelState.PausedChoiceState.ResumeActionId,
                ResumeToken = source.ActionKernelState.PausedChoiceState.ResumeToken,
                ChoiceSpec = choiceSpec
            };
        ActionKernelState actionKernelState = new()
        {
            SchemaVersion = source.ActionKernelState.SchemaVersion,
            BoundaryKind = source.ActionKernelState.BoundaryKind,
            CurrentActionTypeName = source.ActionKernelState.CurrentActionTypeName,
            CurrentActionState = source.ActionKernelState.CurrentActionState,
            CurrentActionRef = source.ActionKernelState.CurrentActionRef,
            CurrentActionCodecId = source.ActionKernelState.CurrentActionCodecId,
            CurrentActionPayload = source.ActionKernelState.CurrentActionPayload,
            CurrentHookActionRef = source.ActionKernelState.CurrentHookActionRef,
            PausedChoiceState = pausedChoiceState,
            Queues = source.ActionKernelState.Queues,
            WaitingForResumptionCount = source.ActionKernelState.WaitingForResumptionCount,
            WaitingForResumption = source.ActionKernelState.WaitingForResumption
        };
        UndoSelectionSessionState? selectionSessionState = source.SelectionSessionState == null
            ? null
            : new UndoSelectionSessionState
            {
                HandSelectionActive = source.SelectionSessionState.HandSelectionActive,
                OverlaySelectionActive = source.SelectionSessionState.OverlaySelectionActive,
                SupportedChoiceUiActive = source.SelectionSessionState.SupportedChoiceUiActive,
                OverlayScreenType = source.SelectionSessionState.OverlayScreenType,
                ChoiceSpec = choiceSpec
            };
        return new UndoCombatFullState(
            source.FullState,
            source.RoundNumber,
            source.CurrentSide,
            source.SynchronizerCombatState,
            source.NextActionId,
            source.NextHookId,
            source.NextChecksumId,
            source.CombatHistoryState,
            actionKernelState,
            source.MonsterStates,
            source.CardCostStates,
            source.CardRuntimeStates,
            source.PowerRuntimeStates,
            source.RelicRuntimeStates,
            selectionSessionState,
            source.FirstInSeriesPlayCounts,
            source.RuntimeGraphState,
            new PresentationHints
            {
                SelectionSessionState = selectionSessionState
            },
            source.CreatureTopologyStates,
            source.CreatureStatusRuntimeStates,
            source.CreatureVisualStates,
            source.CombatCardDbState,
            source.PlayerOrbStates,
            source.PlayerDeckStates,
            source.PlayerPotionStates,
            source.AudioLoopStates,
            source.SchemaVersion,
            source.ChoiceBranchStates,
            source.PendingCombatRewardStates);
    }

    private void PreserveChoiceAnchorInPastHistory(UndoSnapshot anchorSnapshot, UndoCombatFullState? combatStateOverride = null)
    {
        if (!anchorSnapshot.IsChoiceAnchor || !IsSupportedChoiceAnchorKind(anchorSnapshot.ChoiceSpec))
            return;

        for (LinkedListNode<UndoSnapshot>? node = _pastSnapshots.First; node != null;)
        {
            LinkedListNode<UndoSnapshot>? next = node.Next;
            if (node.Value.IsChoiceAnchor
                && node.Value.ReplayEventCount == anchorSnapshot.ReplayEventCount
                && string.Equals(node.Value.ActionLabel, anchorSnapshot.ActionLabel, StringComparison.Ordinal)
                && AreEquivalentChoiceSpecs(node.Value.ChoiceSpec, anchorSnapshot.ChoiceSpec))
            {
                _pastSnapshots.Remove(node);
            }

            node = next;
        }

        UndoSnapshot preservedAnchor = new(
            combatStateOverride ?? anchorSnapshot.CombatState,
            anchorSnapshot.ReplayEventCount,
            UndoActionKind.PlayerChoice,
            _nextSequenceId++,
            anchorSnapshot.ActionLabel,
            isChoiceAnchor: true,
            choiceSpec: anchorSnapshot.ChoiceSpec,
            historyOrderReplayEventCount: anchorSnapshot.HistoryOrderReplayEventCount);
        _pastSnapshots.AddFirst(preservedAnchor);
        TrimSnapshots(_pastSnapshots);
    }

    private async Task<bool> TryActivateSynthesizedChoiceContinuationAsync(UndoSnapshot synthesizedBranch)
    {
        UndoChoiceSpec? activeChoiceSpec = GetSnapshotChoiceSpec(synthesizedBranch);
        if (!IsSupportedChoiceAnchorKind(activeChoiceSpec))
            return false;

        UndoSnapshot activeChoiceSnapshot = synthesizedBranch.IsChoiceAnchor
            && AreEquivalentChoiceSpecs(synthesizedBranch.ChoiceSpec, activeChoiceSpec)
            ? synthesizedBranch
            : new UndoSnapshot(
                synthesizedBranch.CombatState,
                synthesizedBranch.ReplayEventCount,
                UndoActionKind.PlayerChoice,
                synthesizedBranch.SequenceId,
                synthesizedBranch.ActionLabel,
                isChoiceAnchor: true,
                choiceSpec: activeChoiceSpec,
                historyOrderReplayEventCount: synthesizedBranch.HistoryOrderReplayEventCount);

        if (UndoModSettings.EnableUnifiedEffectMode
            && await TryRestorePrimaryChoiceAsync(activeChoiceSnapshot, null, stateAlreadyApplied: true))
        {
            return true;
        }

        if (!UndoModSettings.EnableChoiceUndo || activeChoiceSpec?.SupportsSyntheticRestore != true)
            return false;

        await WaitOneFrameAsync();
        OpenSyntheticChoiceSession(activeChoiceSnapshot, null);
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
        _syntheticChoiceSession = new UndoSyntheticChoiceSession(
            anchorSnapshot,
            anchorSnapshot.ChoiceSpec!,
            branchSnapshot,
            requiresAuthoritativeBranchExecution: ShouldRequireAuthoritativeSyntheticChoiceExecution(anchorSnapshot.ChoiceSpec!));
        UndoSyntheticChoiceSession session = _syntheticChoiceSession;
        RememberSavedChoiceBranches(session, anchorSnapshot.CombatState.ChoiceBranchStates);
        if (branchSnapshot?.ChoiceResultKey != null)
            session.RememberBranch(branchSnapshot.ChoiceResultKey, branchSnapshot);

        StartChoiceSelectionOperation(
            "synthetic_choice_selection",
            lease => HandleSyntheticChoiceSelectionAsync(lease, session));
    }

    private async Task HandleSyntheticChoiceSelectionAsync(UndoOperationLease lease, UndoSyntheticChoiceSession session)
    {
        try
        {
            UndoChoiceResultKey? selectedKey = await ShowSyntheticChoiceSelectionAsync(session);
            if (ShouldAbortTrackedOperation(lease, "synthetic_choice_selection_completed")
                || _syntheticChoiceSession != session
                || selectedKey == null)
                return;

            if (session.RequiresAuthoritativeBranchExecution)
            {
                if (await TryCommitCustomChoiceBranchAsync(session, selectedKey))
                {
                    WriteInteractionLog(
                        "branch_commit_after_reselect",
                        $"choice={selectedKey} label={session.AnchorSnapshot.ActionLabel} replayEvents={session.AnchorSnapshot.ReplayEventCount} source=custom");
                    return;
                }

                UndoDebugLog.Write(
                    $"authoritative_choice_branch_unavailable choice={selectedKey}"
                    + $" label={session.AnchorSnapshot.ActionLabel}"
                    + $" replayEvents={session.AnchorSnapshot.ReplayEventCount}"
                    + $" source={session.ChoiceSpec.SourceModelTypeName ?? "unknown"}");
                OpenSyntheticChoiceSession(session.AnchorSnapshot, session.TemplateSnapshot ?? _futureSnapshots.First?.Value);
                return;
            }

            if (session.CachedBranches.TryGetValue(selectedKey, out UndoSnapshot? cachedBranchSnapshot))
            {
                if (ReferenceEquals(_futureSnapshots.First?.Value, cachedBranchSnapshot))
                {
                    _syntheticChoiceSession = null;
                    RememberResolvedChoiceBranch(session.ChoiceSpec, selectedKey, allowImmediateContinuation: false);
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
        await StabilizeAfterFlowAdvancingChoiceExecutionAsync();
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
            templateSnapshot.IsChoiceAnchor,
            templateSnapshot.ChoiceSpec,
            selectedKey,
            templateSnapshot.HistoryOrderReplayEventCount);
        return true;
    }
}
