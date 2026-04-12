using System.Reflection;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace UndoTheSpire2;

public sealed partial class UndoController
{
    private enum HandChoiceUiSettleResult
    {
        Settled,
        Forced,
        TimedOut,
        Canceled
    }

    private sealed class PendingHandChoiceUiState(AbstractModel source)
    {
        public AbstractModel Source { get; set; } = source;

        public bool CallbackObserved { get; set; }

        public int ExpectedHandCount { get; set; } = -1;

        public int StableFrames { get; set; }

        public bool InstantCompletionApplied { get; set; }

        public int SelectedHolderCountBefore { get; set; }

        public HandChoiceUiSettleResult ResultHint { get; set; } = HandChoiceUiSettleResult.Settled;

        public string RecoveryPath { get; set; } = "live";

        public bool FinalResultLogged { get; set; }
    }

    public void OnOfficialHandChoiceSourceFinishing(NPlayerHand hand, AbstractModel? source)
    {
        PendingHandChoiceUiState? pendingState = _pendingHandChoiceUiState;
        if (source == null
            || pendingState == null
            || !TryAdoptPendingHandChoiceSource(source)
            || !IsUndoSpecificHandChoiceContext())
        {
            return;
        }

        pendingState.SelectedHolderCountBefore = GetSelectedHandHolderCount(hand);
    }

    public void RegisterPendingHandChoice(Player player, CardSelectorPrefs prefs, Func<CardModel, bool>? filter, AbstractModel? source)
    {
        if (!ShouldTrackLocalChoice(player))
            return;

        _pendingChoiceSpec = UndoChoiceSpec.CreateHandSelection(player, prefs, filter, source);
        SetPendingHandChoiceUiTracking(player, source);
    }

    internal void PrimePendingHandChoiceUiTracking(Player player, AbstractModel? source)
    {
        SetPendingHandChoiceUiTracking(player, source);
    }

    public void OnOfficialHandChoiceSourceFinished(NPlayerHand hand, AbstractModel? source)
    {
        PendingHandChoiceUiState? pendingState = _pendingHandChoiceUiState;
        if (pendingState == null
            || source == null
            || !TryAdoptPendingHandChoiceSource(source))
        {
            return;
        }

        pendingState.CallbackObserved = true;
        pendingState.StableFrames = 0;
        UndoDebugLog.Write(
            $"official_hand_choice_source_finished source={source.GetType().Name}"
            + $" holders={hand.CardHolderContainer.GetChildCount()}"
            + $" selected={GetSelectedHandHolderCount(hand)}");
        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        Player? player = combatState == null ? null : LocalContext.GetMe(combatState);
        if (ShouldForceCompletePendingHandChoiceUi(hand, source))
            ForceCompletePendingHandChoiceUi(hand, player);
        TryCompletePendingHandChoiceUiInstantly(hand, player);
        pendingState.SelectedHolderCountBefore = 0;
        StartHandChoiceUiReconcileOperation(
            "official_hand_choice_ui_followup",
            lease => TryCompletePendingHandChoiceUiSoonAsync(lease, source));
    }

    private bool IsAwaitingOfficialHandChoiceSourceFinish(NPlayerHand? hand = null, Player? player = null)
    {
        return _pendingHandChoiceUiState != null && ObserveOfficialHandChoiceUiSettle(hand, player) == null;
    }

    private async Task<HandChoiceUiSettleResult> WaitForOfficialHandChoiceUiSettleAsync(
        NPlayerHand? hand,
        Player? player,
        UndoOperationLease? lease = null,
        int maxFrames = 180)
    {
        if (_pendingHandChoiceUiState == null)
            return HandChoiceUiSettleResult.Settled;

        for (int frame = 0; frame < maxFrames; frame++)
        {
            if (lease != null && ShouldAbortTrackedOperation(lease, $"hand_choice_ui_wait:{frame}"))
                return HandChoiceUiSettleResult.Canceled;

            hand ??= NCombatRoom.Instance?.Ui?.Hand;
            HandChoiceUiSettleResult? observedResult = ObserveOfficialHandChoiceUiSettle(hand, player);
            if (observedResult != null)
                return observedResult.Value;

            await WaitOneFrameAsync();
        }

        hand ??= NCombatRoom.Instance?.Ui?.Hand;
        HandChoiceUiSettleResult? finalResult = ObserveOfficialHandChoiceUiSettle(hand, player);
        return finalResult ?? HandChoiceUiSettleResult.TimedOut;
    }

    private bool TryCompletePendingHandDiscardChoiceUiViaOfficialPath(NPlayerHand hand)
    {
        PendingHandChoiceUiState? pendingState = _pendingHandChoiceUiState;
        if (!CanSafelyMutateHandUi(hand)
            || pendingState?.Source == null
            || pendingState.CallbackObserved
            || GetSelectedHandHolderCount(hand) == 0)
        {
            return false;
        }

        MethodInfo? handlerMethod = FindMethod(hand.GetType(), "OnSelectModeSourceFinished");
        if (handlerMethod == null)
            return false;

        try
        {
            handlerMethod.Invoke(hand, [pendingState.Source]);
            MarkPendingHandChoiceRecovery(HandChoiceUiSettleResult.Forced, "official-path");
            UndoDebugLog.Write(
                $"hand_discard_ui_recovered_via_official source={pendingState.Source.GetType().Name}"
                + $" holders={hand.CardHolderContainer.GetChildCount()} selected={GetSelectedHandHolderCount(hand)}");
            return true;
        }
        catch (Exception ex)
        {
            UndoDebugLog.Write($"hand_discard_ui_official_recovery_failed:{ex}");
            return false;
        }
    }

    private void SetPendingHandChoiceUiTracking(Player player, AbstractModel? source)
    {
        if (source == null)
        {
            _pendingHandChoiceUiState = null;
            return;
        }

        _pendingHandChoiceUiState = new PendingHandChoiceUiState(source)
        {
            ExpectedHandCount = PileType.Hand.GetPile(player).Cards.Count
        };
    }

    private void ClearPendingHandChoiceSourceTracking(bool canceled = false)
    {
        PendingHandChoiceUiState? pendingState = _pendingHandChoiceUiState;
        if (pendingState == null)
            return;

        if (canceled)
            FinalizePendingHandChoiceLifecycle(pendingState, HandChoiceUiSettleResult.Canceled, "canceled");
        else
            _pendingHandChoiceUiState = null;
    }

    private void FinalizePendingHandChoiceLifecycle(
        PendingHandChoiceUiState pendingState,
        HandChoiceUiSettleResult result,
        string recoveryPath,
        int? replayEventCount = null)
    {
        if (!pendingState.FinalResultLogged)
        {
            pendingState.FinalResultLogged = true;
            UndoDebugLog.Write(
                $"hand_choice_settle_result result={result}"
                + $" recovery_path={recoveryPath}"
                + $" source={pendingState.Source.GetType().Name}"
                + $" replayEvents={(replayEventCount ?? GetCurrentReplayEventCount())}");
        }

        if (ReferenceEquals(_pendingHandChoiceUiState, pendingState))
            _pendingHandChoiceUiState = null;
    }

    private void MarkPendingHandChoiceRecovery(HandChoiceUiSettleResult result, string recoveryPath)
    {
        PendingHandChoiceUiState? pendingState = _pendingHandChoiceUiState;
        if (pendingState == null)
            return;

        pendingState.ResultHint = result;
        pendingState.RecoveryPath = recoveryPath;
    }

    private static bool IsCompletedHandChoiceUiSettleResult(HandChoiceUiSettleResult result)
    {
        return result is HandChoiceUiSettleResult.Settled or HandChoiceUiSettleResult.Forced;
    }

    private string GetPendingHandChoiceCallbackObservedText()
    {
        return _pendingHandChoiceUiState?.CallbackObserved.ToString() ?? "null";
    }

    private string GetPendingHandChoiceSourceName(string? fallbackSourceName = null)
    {
        return _pendingHandChoiceUiState?.Source.GetType().Name
            ?? fallbackSourceName
            ?? "unknown";
    }

    private void FinalizePendingHandChoiceAfterForcedSync(NPlayerHand hand, Player player)
    {
        PendingHandChoiceUiState? pendingState = _pendingHandChoiceUiState;
        if (pendingState == null)
            return;

        MarkPendingHandChoiceRecovery(HandChoiceUiSettleResult.Forced, "sync-existing-hand");
        UndoDebugLog.Write(
            $"official_hand_choice_ui_forced_settle source={pendingState.Source.GetType().Name}"
            + $" expectedHand={PileType.Hand.GetPile(player).Cards.Count}"
            + $" holders={hand.CardHolderContainer.GetChildCount()}");
        FinalizePendingHandChoiceLifecycle(pendingState, HandChoiceUiSettleResult.Forced, "sync-existing-hand");
    }

    private bool TryAdoptPendingHandChoiceSource(AbstractModel source)
    {
        PendingHandChoiceUiState? pendingState = _pendingHandChoiceUiState;
        if (pendingState == null)
            return false;

        if (ReferenceEquals(source, pendingState.Source))
            return true;

        if (!AreEquivalentPendingHandChoiceSources(source, pendingState.Source))
            return false;

        UndoDebugLog.Write(
            $"official_hand_choice_source_rebound expected={pendingState.Source.GetType().Name}"
            + $" actual={source.GetType().Name}");
        pendingState.Source = source;
        return true;
    }

    private static bool AreEquivalentPendingHandChoiceSources(AbstractModel? left, AbstractModel? right)
    {
        if (ReferenceEquals(left, right))
            return true;

        if (left == null || right == null || left.GetType() != right.GetType())
            return false;

        if (left is CardModel leftCard && right is CardModel rightCard)
        {
            try
            {
                return UndoSerializationUtil.PacketDataEquals(leftCard.ToSerializable(), rightCard.ToSerializable());
            }
            catch
            {
            }
        }

        return string.Equals(left.Id.Entry, right.Id.Entry, StringComparison.Ordinal);
    }

    private HandChoiceUiSettleResult? ObserveOfficialHandChoiceUiSettle(
        NPlayerHand? hand,
        Player? player = null,
        int requiredStableFrames = RequiredHandChoiceUiStableFrames)
    {
        PendingHandChoiceUiState? pendingState = _pendingHandChoiceUiState;
        if (pendingState == null)
            return HandChoiceUiSettleResult.Settled;

        if (player != null)
            pendingState.ExpectedHandCount = PileType.Hand.GetPile(player).Cards.Count;

        if (!CanSafelyMutateHandUi(hand))
            return null;

        if (!pendingState.CallbackObserved)
        {
            pendingState.StableFrames = 0;
            return null;
        }
        if (hand == null)
            return null;

        TryCompletePendingHandChoiceUiInstantly(hand, player);

        int selectedCount = GetSelectedHandHolderCount(hand);
        int holderCount = hand.CardHolderContainer.GetChildCount();
        int expectedHandCount = pendingState.ExpectedHandCount;
        bool holdersMatch = expectedHandCount >= 0 && holderCount == expectedHandCount;
        bool selectedContainerDrained = selectedCount == 0;
        bool reusable = player == null || TryGetReusableHandHolders(hand, player, out _);
        if (!selectedContainerDrained || !holdersMatch || !reusable)
        {
            pendingState.StableFrames = 0;
            return null;
        }

        pendingState.StableFrames++;
        if (pendingState.StableFrames < requiredStableFrames)
            return null;

        HandChoiceUiSettleResult result = pendingState.ResultHint;
        UndoDebugLog.Write(
            $"official_hand_choice_ui_settled source={pendingState.Source.GetType().Name}"
            + $" expectedHand={expectedHandCount} holders={holderCount} selected={selectedCount}"
            + $" stableFrames={pendingState.StableFrames}");
        FinalizePendingHandChoiceLifecycle(pendingState, result, pendingState.RecoveryPath);
        return result;
    }

    private void TryCompletePendingHandChoiceUiInstantly(NPlayerHand hand, Player? player, bool allowBeforeOfficialCallback = false)
    {
        PendingHandChoiceUiState? pendingState = _pendingHandChoiceUiState;
        if (pendingState == null
            || !CanSafelyMutateHandUi(hand)
            || (_syntheticChoiceSession == null && !allowBeforeOfficialCallback)
            || (!allowBeforeOfficialCallback && !pendingState.CallbackObserved)
            || pendingState.InstantCompletionApplied)
        {
            return;
        }

        if (GetSelectedHandHolderCount(hand) > 0)
        {
            if (ShouldForceCompletePendingHandChoiceUi(hand, pendingState.Source))
                ForceCompletePendingHandChoiceUi(hand, player);

            return;
        }

        ClearHandChoiceUiTweens(hand);
        ClearTween(hand, "_selectedCardScaleTween");
        ClearTween(hand, "_animInTween");
        ClearTween(hand, "_animOutTween");
        ClearTween(hand, "_animEnableTween");
        GetPrivateFieldValue<System.Collections.IDictionary>(hand, "_holdersAwaitingQueue")?.Clear();
        InvokePrivateMethod(hand, "RefreshLayout");
        hand.ForceRefreshCardIndices();
        ClearHandChoiceUiTweens(hand);
        SnapHandHolders(hand, preserveHoveredHolders: true);
        if (player != null
            && (hand.CardHolderContainer.GetChildCount() != PileType.Hand.GetPile(player).Cards.Count
                || !TryGetReusableHandHolders(hand, player, out _)))
        {
            TrySyncExistingHandUi(hand, player, normalizeLayout: true);
        }

        pendingState.InstantCompletionApplied = true;
        MarkPendingHandChoiceRecovery(HandChoiceUiSettleResult.Forced, "instant");
        UndoDebugLog.Write(
            $"official_hand_choice_ui_instant_completed source={pendingState.Source.GetType().Name}"
            + $" holders={hand.CardHolderContainer.GetChildCount()} awaiting={GetAwaitingHandHolderCount(hand)}");
    }

    private bool IsUndoSpecificHandChoiceContext()
    {
        return IsRestoring || _syntheticChoiceSession != null;
    }

    private bool ShouldForceCompletePendingHandChoiceUi(NPlayerHand hand, AbstractModel? source)
    {
        PendingHandChoiceUiState? pendingState = _pendingHandChoiceUiState;
        if (pendingState == null
            || source == null
            || !IsUndoSpecificHandChoiceContext()
            || !ReferenceEquals(source, pendingState.Source))
        {
            return false;
        }

        return pendingState.SelectedHolderCountBefore > 0 || GetSelectedHandHolderCount(hand) > 0;
    }

    private void ForceCompletePendingHandChoiceUi(NPlayerHand hand, Player? player)
    {
        PendingHandChoiceUiState? pendingState = _pendingHandChoiceUiState;
        if (pendingState == null || !CanSafelyMutateHandUi(hand))
            return;

        ClearHandChoiceUiTweens(hand);
        ClearSelectedHandCardsUi(hand);
        ResetSelectedHandCardContainerState(hand);
        GetPrivateFieldValue<System.Collections.IDictionary>(hand, "_holdersAwaitingQueue")?.Clear();
        GetPrivateFieldValue<System.Collections.IList>(hand, "_selectedCards")?.Clear();
        InvokePrivateMethod(hand, "RefreshLayout");
        hand.ForceRefreshCardIndices();
        SnapHandHolders(hand, preserveHoveredHolders: true);
        if (player != null
            && (hand.CardHolderContainer.GetChildCount() != PileType.Hand.GetPile(player).Cards.Count
                || !TryGetReusableHandHolders(hand, player, out _)))
        {
            TrySyncExistingHandUi(hand, player, normalizeLayout: true);
        }

        pendingState.InstantCompletionApplied = true;
        pendingState.SelectedHolderCountBefore = 0;
        MarkPendingHandChoiceRecovery(HandChoiceUiSettleResult.Forced, "force");
        UndoDebugLog.Write(
            $"official_hand_choice_ui_force_complete source={pendingState.Source.GetType().Name}"
            + $" holders={hand.CardHolderContainer.GetChildCount()}"
            + $" selected={GetSelectedHandHolderCount(hand)}"
            + $" awaiting={GetAwaitingHandHolderCount(hand)}");
    }

    private async Task TryCompletePendingHandChoiceUiSoonAsync(UndoOperationLease lease, AbstractModel source, int maxFrames = 4)
    {
        for (int frame = 0; frame < maxFrames; frame++)
        {
            if (ShouldAbortTrackedOperation(lease, $"hand_choice_ui_reconcile:{frame}"))
                return;

            PendingHandChoiceUiState? pendingState = _pendingHandChoiceUiState;
            if (pendingState == null || !ReferenceEquals(pendingState.Source, source))
                return;

            NPlayerHand? hand = NCombatRoom.Instance?.Ui?.Hand;
            CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
            Player? player = combatState == null ? null : LocalContext.GetMe(combatState);
            if (hand != null)
                TryCompletePendingHandChoiceUiInstantly(hand, player);

            if (_pendingHandChoiceUiState == null || pendingState.InstantCompletionApplied)
                return;

            await WaitOneFrameAsync();
        }
    }

    private void DetachPendingHandSelectionSource(NPlayerHand hand)
    {
        PendingHandChoiceUiState? pendingState = _pendingHandChoiceUiState;
        if (pendingState?.Source == null)
            return;

        MethodInfo? handlerMethod = FindMethod(hand.GetType(), "OnSelectModeSourceFinished");
        if (handlerMethod == null)
        {
            FinalizePendingHandChoiceLifecycle(pendingState, HandChoiceUiSettleResult.Canceled, "handler_missing");
            return;
        }

        try
        {
            Action<AbstractModel> handler = (Action<AbstractModel>)handlerMethod.CreateDelegate(typeof(Action<AbstractModel>), hand);
            pendingState.Source.ExecutionFinished -= handler;
        }
        catch
        {
        }

        FinalizePendingHandChoiceLifecycle(pendingState, HandChoiceUiSettleResult.Canceled, "detached");
    }
}
