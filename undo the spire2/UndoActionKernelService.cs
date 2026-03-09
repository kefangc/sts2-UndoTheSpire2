using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Runs;

namespace UndoTheSpire2;

internal static class UndoActionKernelService
{
    public static ActionKernelState Capture(RunState runState, UndoChoiceSpec? activeChoiceSpec)
    {
        ActionQueueSet actionQueueSet = RunManager.Instance.ActionQueueSet;
        List<ActionQueueState> queueStates = [];
        if (UndoReflectionUtil.FindField(actionQueueSet.GetType(), "_actionQueues")?.GetValue(actionQueueSet) is System.Collections.IEnumerable rawQueues)
        {
            foreach (object rawQueue in rawQueues)
                queueStates.Add(CaptureQueueState(rawQueue));
        }

        IReadOnlyList<ActionResumeState> waitingForResume = CaptureWaitingForResumeStates(actionQueueSet);
        GameAction? currentAction = RunManager.Instance.ActionExecutor.CurrentlyRunningAction;
        return new ActionKernelState
        {
            CurrentActionTypeName = currentAction?.GetType().FullName,
            CurrentActionState = currentAction?.State,
            CurrentActionRef = CaptureActionRef(currentAction),
            CurrentActionCodecId = TryGetActionCodecId(currentAction),
            CurrentActionPayload = TryCaptureActionPayload(currentAction),
            CurrentHookActionRef = currentAction is GenericHookGameAction hookAction
                ? new ActionRef { HookId = hookAction.HookId, TypeName = hookAction.GetType().FullName }
                : null,
            PausedChoiceState = CapturePausedChoiceState(runState, currentAction, activeChoiceSpec),
            Queues = queueStates,
            WaitingForResumptionCount = waitingForResume.Count,
            WaitingForResumption = waitingForResume
        };
    }

    public static RestoreCapabilityReport Restore(ActionKernelState state, RunState runState)
    {
        if (state.SchemaVersion < ActionKernelState.CurrentSchemaVersion)
        {
            return new RestoreCapabilityReport
            {
                Result = RestoreCapabilityResult.SchemaMismatch,
                Detail = $"action_kernel_schema={state.SchemaVersion}"
            };
        }

        ActionQueueSet actionQueueSet = RunManager.Instance.ActionQueueSet;
        if (UndoReflectionUtil.FindField(actionQueueSet.GetType(), "_actionQueues")?.GetValue(actionQueueSet) is not System.Collections.IList rawQueues)
        {
            return new RestoreCapabilityReport
            {
                Result = RestoreCapabilityResult.UnsupportedOfficialPattern,
                Detail = "action_queues_inaccessible"
            };
        }

        Dictionary<ulong, Player> playersById = runState.Players.ToDictionary(static player => player.NetId);
        Action<GameAction>? popAction = CreatePopActionDelegate(actionQueueSet);
        if (popAction == null)
        {
            return new RestoreCapabilityReport
            {
                Result = RestoreCapabilityResult.UnsupportedOfficialPattern,
                Detail = "pop_action_unavailable"
            };
        }

        for (int i = 0; i < state.Queues.Count && i < rawQueues.Count; i++)
        {
            ApplyQueueFlags(rawQueues[i], state.Queues[i]);
            if (!TryRestoreQueueEntries(rawQueues[i], state.Queues[i], playersById, popAction, out string? error))
            {
                return new RestoreCapabilityReport
                {
                    Result = RestoreCapabilityResult.UnsupportedOfficialPattern,
                    Detail = error
                };
            }
        }

        if (!TryRestoreWaitingForResume(actionQueueSet, state.WaitingForResumption))
        {
            return new RestoreCapabilityReport
            {
                Result = RestoreCapabilityResult.UnsupportedOfficialPattern,
                Detail = "resume_queue_restore_failed"
            };
        }

        if (state.PausedChoiceState?.ChoiceSpec?.SupportsSyntheticRestore == true)
        {
            return new RestoreCapabilityReport
            {
                Result = RestoreCapabilityResult.FallbackToSyntheticChoice,
                Detail = state.PausedChoiceState.ChoiceKind.ToString()
            };
        }

        return RestoreCapabilityReport.SupportedReport();
    }

    private static ActionQueueState CaptureQueueState(object rawQueue)
    {
        List<ActionQueueEntryState> pendingActions = [];
        if (UndoReflectionUtil.FindField(rawQueue.GetType(), "actions")?.GetValue(rawQueue) is System.Collections.IEnumerable rawActions)
        {
            foreach (object rawAction in rawActions)
            {
                if (rawAction is not GameAction action)
                    continue;

                pendingActions.Add(new ActionQueueEntryState
                {
                    ActionRef = CaptureActionRef(action),
                    CodecId = TryGetActionCodecId(action),
                    Payload = TryCaptureActionPayload(action),
                    State = action.State
                });
            }
        }

        return new ActionQueueState
        {
            OwnerNetId = UndoReflectionUtil.FindField(rawQueue.GetType(), "ownerId")?.GetValue(rawQueue) is ulong owner ? owner : 0UL,
            IsPaused = UndoReflectionUtil.FindField(rawQueue.GetType(), "isPaused")?.GetValue(rawQueue) is bool paused && paused,
            PendingActionCount = pendingActions.Count,
            PendingActions = pendingActions
        };
    }

    private static IReadOnlyList<ActionResumeState> CaptureWaitingForResumeStates(ActionQueueSet actionQueueSet)
    {
        List<ActionResumeState> states = [];
        if (UndoReflectionUtil.FindField(actionQueueSet.GetType(), "_actionsWaitingForResumption")?.GetValue(actionQueueSet) is not System.Collections.IEnumerable rawWaiting)
            return states;

        foreach (object rawState in rawWaiting)
        {
            uint? oldId = UndoReflectionUtil.FindField(rawState.GetType(), "oldId")?.GetValue(rawState) as uint?;
            uint? newId = UndoReflectionUtil.FindField(rawState.GetType(), "newId")?.GetValue(rawState) as uint?;
            if (oldId == null || newId == null)
                continue;

            states.Add(new ActionResumeState
            {
                OldActionId = oldId.Value,
                NewActionId = newId.Value
            });
        }

        return states;
    }

    private static PausedChoiceState? CapturePausedChoiceState(RunState runState, GameAction? currentAction, UndoChoiceSpec? activeChoiceSpec)
    {
        if (currentAction?.State != GameActionState.GatheringPlayerChoice || activeChoiceSpec == null)
            return null;

        Player? ownerPlayer = runState.GetPlayer(currentAction.OwnerId);
        IReadOnlyList<CardRef> candidateCards = [];
        if (ownerPlayer != null)
        {
            candidateCards = activeChoiceSpec.Kind switch
            {
                UndoChoiceKind.HandSelection => PileType.Hand.GetPile(ownerPlayer).Cards
                    .Where(activeChoiceSpec.BuildHandFilter(ownerPlayer))
                    .Select(card => UndoStableRefs.CaptureCardRef(runState, card))
                    .ToList(),
                _ => activeChoiceSpec.BuildOptionCards(ownerPlayer)
                    .Select(card => UndoStableRefs.CaptureCardRef(runState, card))
                    .ToList()
            };
        }

        return new PausedChoiceState
        {
            ChoiceKind = activeChoiceSpec.Kind,
            OwnerNetId = ownerPlayer?.NetId,
            Prompt = activeChoiceSpec.SelectionPrefs.Prompt?.ToString(),
            MinSelections = activeChoiceSpec.SelectionPrefs.MinSelect,
            MaxSelections = activeChoiceSpec.SelectionPrefs.MaxSelect,
            CandidateCardRefs = candidateCards,
            SourceActionRef = CaptureActionRef(currentAction),
            ChoiceSpec = activeChoiceSpec
        };
    }

    private static ActionRef? CaptureActionRef(GameAction? action)
    {
        if (action == null)
            return null;

        if (action is GenericHookGameAction hookAction)
        {
            return new ActionRef
            {
                ActionId = action.Id,
                HookId = hookAction.HookId,
                TypeName = action.GetType().FullName
            };
        }

        return new ActionRef
        {
            ActionId = action.Id,
            TypeName = action.GetType().FullName
        };
    }

    private static string? TryGetActionCodecId(GameAction? action)
    {
        return action switch
        {
            null => null,
            GenericHookGameAction => "action:hook",
            _ => "action:net"
        };
    }

    private static UndoSerializedActionPayload? TryCaptureActionPayload(GameAction? action)
    {
        if (action == null)
            return null;

        if (action is GenericHookGameAction hookAction)
        {
            return new UndoSerializedActionPayload
            {
                OwnerNetId = action.OwnerId,
                GameActionType = hookAction.ActionType,
                HookId = hookAction.HookId
            };
        }

        if (!action.RecordableToReplay)
            return null;

        try
        {
            return new UndoSerializedActionPayload
            {
                OwnerNetId = action.OwnerId,
                NetAction = UndoSerializationUtil.CloneNetAction(action.ToNetAction())
            };
        }
        catch
        {
            return null;
        }
    }

    private static Action<GameAction>? CreatePopActionDelegate(ActionQueueSet actionQueueSet)
    {
        MethodInfo? popActionMethod = UndoReflectionUtil.FindMethod(actionQueueSet.GetType(), "PopAction");
        return popActionMethod == null
            ? null
            : (Action<GameAction>)Delegate.CreateDelegate(typeof(Action<GameAction>), actionQueueSet, popActionMethod);
    }

    private static void ApplyQueueFlags(object rawQueue, ActionQueueState state)
    {
        UndoReflectionUtil.TrySetFieldValue(rawQueue, "isPaused", state.IsPaused);
        UndoReflectionUtil.TrySetFieldValue(rawQueue, "isCancellingPlayCardActions", false);
        UndoReflectionUtil.TrySetFieldValue(rawQueue, "isCancellingPlayerDrivenCombatActions", false);
        UndoReflectionUtil.TrySetFieldValue(rawQueue, "isCancellingCombatActions", false);
    }

    private static bool TryRestoreQueueEntries(object rawQueue, ActionQueueState state, IReadOnlyDictionary<ulong, Player> playersById, Action<GameAction> popAction, out string? error)
    {
        error = null;
        if (UndoReflectionUtil.FindField(rawQueue.GetType(), "actions")?.GetValue(rawQueue) is not System.Collections.IList actionsList)
        {
            error = "queue_actions_missing";
            return false;
        }

        actionsList.Clear();
        foreach (ActionQueueEntryState entry in state.PendingActions)
        {
            GameAction? action = CreateGameActionFromEntry(entry, playersById);
            if (action == null)
                continue;

            if (entry.ActionRef?.ActionId is not uint actionId)
            {
                error = $"queue_entry_missing_id:{entry.CodecId}";
                return false;
            }

            action.OnEnqueued(popAction, actionId);
            actionsList.Add(action);
        }

        return true;
    }

    private static GameAction? CreateGameActionFromEntry(ActionQueueEntryState entry, IReadOnlyDictionary<ulong, Player> playersById)
    {
        if (entry.Payload == null)
            return null;

        if (entry.Payload.NetAction != null)
        {
            if (!playersById.TryGetValue(entry.Payload.OwnerNetId, out Player? player))
                return null;

            return entry.Payload.NetAction.ToGameAction(player);
        }

        if (entry.Payload.HookId is uint hookId)
        {
            return RunManager.Instance.ActionQueueSynchronizer.GetHookActionForId(
                hookId,
                entry.Payload.OwnerNetId,
                entry.Payload.GameActionType ?? GameActionType.Combat);
        }

        return null;
    }

    private static bool TryRestoreWaitingForResume(ActionQueueSet actionQueueSet, IReadOnlyList<ActionResumeState> waitingForResume)
    {
        if (UndoReflectionUtil.FindField(actionQueueSet.GetType(), "_actionsWaitingForResumption")?.GetValue(actionQueueSet) is not System.Collections.IList rawWaiting)
            return false;

        rawWaiting.Clear();
        Type? waitingType = actionQueueSet.GetType().GetNestedType("ActionWaitingForResumption", BindingFlags.NonPublic);
        if (waitingType == null)
            return false;

        foreach (ActionResumeState waitingState in waitingForResume)
        {
            object waiting = Activator.CreateInstance(waitingType, true)
                ?? throw new InvalidOperationException("Could not create ActionWaitingForResumption.");
            UndoReflectionUtil.TrySetFieldValue(waiting, "oldId", waitingState.OldActionId);
            UndoReflectionUtil.TrySetFieldValue(waiting, "newId", waitingState.NewActionId);
            rawWaiting.Add(waiting);
        }

        return true;
    }
}
