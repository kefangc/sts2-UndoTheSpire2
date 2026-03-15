// 文件说明：实现 paused choice 等官方 action 的恢复 codec。
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
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

        return key;
    }

    private static UndoChoiceResultKey? MapAndSyncChooseACard(RunState runState, PausedChoiceState state, Player player, IReadOnlyList<CardModel> options, CardModel? selectedCard)
    {
        UndoChoiceResultKey? key = state.ChoiceSpec?.TryMapDisplayedOptionSelection(options, selectedCard == null ? [] : [selectedCard]);
        int selectedIndex = selectedCard == null ? -1 : IndexOfReference(options, selectedCard);
        uint? choiceId = GetChoiceId(runState, state, player);
        if (choiceId != null)
            RunManager.Instance.PlayerChoiceSynchronizer.SyncLocalChoice(player, choiceId.Value, PlayerChoiceResult.FromIndex(selectedIndex));

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

        return key;
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

    private static Task<IEnumerable<CardModel>>? TryAwaitExistingHandSelection()
    {
        NPlayerHand? hand = NPlayerHand.Instance;
        if (hand?.IsInCardSelection != true)
            return null;

        if (UndoReflectionUtil.FindField(hand.GetType(), "_selectionCompletionSource")?.GetValue(hand) is not TaskCompletionSource<IEnumerable<CardModel>> completionSource)
            return null;

        Task<IEnumerable<CardModel>> task = completionSource.Task;
        if (task.IsCompleted)
        {
            UndoReflectionUtil.FindMethod(hand.GetType(), "AfterCardsSelected")?.Invoke(hand, [null]);
            return null;
        }

        return task;
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

            Task<IEnumerable<CardModel>> selectionTask = ShouldUseDetachedHandSelection(state.ChoiceSpec)
                ? StartDetachedHandSelection(hand, player, state.ChoiceSpec)
                : TryAwaitExistingHandSelection()
                    ?? hand.SelectCards(state.ChoiceSpec.SelectionPrefs, state.ChoiceSpec.BuildHandFilter(player), null);
            IEnumerable<CardModel> selected = await selectionTask;
            return MapAndSyncHandSelection(runState, state, player, selected);
        }
    }

    private static bool ShouldUseDetachedHandSelection(UndoChoiceSpec choiceSpec)
    {
        return choiceSpec.SourcePileType == PileType.Hand
            && string.Equals(choiceSpec.SelectionPrefs.Prompt.LocTable, "card_selection", StringComparison.Ordinal)
            && string.Equals(choiceSpec.SelectionPrefs.Prompt.LocEntryKey, "TO_DISCARD", StringComparison.Ordinal);
    }

    private static Task<IEnumerable<CardModel>> StartDetachedHandSelection(NPlayerHand hand, Player player, UndoChoiceSpec choiceSpec)
    {
        if (hand.IsInCardSelection)
            UndoReflectionUtil.FindMethod(hand.GetType(), "AfterCardsSelected")?.Invoke(hand, [null]);

        UndoReflectionUtil.FindField(hand.GetType(), "_selectionCompletionSource")?.SetValue(hand, null);
        return hand.SelectCards(choiceSpec.SelectionPrefs, choiceSpec.BuildHandFilter(player), null);
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



