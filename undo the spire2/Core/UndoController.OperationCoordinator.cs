using System.Threading;
using MegaCrit.Sts2.Core.Helpers;

namespace UndoTheSpire2;

public sealed partial class UndoController
{
    private enum UndoOperationLane
    {
        HistoryMove,
        ChoiceSelection,
        HandChoiceUiReconcile
    }

    private sealed class UndoOperationLease(UndoOperationLane lane, long generation, string stage)
    {
        public UndoOperationLane Lane { get; } = lane;

        public long Generation { get; } = generation;

        public string Stage { get; private set; } = stage;

        public CancellationTokenSource CancellationSource { get; } = new();

        public bool TerminalLogged { get; set; }

        public void UpdateStage(string stage)
        {
            Stage = stage;
        }
    }

    private void StartHistoryMoveOperation(string stage, Func<UndoOperationLease, Task> operation)
    {
        StartTrackedOperation(UndoOperationLane.HistoryMove, stage, operation);
    }

    private void StartChoiceSelectionOperation(string stage, Func<UndoOperationLease, Task> operation)
    {
        StartTrackedOperation(UndoOperationLane.ChoiceSelection, stage, operation);
    }

    private void StartHandChoiceUiReconcileOperation(string stage, Func<UndoOperationLease, Task> operation)
    {
        StartTrackedOperation(UndoOperationLane.HandChoiceUiReconcile, stage, operation);
    }

    private void CancelAllTrackedOperations(string reason)
    {
        List<UndoOperationLease> leases;
        lock (_operationLeaseLock)
        {
            leases = _activeOperationLeases.Values.ToList();
            _activeOperationLeases.Clear();
        }

        foreach (UndoOperationLease lease in leases)
            CancelTrackedOperationLease(lease, reason);
    }

    private void StartTrackedOperation(UndoOperationLane lane, string stage, Func<UndoOperationLease, Task> operation)
    {
        UndoOperationLease lease = BeginTrackedOperation(lane, stage);
        TaskHelper.RunSafely(RunTrackedOperationAsync(lease, operation));
    }

    private UndoOperationLease BeginTrackedOperation(UndoOperationLane lane, string stage)
    {
        List<(UndoOperationLease Lease, string Reason)> toCancel = [];
        UndoOperationLease lease;
        lock (_operationLeaseLock)
        {
            switch (lane)
            {
                case UndoOperationLane.HistoryMove:
                    foreach ((UndoOperationLane activeLane, UndoOperationLease activeLease) in _activeOperationLeases.ToArray())
                    {
                        toCancel.Add((activeLease, $"superseded_by_{lane}"));
                        _activeOperationLeases.Remove(activeLane);
                    }

                    break;
                case UndoOperationLane.ChoiceSelection:
                    CollectLeaseForCancellation(UndoOperationLane.ChoiceSelection, $"superseded_by_{lane}");
                    CollectLeaseForCancellation(UndoOperationLane.HandChoiceUiReconcile, $"superseded_by_{lane}");
                    break;
                case UndoOperationLane.HandChoiceUiReconcile:
                    CollectLeaseForCancellation(UndoOperationLane.HandChoiceUiReconcile, $"superseded_by_{lane}");
                    break;
            }

            lease = new UndoOperationLease(lane, _nextOperationGeneration++, stage);
            _activeOperationLeases[lane] = lease;
        }

        foreach ((UndoOperationLease canceledLease, string reason) in toCancel)
            CancelTrackedOperationLease(canceledLease, reason);

        UndoDebugLog.Write(
            $"undo_operation_started lane={lease.Lane} generation={lease.Generation} stage={lease.Stage}");
        return lease;

        void CollectLeaseForCancellation(UndoOperationLane targetLane, string reason)
        {
            if (_activeOperationLeases.TryGetValue(targetLane, out UndoOperationLease? existingLease))
            {
                toCancel.Add((existingLease, reason));
                _activeOperationLeases.Remove(targetLane);
            }
        }
    }

    private async Task RunTrackedOperationAsync(UndoOperationLease lease, Func<UndoOperationLease, Task> operation)
    {
        try
        {
            if (ShouldAbortTrackedOperation(lease, $"{lease.Stage}:pre_start"))
                return;

            await operation(lease);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            UndoDebugLog.Write(
                $"undo_operation_failed lane={lease.Lane} generation={lease.Generation} stage={lease.Stage} error={ex}");
        }
        finally
        {
            CompleteTrackedOperation(lease, lease.Stage);
        }
    }

    private bool ShouldAbortTrackedOperation(UndoOperationLease lease, string stage)
    {
        lease.UpdateStage(stage);
        lock (_operationLeaseLock)
        {
            return lease.CancellationSource.IsCancellationRequested
                || !_activeOperationLeases.TryGetValue(lease.Lane, out UndoOperationLease? currentLease)
                || !ReferenceEquals(currentLease, lease);
        }
    }

    private void CompleteTrackedOperation(UndoOperationLease lease, string stage)
    {
        bool shouldLogCompletion = false;
        lock (_operationLeaseLock)
        {
            lease.UpdateStage(stage);
            if (_activeOperationLeases.TryGetValue(lease.Lane, out UndoOperationLease? currentLease)
                && ReferenceEquals(currentLease, lease))
            {
                _activeOperationLeases.Remove(lease.Lane);
            }

            if (!lease.TerminalLogged)
            {
                lease.TerminalLogged = true;
                shouldLogCompletion = true;
            }
        }

        if (shouldLogCompletion)
        {
            UndoDebugLog.Write(
                $"undo_operation_completed lane={lease.Lane} generation={lease.Generation} stage={lease.Stage}");
        }
    }

    private void CancelTrackedOperationLease(UndoOperationLease lease, string reason)
    {
        bool shouldLogCancellation = false;
        lock (_operationLeaseLock)
        {
            lease.UpdateStage(reason);
            if (!lease.CancellationSource.IsCancellationRequested)
                lease.CancellationSource.Cancel();

            if (!lease.TerminalLogged)
            {
                lease.TerminalLogged = true;
                shouldLogCancellation = true;
            }
        }

        if (shouldLogCancellation)
        {
            UndoDebugLog.Write(
                $"undo_operation_canceled lane={lease.Lane} generation={lease.Generation} stage={lease.Stage}");
        }
    }
}
