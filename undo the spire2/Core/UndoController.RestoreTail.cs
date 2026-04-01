using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace UndoTheSpire2;

public sealed partial class UndoController
{
    private static void QuarantineOutstandingActionsForRestore(ActionExecutor actionExecutor)
    {
        if (actionExecutor.CurrentlyRunningAction is { } currentAction)
            QuarantineActionForRestore(currentAction);

        if (UndoReflectionUtil.FindField(RunManager.Instance.ActionQueueSet.GetType(), "_actionQueues")?.GetValue(RunManager.Instance.ActionQueueSet) is not System.Collections.IEnumerable rawQueues)
            return;

        foreach (object rawQueue in rawQueues)
        {
            if (UndoReflectionUtil.FindField(rawQueue.GetType(), "actions")?.GetValue(rawQueue) is not System.Collections.IEnumerable actions)
                continue;

            foreach (GameAction action in actions.OfType<GameAction>().ToList())
                QuarantineActionForRestore(action);
        }
    }

    private static void QuarantineActionForRestore(GameAction action)
    {
        TrackRestoreTailTask(GetActionExecutionTask(action));
        bool hadPausedChoiceTail =
            action.State == GameActionState.GatheringPlayerChoice
            || UndoReflectionUtil.FindField(action.GetType(), "_executeAfterResumptionTaskSource")?.GetValue(action) != null;

        try
        {
            action.Cancel();
        }
        catch
        {
        }

        if (hadPausedChoiceTail && TryReleasePausedChoiceTailForRestore(action))
        {
            UndoDebugLog.Write(
                $"restore_choice_tail_released action={action.GetType().Name}"
                + $" actionId={action.Id?.ToString() ?? "null"}");
        }

        foreach (string eventFieldName in new[]
        {
            "AfterFinished",
            "BeforeExecuted",
            "BeforeCancelled",
            "BeforePausedForPlayerChoice",
            "BeforeReadyToResumeAfterPlayerChoice",
            "BeforeResumedAfterPlayerChoice"
        })
        {
            UndoReflectionUtil.TrySetFieldValue(action, eventFieldName, null);
        }
    }

    private static bool TryReleasePausedChoiceTailForRestore(GameAction action)
    {
        object? resumeTaskSource = UndoReflectionUtil.FindField(action.GetType(), "_executeAfterResumptionTaskSource")?.GetValue(action);
        if (resumeTaskSource == null)
            return false;

        try
        {
            if (UndoReflectionUtil.FindMethod(resumeTaskSource.GetType(), "TrySetCanceled") is { } trySetCanceled)
            {
                object? result = trySetCanceled.Invoke(resumeTaskSource, []);
                return result is not bool success || success;
            }

            if (UndoReflectionUtil.FindMethod(resumeTaskSource.GetType(), "SetCanceled") is { } setCanceled)
            {
                setCanceled.Invoke(resumeTaskSource, []);
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static Task? GetActionExecutionTask(GameAction action)
    {
        return UndoReflectionUtil.FindField(typeof(GameAction), "_executionTask")?.GetValue(action) as Task;
    }

    private static void TrackRestoreTailTask(Task? task)
    {
        if (task == null || task.IsCompleted)
            return;

        lock (RestoreTailTaskLock)
        {
            RestoreTailTasks.Add(task);
        }
    }

    private static bool HasTrackedRestoreTailTasks()
    {
        lock (RestoreTailTaskLock)
        {
            for (int i = RestoreTailTasks.Count - 1; i >= 0; i--)
            {
                if (RestoreTailTasks[i].IsCompleted)
                    RestoreTailTasks.RemoveAt(i);
            }

            return RestoreTailTasks.Count > 0;
        }
    }

    private static bool HasSavestateVisualTailActivity()
    {
        NCombatRoom? combatRoom = NCombatRoom.Instance;
        return HasTransientCardFlyVfx(combatRoom?.BackCombatVfxContainer)
            || HasTransientCardFlyVfx(combatRoom?.CombatVfxContainer)
            || HasTransientCardFlyVfx(NRun.Instance?.GlobalUi?.TopBar?.TrailContainer);
    }

    private static bool TryClearSavestateVisualTail()
    {
        if (!HasSavestateVisualTailActivity())
            return false;

        ClearTransientCardVisuals();
        return true;
    }

    private static bool HasSavestateStructuralTailActivity(bool includeTrackedRestoreTasks = true)
    {
        ActionExecutor actionExecutor = RunManager.Instance.ActionExecutor;
        if (actionExecutor.CurrentlyRunningAction != null || actionExecutor.IsRunning)
            return true;

        if (includeTrackedRestoreTasks && HasTrackedRestoreTailTasks())
            return true;

        if (UndoReflectionUtil.FindField(RunManager.Instance.ActionQueueSet.GetType(), "_actionsWaitingForResumption")?.GetValue(RunManager.Instance.ActionQueueSet) is System.Collections.ICollection waitingForResumption
            && waitingForResumption.Count > 0)
        {
            return true;
        }

        if (UndoReflectionUtil.FindField(RunManager.Instance.ActionQueueSet.GetType(), "_actionQueues")?.GetValue(RunManager.Instance.ActionQueueSet) is not System.Collections.IEnumerable rawQueues)
            return false;

        foreach (object rawQueue in rawQueues)
        {
            if (UndoReflectionUtil.FindField(rawQueue.GetType(), "actions")?.GetValue(rawQueue) is System.Collections.ICollection actions
                && actions.Count > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasSavestateBoundaryTailActivity(bool includeTrackedRestoreTasks = true, bool includeVisualTail = true)
    {
        if (HasSavestateStructuralTailActivity(includeTrackedRestoreTasks))
            return true;

        return includeVisualTail && HasSavestateVisualTailActivity();
    }

    private static bool HasImmediateRestoreTailActivity()
    {
        return HasSavestateBoundaryTailActivity();
    }

    private static bool RemoveRestoreTailQueuedActions(GameAction currentAction, uint? actionId)
    {
        bool removedAny = false;
        if (UndoReflectionUtil.FindField(RunManager.Instance.ActionQueueSet.GetType(), "_actionQueues")?.GetValue(RunManager.Instance.ActionQueueSet) is not System.Collections.IEnumerable rawQueues)
            return false;

        foreach (object rawQueue in rawQueues)
        {
            if (UndoReflectionUtil.FindField(rawQueue.GetType(), "actions")?.GetValue(rawQueue) is not System.Collections.IList actions)
                continue;

            for (int i = actions.Count - 1; i >= 0; i--)
            {
                if (actions[i] is not GameAction queuedAction)
                    continue;

                if (!ReferenceEquals(queuedAction, currentAction)
                    && (actionId == null || queuedAction.Id != actionId))
                {
                    continue;
                }

                QuarantineActionForRestore(queuedAction);
                actions.RemoveAt(i);
                removedAny = true;
            }
        }

        return removedAny;
    }

    private static int RemoveRestoreTailWaitingEntries(uint? actionId = null)
    {
        if (UndoReflectionUtil.FindField(RunManager.Instance.ActionQueueSet.GetType(), "_actionsWaitingForResumption")?.GetValue(RunManager.Instance.ActionQueueSet) is not System.Collections.IList waitingForResumption)
            return 0;

        int removedCount = 0;
        for (int i = waitingForResumption.Count - 1; i >= 0; i--)
        {
            object waiting = waitingForResumption[i];
            object? oldIdValue = FindField(waiting.GetType(), "oldId")?.GetValue(waiting);
            object? newIdValue = FindField(waiting.GetType(), "newId")?.GetValue(waiting);
            uint? oldId = oldIdValue is uint oldIdTyped ? oldIdTyped : null;
            uint? newId = newIdValue is uint newIdTyped ? newIdTyped : null;
            if (actionId != null && oldId != actionId && newId != actionId)
                continue;

            waitingForResumption.RemoveAt(i);
            removedCount++;
        }

        return removedCount;
    }

    private static bool TryQuarantineRestoreTailAction()
    {
        ActionExecutor actionExecutor = RunManager.Instance.ActionExecutor;
        GameAction? currentAction = actionExecutor.CurrentlyRunningAction;
        uint? actionId = currentAction?.Id;
        bool removedWaiting = false;
        bool removedQueued = false;
        bool quarantinedCurrent = false;

        if (currentAction is PlayCardAction || currentAction != null && actionId != null)
        {
            removedQueued = currentAction != null && RemoveRestoreTailQueuedActions(currentAction, actionId);
            removedWaiting = RemoveRestoreTailWaitingEntries(actionId) > 0;
            if (currentAction != null)
            {
                QuarantineActionForRestore(currentAction);
                UndoReflectionUtil.TrySetPropertyValue(actionExecutor, "CurrentlyRunningAction", null);
                quarantinedCurrent = true;
            }
        }
        else if (currentAction == null)
        {
            removedWaiting = RemoveRestoreTailWaitingEntries() > 0;
        }

        if (!quarantinedCurrent && !removedWaiting && !removedQueued)
            return false;

        UndoDebugLog.Write(
            $"restore_tail_quarantined action={(currentAction == null ? "null" : currentAction.GetType().Name)}"
            + $" actionId={actionId?.ToString() ?? "null"}"
            + $" state={(currentAction == null ? "null" : currentAction.State.ToString())}"
            + $" queuedRemoved={removedQueued}"
            + $" waitingRemoved={removedWaiting}");
        return true;
    }

    private static bool HasTransientRestoreTailActivity()
    {
        return HasSavestateBoundaryTailActivity();
    }

    private static async Task WaitForRestoreTailToSettleAsync(int maxFrames = 90, int requiredStableFrames = 3)
    {
        TryClearSavestateVisualTail();

        int stableFrames = 0;
        for (int frame = 0; frame < maxFrames; frame++)
        {
            TryQuarantineRestoreTailAction();
            ResetActionExecutorForRestore(trackQueueCompletion: false);
            RunManager.Instance.ActionQueueSet.Reset();
            ResetActionSynchronizerForRestore();
            bool clearedVisualTail = TryClearSavestateVisualTail();
            await WaitOneFrameAsync();

            bool structuralTailActive = HasSavestateStructuralTailActivity();
            bool visualTailActive = HasSavestateVisualTailActivity();
            if (visualTailActive)
            {
                TryClearSavestateVisualTail();
                visualTailActive = HasSavestateVisualTailActivity();
            }

            if (structuralTailActive || visualTailActive || clearedVisualTail)
            {
                stableFrames = 0;
                continue;
            }

            stableFrames++;
            if (stableFrames >= requiredStableFrames)
                return;
        }

        TryQuarantineRestoreTailAction();
        ResetActionExecutorForRestore(trackQueueCompletion: false);
        RunManager.Instance.ActionQueueSet.Reset();
        ResetActionSynchronizerForRestore();
        TryClearSavestateVisualTail();
        for (int frame = 0; frame < requiredStableFrames; frame++)
        {
            await WaitOneFrameAsync();
            if (HasTransientRestoreTailActivity())
                break;
        }
    }
}
