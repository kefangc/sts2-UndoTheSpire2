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
        _lastResolvedChoiceSpec = session.ChoiceSpec;
        _lastResolvedChoiceResultKey = selectedKey;
        session.RememberBranch(selectedKey, synthesizedBranch);
        _futureSnapshots.Clear();

        RewriteReplayChoiceBranch(session.AnchorSnapshot, synthesizedBranch);
        if (!await TryApplyFullStateInPlaceAsync(synthesizedBranch.CombatState))
            throw new InvalidOperationException($"Failed to apply synthesized choice branch {selectedKey}.");

        _combatReplay!.ActiveEventCount = synthesizedBranch.ReplayEventCount;
        DisableReplayChecksumComparison(synthesizedBranch.CombatState.NextChecksumId);
        bool preservePrimaryChoiceAnchor = session.ChoiceSpec.Kind is UndoChoiceKind.ChooseACard or UndoChoiceKind.SimpleGridSelection;
        UndoSnapshot anchorSnapshot = preservePrimaryChoiceAnchor ? session.AnchorSnapshot : synthesizedBranch;
        UndoCombatFullState? anchorCombatStateOverride = preservePrimaryChoiceAnchor
            ? WithChoiceBranchStates(session.AnchorSnapshot.CombatState, CaptureSavedChoiceBranchStates(session))
            : null;
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
        RememberSavedChoiceBranches(_syntheticChoiceSession, anchorSnapshot.CombatState.ChoiceBranchStates);
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
}
