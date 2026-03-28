// 文件说明：实现 paused choice 等官方 action 的恢复 codec。
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;

namespace UndoTheSpire2;

internal static class UndoActionCodecRegistry
{
    private static readonly IReadOnlyList<IUndoActionCodec<PausedChoiceState>> Codecs =
    [
        new FromHandPausedChoiceCodec(),
        new ChooseACardPausedChoiceCodec(),
        new SimpleGridPausedChoiceCodec()
    ];

    public static IReadOnlyCollection<string> GetImplementedCodecIds()
    {
        HashSet<string> ids = Codecs.Select(static codec => codec.CodecId).ToHashSet(StringComparer.Ordinal);
        ids.Add("action:hook-choice");
        ids.Add("action:WellLaidPlans.choice");
        return ids;
    }

    public static RestoreCapabilityReport EvaluateCapability(PausedChoiceState? state)
    {
        if (state?.ChoiceSpec == null)
        {
            return new RestoreCapabilityReport
            {
                Result = RestoreCapabilityResult.UnsupportedOfficialPattern,
                Detail = "paused_choice_missing_spec"
            };
        }

        if (state.SourceActionRef?.ActionId == null)
        {
            return new RestoreCapabilityReport
            {
                Result = RestoreCapabilityResult.UnsupportedOfficialPattern,
                Detail = "paused_choice_missing_action_id"
            };
        }

        if (state.OwnerNetId == null)
        {
            return new RestoreCapabilityReport
            {
                Result = RestoreCapabilityResult.UnsupportedOfficialPattern,
                Detail = "paused_choice_missing_owner"
            };
        }

        if (state.ChoiceId == null)
        {
            if (state.ChoiceKind == UndoChoiceKind.ChooseACard && TryGetCodec(state) != null)
                return RestoreCapabilityReport.SupportedReport("paused_choice_primary_anchor_reopen");

            return new RestoreCapabilityReport
            {
                Result = RestoreCapabilityResult.FallbackToSyntheticChoice,
                Detail = "paused_choice_missing_choice_id"
            };
        }

        if (TryGetCodec(state) != null)
            return RestoreCapabilityReport.SupportedReport("paused_choice_primary");

        return new RestoreCapabilityReport
        {
            Result = RestoreCapabilityResult.FallbackToSyntheticChoice,
            Detail = state.SourceActionCodecId ?? state.ChoiceKind.ToString()
        };
    }

    public static async Task<UndoChoiceResultKey?> RestoreAsync(PausedChoiceState state, RunState runState)
    {
        IUndoActionCodec<PausedChoiceState>? codec = TryGetCodec(state);
        if (codec == null)
            return null;

        return await codec.RestoreAsync(state, runState);
    }

    private static IUndoActionCodec<PausedChoiceState>? TryGetCodec(PausedChoiceState state)
    {
        return Codecs.FirstOrDefault(codec => codec.CanHandle(state));
    }

    private static Player? GetOwner(RunState runState, PausedChoiceState state)
    {
        return state.OwnerNetId == null ? null : runState.GetPlayer(state.OwnerNetId.Value);
    }

    private static uint? GetChoiceId(RunState runState, PausedChoiceState state, Player player)
    {
        if (state.ChoiceId != null)
            return state.ChoiceId.Value;

        int slot = runState.GetPlayerSlotIndex(player);
        IReadOnlyList<uint> choiceIds = RunManager.Instance.PlayerChoiceSynchronizer.ChoiceIds;
        if (slot < 0 || slot >= choiceIds.Count || choiceIds[slot] == 0)
            return null;

        return choiceIds[slot] - 1;
    }

    private static UndoChoiceResultKey? MapAndSyncHandSelection(RunState runState, PausedChoiceState state, Player player, IEnumerable<CardModel> selectedCards)
    {
        List<CardModel> selected = [.. selectedCards];
        UndoChoiceResultKey? key = state.ChoiceSpec?.TryMapSyntheticSelection(player, selected);
        uint? choiceId = GetChoiceId(runState, state, player);
        if (choiceId != null)
            RunManager.Instance.PlayerChoiceSynchronizer.SyncLocalChoice(player, choiceId.Value, PlayerChoiceResult.FromMutableCombatCards(selected));
        TryResumeRestoredChoiceSourceAction(state);

        return key;
    }

    private static UndoChoiceResultKey? MapAndSyncChooseACard(RunState runState, PausedChoiceState state, Player player, IReadOnlyList<CardModel> options, CardModel? selectedCard)
    {
        UndoChoiceResultKey? key = state.ChoiceSpec?.TryMapDisplayedOptionSelection(options, selectedCard == null ? [] : [selectedCard]);
        int selectedIndex = selectedCard == null ? -1 : IndexOfReference(options, selectedCard);
        uint? choiceId = GetChoiceId(runState, state, player);
        if (choiceId != null)
            RunManager.Instance.PlayerChoiceSynchronizer.SyncLocalChoice(player, choiceId.Value, PlayerChoiceResult.FromIndex(selectedIndex));
        TryResumeRestoredChoiceSourceAction(state);

        return key;
    }

    private static UndoChoiceResultKey? MapAndSyncSimpleGrid(RunState runState, PausedChoiceState state, Player player, IReadOnlyList<CardModel> options, IEnumerable<CardModel> selectedCards)
    {
        List<CardModel> selected = [.. selectedCards];
        List<int> indexes = selected.Select(card => IndexOfReference(options, card)).Where(static index => index >= 0).ToList();
        UndoChoiceResultKey? key = state.ChoiceSpec?.TryMapDisplayedOptionSelection(options, selected);
        uint? choiceId = GetChoiceId(runState, state, player);
        if (choiceId != null)
            RunManager.Instance.PlayerChoiceSynchronizer.SyncLocalChoice(player, choiceId.Value, PlayerChoiceResult.FromIndexes(indexes));
        TryResumeRestoredChoiceSourceAction(state);

        return key;
    }

    private static void TryResumeRestoredChoiceSourceAction(PausedChoiceState state)
    {
        uint? sourceActionId = state.SourceActionRef?.ActionId;
        if (sourceActionId == null
            || string.Equals(state.SourceActionCodecId, "action:hook-choice", StringComparison.Ordinal))
        {
            return;
        }

        GameAction? sourceAction = FindTrackedAction(sourceActionId.Value);
        if (sourceAction == null)
        {
            UndoDebugLog.Write(
                $"restored_choice_resume_skipped codec={state.SourceActionCodecId ?? "unknown"}"
                + $" sourceActionId={sourceActionId.Value}"
                + " reason=source_action_missing");
            return;
        }

        if (sourceAction.State != GameActionState.GatheringPlayerChoice)
        {
            UndoDebugLog.Write(
                $"restored_choice_resume_skipped codec={state.SourceActionCodecId ?? "unknown"}"
                + $" sourceActionId={sourceActionId.Value}"
                + $" reason=state_{sourceAction.State}");
            return;
        }

        if (!HasLivePlayerChoiceResumptionState(sourceAction, out string? reason))
        {
            UndoDebugLog.Write(
                $"restored_choice_resume_skipped codec={state.SourceActionCodecId ?? "unknown"}"
                + $" sourceActionId={sourceActionId.Value}"
                + $" reason={reason ?? "missing_live_choice_state"}");
            return;
        }

        RunManager.Instance.ActionQueueSynchronizer.RequestResumeActionAfterPlayerChoice(sourceAction);
        UndoDebugLog.Write(
            $"restored_choice_resume_requested codec={state.SourceActionCodecId ?? "unknown"}"
            + $" sourceActionId={sourceActionId.Value}");
    }

    private static GameAction? FindTrackedAction(uint actionId)
    {
        if (RunManager.Instance.ActionExecutor.CurrentlyRunningAction?.Id == actionId)
            return RunManager.Instance.ActionExecutor.CurrentlyRunningAction;

        if (UndoReflectionUtil.FindField(RunManager.Instance.ActionQueueSet.GetType(), "_actionQueues")?.GetValue(RunManager.Instance.ActionQueueSet) is not System.Collections.IEnumerable rawQueues)
            return null;

        foreach (object rawQueue in rawQueues)
        {
            if (UndoReflectionUtil.FindField(rawQueue.GetType(), "actions")?.GetValue(rawQueue) is not System.Collections.IEnumerable rawActions)
                continue;

            foreach (GameAction action in rawActions.OfType<GameAction>())
            {
                if (action.Id == actionId)
                    return action;
            }
        }

        return null;
    }

    private static bool HasLivePlayerChoiceResumptionState(GameAction action, out string? reason)
    {
        reason = null;
        if (action.State != GameActionState.GatheringPlayerChoice)
        {
            reason = $"state_{action.State}";
            return false;
        }

        if (UndoReflectionUtil.FindField(action.GetType(), "_executeAfterResumptionTaskSource")?.GetValue(action) == null)
        {
            reason = "missing_resume_task_source";
            return false;
        }

        object? executionTaskObject = UndoReflectionUtil.FindField(action.GetType(), "_executionTask")?.GetValue(action);
        if (executionTaskObject is not Task executionTask
            || executionTask.IsCompleted)
        {
            reason = executionTaskObject == null ? "missing_execution_task" : "execution_task_completed";
            return false;
        }

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

    private static IEnumerable<AbstractModel> EnumeratePotentialChoiceSourceModels(Player player)
    {
        if (player.PlayerCombatState != null)
        {
            foreach (AbstractModel model in player.PlayerCombatState.AllCards.OfType<AbstractModel>())
                yield return model;
        }

        foreach (AbstractModel model in player.Relics.OfType<AbstractModel>())
            yield return model;

        foreach (AbstractModel model in player.Creature.Powers.OfType<AbstractModel>())
            yield return model;

        for (int slotIndex = 0; slotIndex < player.MaxPotionCount; slotIndex++)
        {
            if (player.GetPotionAtSlotIndex(slotIndex) is AbstractModel potion)
                yield return potion;
        }
    }

    private static AbstractModel? ResolveChoiceSourceModel(Player player, UndoChoiceSpec choiceSpec)
    {
        CardModel? sourceCard = ResolveSourceCardFromPlayPile(player, choiceSpec);
        if (sourceCard != null)
            return sourceCard;

        if (string.IsNullOrWhiteSpace(choiceSpec.SourceModelTypeName))
            return null;

        foreach (AbstractModel model in EnumeratePotentialChoiceSourceModels(player))
        {
            if (!string.Equals(model.GetType().FullName, choiceSpec.SourceModelTypeName, StringComparison.Ordinal))
                continue;

            if (!string.IsNullOrWhiteSpace(choiceSpec.SourceModelId)
                && !string.Equals(model.Id.Entry, choiceSpec.SourceModelId, StringComparison.Ordinal))
            {
                continue;
            }

            return model;
        }

        return null;
    }

    private static IReadOnlyList<CardModel> ResolveChooseACardOptions(Player player, PausedChoiceState state)
    {
        if (NOverlayStack.Instance?.Peek() is NChooseACardSelectionScreen screen
            && UndoReflectionUtil.FindField(screen.GetType(), "_cards")?.GetValue(screen) is IReadOnlyList<CardModel> displayedOptions
            && displayedOptions.Count > 0)
        {
            return displayedOptions;
        }

        return state.ChoiceSpec?.BuildOptionCards(player) ?? [];
    }

    private static IReadOnlyList<CardModel> ResolveSimpleGridOptions(Player player, PausedChoiceState state)
    {
        if (NOverlayStack.Instance?.Peek() is NCardGridSelectionScreen screen
            && UndoReflectionUtil.FindField(screen.GetType(), "_cards")?.GetValue(screen) is IReadOnlyList<CardModel> displayedOptions
            && displayedOptions.Count > 0)
        {
            return displayedOptions;
        }

        return state.ChoiceSpec?.BuildOptionCards(player) ?? [];
    }

    private static bool IsDiscardSelection(CardSelectorPrefs prefs)
    {
        return string.Equals(prefs.Prompt.LocTable, "card_selection", StringComparison.Ordinal)
            && string.Equals(prefs.Prompt.LocEntryKey, "TO_DISCARD", StringComparison.Ordinal);
    }

    private static bool IsSourceChoice(UndoChoiceSpec choiceSpec, Type sourceType)
    {
        return string.Equals(choiceSpec.SourceModelTypeName, sourceType.FullName, StringComparison.Ordinal);
    }

    private static bool IsOfficialFromHandDiscardChoice(UndoChoiceSpec choiceSpec)
    {
        return choiceSpec.Kind == UndoChoiceKind.HandSelection
            && choiceSpec.SourcePileType == PileType.Hand
            && IsDiscardSelection(choiceSpec.SelectionPrefs)
            && (
                IsSourceChoice(choiceSpec, typeof(Acrobatics))
                || IsSourceChoice(choiceSpec, typeof(DaggerThrow))
                || IsSourceChoice(choiceSpec, typeof(HiddenDaggers))
                || IsSourceChoice(choiceSpec, typeof(Prepared))
                || IsSourceChoice(choiceSpec, typeof(Survivor))
                || IsSourceChoice(choiceSpec, typeof(GamblingChip))
                || IsSourceChoice(choiceSpec, typeof(GamblersBrew))
                || IsSourceChoice(choiceSpec, typeof(ToolsOfTheTradePower)));
    }

    private static bool IsTrackedOfficialFromHandDiscardChoice(PausedChoiceState state, UndoChoiceSpec choiceSpec)
    {
        return state.SourceActionRef?.ActionId != null
            && string.Equals(state.SourceActionCodecId, "action:from-hand", StringComparison.Ordinal)
            && IsOfficialFromHandDiscardChoice(choiceSpec);
    }

    private static Task<IEnumerable<CardModel>>? TryAwaitExistingHandSelection(NPlayerHand hand, Player player)
    {
        if (!hand.IsInCardSelection)
            return null;

        if (UndoReflectionUtil.FindField(hand.GetType(), "_selectionCompletionSource")?.GetValue(hand) is not TaskCompletionSource<IEnumerable<CardModel>> completionSource)
        {
            MainFile.Controller.ResetPendingHandChoiceUiForRestore(hand, player);
            return null;
        }

        Task<IEnumerable<CardModel>> task = completionSource.Task;
        if (task.IsCompleted)
        {
            MainFile.Controller.ResetPendingHandChoiceUiForRestore(hand, player);
            return null;
        }

        MainFile.Controller.PrepareHandSelectionUiForOpen(hand);
        return task;
    }

    private static void PrepareOfficialLiveHandSelectionRestore(NPlayerHand hand, Player player, UndoChoiceSpec choiceSpec)
    {
        bool wasInCardSelection = hand.IsInCardSelection;
        TaskCompletionSource<IEnumerable<CardModel>>? selectionCompletionSource =
            UndoReflectionUtil.FindField(hand.GetType(), "_selectionCompletionSource")?.GetValue(hand) as TaskCompletionSource<IEnumerable<CardModel>>;
        bool hasSelectedCards = UndoReflectionUtil.FindField(hand.GetType(), "_selectedCards")?.GetValue(hand) is System.Collections.IList selectedCards
            && selectedCards.Count > 0;
        bool needsFreshRestore = wasInCardSelection || selectionCompletionSource != null || hasSelectedCards;
        if (!needsFreshRestore)
            return;

        MainFile.Controller.ResetPendingHandChoiceUiForRestore(hand, player, detachPendingSource: false, clearPendingTracking: false);
        UndoDebugLog.Write(
            $"official_hand_choice_live_restore_reopen source={choiceSpec.SourceModelTypeName ?? "unknown"}"
            + $" hadSelection={wasInCardSelection}"
            + $" hadCompletionSource={(selectionCompletionSource != null)}"
            + $" hadSelectedCards={hasSelectedCards}");
    }

    private static Task<IEnumerable<CardModel>>? TryAwaitExistingChooseACardSelection()
    {
        if (NOverlayStack.Instance?.Peek() is not NChooseACardSelectionScreen screen)
            return null;

        if (UndoReflectionUtil.FindField(screen.GetType(), "_completionSource")?.GetValue(screen) is not TaskCompletionSource<IEnumerable<CardModel>> completionSource)
            return null;

        Task<IEnumerable<CardModel>> task = completionSource.Task;
        if (task.IsCompleted)
        {
            NOverlayStack.Instance?.Remove(screen);
            return null;
        }

        return task;
    }

    private static Task<IEnumerable<CardModel>>? TryAwaitExistingSimpleGridSelection()
    {
        if (NOverlayStack.Instance?.Peek() is not NCardGridSelectionScreen screen)
            return null;

        if (UndoReflectionUtil.FindField(screen.GetType(), "_completionSource")?.GetValue(screen) is not TaskCompletionSource<IEnumerable<CardModel>> completionSource)
            return null;

        Task<IEnumerable<CardModel>> task = completionSource.Task;
        if (task.IsCompleted)
        {
            NOverlayStack.Instance?.Remove(screen);
            return null;
        }

        return task;
    }

    private static int IndexOfReference(IReadOnlyList<CardModel> cards, CardModel target)
    {
        for (int i = 0; i < cards.Count; i++)
        {
            if (ReferenceEquals(cards[i], target))
                return i;
        }

        return -1;
    }

    private sealed class FromHandPausedChoiceCodec : IUndoActionCodec<PausedChoiceState>
    {
        public string CodecId => "action:from-hand";

        public bool CanHandle(PausedChoiceState state)
        {
            return state.ChoiceKind == UndoChoiceKind.HandSelection && state.ChoiceSpec != null;
        }

        public async Task<UndoChoiceResultKey?> RestoreAsync(PausedChoiceState state, RunState runState)
        {
            Player? player = GetOwner(runState, state);
            NPlayerHand? hand = NPlayerHand.Instance;
            if (player == null || hand == null || state.ChoiceSpec == null)
                return null;

            AbstractModel? source = ResolveChoiceSourceModel(player, state.ChoiceSpec);
            bool requiresOfficialLiveRestore = IsTrackedOfficialFromHandDiscardChoice(state, state.ChoiceSpec);
            if (requiresOfficialLiveRestore && source == null)
            {
                UndoDebugLog.Write(
                    $"official_hand_choice_live_restore_failed source={state.ChoiceSpec.SourceModelTypeName ?? "unknown"}"
                    + " reason=source_missing");
                return null;
            }

            if (requiresOfficialLiveRestore)
                PrepareOfficialLiveHandSelectionRestore(hand, player, state.ChoiceSpec);

            Task<IEnumerable<CardModel>>? existingSelectionTask = requiresOfficialLiveRestore
                ? null
                : TryAwaitExistingHandSelection(hand, player);
            MainFile.Controller.RegisterPendingHandChoice(player, state.ChoiceSpec.SelectionPrefs, state.ChoiceSpec.BuildHandFilter(player), source);
            MainFile.Controller.PrepareHandSelectionUiForOpen(hand);
            Task<IEnumerable<CardModel>> selectionTask = existingSelectionTask
                ?? (ShouldUseDetachedHandSelection(state, state.ChoiceSpec, source)
                    ? StartDetachedHandSelection(hand, player, state.ChoiceSpec, source)
                    : hand.SelectCards(state.ChoiceSpec.SelectionPrefs, state.ChoiceSpec.BuildHandFilter(player), source));
            IEnumerable<CardModel> selected = await selectionTask;
            return MapAndSyncHandSelection(runState, state, player, selected);
        }
    }

    private static bool ShouldUseDetachedHandSelection(PausedChoiceState state, UndoChoiceSpec choiceSpec, AbstractModel? source)
    {
        if (choiceSpec.SourcePileType != PileType.Hand)
            return false;

        if (IsTrackedOfficialFromHandDiscardChoice(state, choiceSpec))
            return source == null;

        return true;
    }

    private static Task<IEnumerable<CardModel>> StartDetachedHandSelection(NPlayerHand hand, Player player, UndoChoiceSpec choiceSpec, AbstractModel? source)
    {
        MainFile.Controller.ResetPendingHandChoiceUiForRestore(hand, player);
        return hand.SelectCards(choiceSpec.SelectionPrefs, choiceSpec.BuildHandFilter(player), source);
    }

    private sealed class ChooseACardPausedChoiceCodec : IUndoActionCodec<PausedChoiceState>
    {
        public string CodecId => "action:choose-a-card";

        public bool CanHandle(PausedChoiceState state)
        {
            return state.ChoiceKind == UndoChoiceKind.ChooseACard && state.ChoiceSpec != null;
        }

        public async Task<UndoChoiceResultKey?> RestoreAsync(PausedChoiceState state, RunState runState)
        {
            Player? player = GetOwner(runState, state);
            if (player == null || state.ChoiceSpec == null)
                return null;

            IReadOnlyList<CardModel> options = ResolveChooseACardOptions(player, state);
            Task<IEnumerable<CardModel>> selectionTask = TryAwaitExistingChooseACardSelection()
                ?? NChooseACardSelectionScreen.ShowScreen(options, state.ChoiceSpec.CanSkip)?.CardsSelected()
                ?? Task.FromResult(Enumerable.Empty<CardModel>());
            CardModel? selected = (await selectionTask).FirstOrDefault();
            return MapAndSyncChooseACard(runState, state, player, options, selected);
        }
    }

    private sealed class SimpleGridPausedChoiceCodec : IUndoActionCodec<PausedChoiceState>
    {
        public string CodecId => "action:simple-grid";

        public bool CanHandle(PausedChoiceState state)
        {
            return state.ChoiceKind == UndoChoiceKind.SimpleGridSelection && state.ChoiceSpec != null;
        }

        public async Task<UndoChoiceResultKey?> RestoreAsync(PausedChoiceState state, RunState runState)
        {
            Player? player = GetOwner(runState, state);
            if (player == null || state.ChoiceSpec == null)
                return null;

            IReadOnlyList<CardModel> options = ResolveSimpleGridOptions(player, state);
            Task<IEnumerable<CardModel>> selectionTask = TryAwaitExistingSimpleGridSelection();
            if (selectionTask == null)
            {
                NSimpleCardSelectScreen screen = NSimpleCardSelectScreen.Create(options, state.ChoiceSpec.SelectionPrefs);
                NOverlayStack.Instance.Push(screen);
                selectionTask = screen.CardsSelected();
            }

            IEnumerable<CardModel> selected = await selectionTask;
            return MapAndSyncSimpleGrid(runState, state, player, options, selected);
        }
    }
}



