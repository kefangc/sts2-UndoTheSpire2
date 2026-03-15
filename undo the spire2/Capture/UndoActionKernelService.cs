// 文件说明：捕获官方 action queue、paused choice 和执行边界的内核状态。
using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
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
        GameAction? currentAction = RunManager.Instance.ActionExecutor.CurrentlyRunningAction;
        GameAction? effectiveAction = currentAction?.State == GameActionState.GatheringPlayerChoice
            ? currentAction
            : FindPausedChoiceAction(actionQueueSet, activeChoiceSpec);
        ActionKernelBoundaryKind boundaryKind = DetermineBoundaryKind(effectiveAction, activeChoiceSpec);
        IReadOnlyList<ActionResumeState> waitingForResume = boundaryKind == ActionKernelBoundaryKind.PausedChoice
            ? CaptureWaitingForResumeStates(actionQueueSet)
            : [];

        List<ActionQueueState> queueStates = [];
        if (UndoReflectionUtil.FindField(actionQueueSet.GetType(), "_actionQueues")?.GetValue(actionQueueSet) is System.Collections.IEnumerable rawQueues)
        {
            foreach (object rawQueue in rawQueues)
                queueStates.Add(CaptureQueueState(rawQueue, boundaryKind, effectiveAction));
        }

        bool persistCurrentAction = boundaryKind == ActionKernelBoundaryKind.PausedChoice;
        return new ActionKernelState
        {
            BoundaryKind = boundaryKind,
            CurrentActionTypeName = persistCurrentAction ? effectiveAction?.GetType().FullName : null,
            CurrentActionState = persistCurrentAction ? effectiveAction?.State : null,
            CurrentActionRef = persistCurrentAction ? CaptureActionRef(effectiveAction) : null,
            CurrentActionCodecId = persistCurrentAction ? TryGetActionCodecId(effectiveAction) : null,
            CurrentActionPayload = persistCurrentAction ? TryCaptureActionPayload(effectiveAction) : null,
            CurrentHookActionRef = persistCurrentAction && effectiveAction is GenericHookGameAction hookAction
                ? new ActionRef { HookId = hookAction.HookId, TypeName = hookAction.GetType().FullName }
                : null,
            PausedChoiceState = boundaryKind == ActionKernelBoundaryKind.PausedChoice
                ? CapturePausedChoiceState(runState, effectiveAction, activeChoiceSpec, waitingForResume)
                : null,
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

        if (state.BoundaryKind == ActionKernelBoundaryKind.UnsupportedLiveAction)
        {
            return new RestoreCapabilityReport
            {
                Result = RestoreCapabilityResult.UnsupportedLiveAction,
                Detail = "unsupported_live_action_boundary"
            };
        }

        if (!ValidateStateForRestore(state, out string? validationError))
        {
            return new RestoreCapabilityReport
            {
                Result = RestoreCapabilityResult.QueueStateMismatch,
                Detail = validationError
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
            ApplyQueueFlags(rawQueues[i], state.Queues[i], state.BoundaryKind);
            if (!TryRestoreQueueEntries(rawQueues[i], state.Queues[i], playersById, popAction, state.BoundaryKind, out string? error))
            {
                return new RestoreCapabilityReport
                {
                    Result = error != null && error.StartsWith("queue_state_", StringComparison.Ordinal)
                        ? RestoreCapabilityResult.QueueStateMismatch
                        : RestoreCapabilityResult.UnsupportedOfficialPattern,
                    Detail = error
                };
            }
        }

        if (state.BoundaryKind == ActionKernelBoundaryKind.PausedChoice)
        {
            if (!TryRestoreWaitingForResume(actionQueueSet, state.WaitingForResumption))
            {
                return new RestoreCapabilityReport
                {
                    Result = RestoreCapabilityResult.UnsupportedOfficialPattern,
                    Detail = "resume_queue_restore_failed"
                };
            }

            return state.PausedChoiceState == null
                ? new RestoreCapabilityReport
                {
                    Result = RestoreCapabilityResult.QueueStateMismatch,
                    Detail = "queue_state_paused_choice_missing"
                }
                : UndoActionCodecRegistry.EvaluateCapability(state.PausedChoiceState);
        }

        if (!TryRestoreWaitingForResume(actionQueueSet, []))
        {
            return new RestoreCapabilityReport
            {
                Result = RestoreCapabilityResult.UnsupportedOfficialPattern,
                Detail = "resume_queue_clear_failed"
            };
        }

        return RestoreCapabilityReport.SupportedReport();
    }

    private static ActionKernelBoundaryKind DetermineBoundaryKind(GameAction? currentAction, UndoChoiceSpec? activeChoiceSpec)
    {
        if (currentAction?.State == GameActionState.GatheringPlayerChoice)
            return activeChoiceSpec == null ? ActionKernelBoundaryKind.UnsupportedLiveAction : ActionKernelBoundaryKind.PausedChoice;

        if (currentAction == null)
            return ActionKernelBoundaryKind.StableBoundary;

        return currentAction.State == GameActionState.Executing
            ? ActionKernelBoundaryKind.StableBoundary
            : ActionKernelBoundaryKind.UnsupportedLiveAction;
    }

    private static GameAction? FindPausedChoiceAction(ActionQueueSet actionQueueSet, UndoChoiceSpec? activeChoiceSpec)
    {
        if (activeChoiceSpec == null)
            return null;

        if (UndoReflectionUtil.FindField(actionQueueSet.GetType(), "_actionQueues")?.GetValue(actionQueueSet) is not System.Collections.IEnumerable rawQueues)
            return null;

        GameAction? firstPausedChoice = null;
        ulong? localNetId = LocalContext.NetId;
        foreach (object rawQueue in rawQueues)
        {
            if (UndoReflectionUtil.FindField(rawQueue.GetType(), "actions")?.GetValue(rawQueue) is not System.Collections.IList actions
                || actions.Count == 0
                || actions[0] is not GameAction frontAction
                || frontAction.State != GameActionState.GatheringPlayerChoice)
            {
                continue;
            }

            if (localNetId != null && frontAction.OwnerId == localNetId.Value)
                return frontAction;

            firstPausedChoice ??= frontAction;
        }

        return firstPausedChoice;
    }

    private static ActionQueueState CaptureQueueState(object rawQueue, ActionKernelBoundaryKind boundaryKind, GameAction? currentAction)
    {
        List<ActionQueueEntryState> pendingActions = [];
        if (UndoReflectionUtil.FindField(rawQueue.GetType(), "actions")?.GetValue(rawQueue) is System.Collections.IEnumerable rawActions)
        {
            foreach (object rawAction in rawActions)
            {
                if (rawAction is not GameAction action)
                    continue;

                if (!ShouldPersistQueueAction(action, boundaryKind, currentAction))
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

    private static bool ShouldPersistQueueAction(GameAction action, ActionKernelBoundaryKind boundaryKind, GameAction? currentAction)
    {
        if (boundaryKind == ActionKernelBoundaryKind.StableBoundary)
        {
            if (ReferenceEquals(action, currentAction) || action.State == GameActionState.Executing)
                return false;

            return IsQueueStateAllowed(action.State, allowGatheringPlayerChoice: false);
        }

        if (boundaryKind == ActionKernelBoundaryKind.PausedChoice)
            return IsQueueStateAllowed(action.State, allowGatheringPlayerChoice: true);

        return false;
    }

    private static IReadOnlyList<ActionResumeState> CaptureWaitingForResumeStates(ActionQueueSet actionQueueSet)
    {
        List<ActionResumeState> states = [];
        if (UndoReflectionUtil.FindField(actionQueueSet.GetType(), "_actionsWaitingForResumption")?.GetValue(actionQueueSet) is not System.Collections.IEnumerable rawWaiting)
            return states;

        foreach (object rawState in rawWaiting)
        {
            if (UndoReflectionUtil.FindField(rawState.GetType(), "oldId")?.GetValue(rawState) is not uint oldId
                || UndoReflectionUtil.FindField(rawState.GetType(), "newId")?.GetValue(rawState) is not uint newId)
                continue;

            states.Add(new ActionResumeState
            {
                OldActionId = oldId,
                NewActionId = newId
            });
        }

        return states;
    }

    private static PausedChoiceState? CapturePausedChoiceState(
        RunState runState,
        GameAction? currentAction,
        UndoChoiceSpec? activeChoiceSpec,
        IReadOnlyList<ActionResumeState> waitingForResume)
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

        uint? actionId = currentAction.Id;
        uint? resumeActionId = actionId == null
            ? null
            : waitingForResume.FirstOrDefault(state => state.OldActionId == actionId.Value)?.NewActionId;
        uint? choiceId = ownerPlayer == null ? null : TryGetCurrentPendingChoiceId(runState, ownerPlayer);
        UndoSerializedActionPayload? payload = TryCaptureActionPayload(currentAction);
        return new PausedChoiceState
        {
            ChoiceKind = activeChoiceSpec.Kind,
            OwnerNetId = ownerPlayer?.NetId,
            ChoiceId = choiceId,
            Prompt = activeChoiceSpec.SelectionPrefs.Prompt?.ToString(),
            MinSelections = activeChoiceSpec.SelectionPrefs.MinSelect,
            MaxSelections = activeChoiceSpec.SelectionPrefs.MaxSelect,
            CandidateCardRefs = candidateCards,
            SourceActionRef = CaptureActionRef(currentAction),
            SourceActionCodecId = DeterminePausedChoiceCodecId(currentAction, activeChoiceSpec),
            SourceActionPayload = payload,
            ResumeActionId = resumeActionId,
            ResumeToken = choiceId,
            ChoiceSpec = activeChoiceSpec
        };
    }

    private static uint? TryGetCurrentPendingChoiceId(RunState runState, Player player)
    {
        int slot = runState.GetPlayerSlotIndex(player);
        IReadOnlyList<uint> choiceIds = RunManager.Instance.PlayerChoiceSynchronizer.ChoiceIds;
        if (slot < 0 || slot >= choiceIds.Count || choiceIds[slot] == 0)
            return null;

        return choiceIds[slot] - 1;
    }

    private static string DeterminePausedChoiceCodecId(GameAction action, UndoChoiceSpec choiceSpec)
    {
        if (action is GenericHookGameAction)
            return "action:hook-choice";

        return choiceSpec.Kind switch
        {
            UndoChoiceKind.HandSelection => "action:from-hand",
            UndoChoiceKind.ChooseACard => "action:choose-a-card",
            UndoChoiceKind.SimpleGridSelection => "action:simple-grid",
            _ => "action:opaque-choice"
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

    private static void ApplyQueueFlags(object rawQueue, ActionQueueState state, ActionKernelBoundaryKind boundaryKind)
    {
        bool shouldBePaused = boundaryKind == ActionKernelBoundaryKind.PausedChoice && state.IsPaused;
        UndoReflectionUtil.TrySetFieldValue(rawQueue, "isPaused", shouldBePaused);
        UndoReflectionUtil.TrySetFieldValue(rawQueue, "isCancellingPlayCardActions", false);
        UndoReflectionUtil.TrySetFieldValue(rawQueue, "isCancellingPlayerDrivenCombatActions", false);
        UndoReflectionUtil.TrySetFieldValue(rawQueue, "isCancellingCombatActions", false);
    }

    private static bool TryRestoreQueueEntries(
        object rawQueue,
        ActionQueueState state,
        IReadOnlyDictionary<ulong, Player> playersById,
        Action<GameAction> popAction,
        ActionKernelBoundaryKind boundaryKind,
        out string? error)
    {
        error = null;
        if (UndoReflectionUtil.FindField(rawQueue.GetType(), "actions")?.GetValue(rawQueue) is not System.Collections.IList actionsList)
        {
            error = "queue_actions_missing";
            return false;
        }

        bool allowGatheringPlayerChoice = boundaryKind == ActionKernelBoundaryKind.PausedChoice;
        actionsList.Clear();
        foreach (ActionQueueEntryState entry in state.PendingActions)
        {
            if (!IsQueueStateAllowed(entry.State, allowGatheringPlayerChoice))
            {
                error = $"queue_state_invalid:{entry.State}";
                return false;
            }

            GameAction? action = CreateGameActionFromEntry(entry, playersById);
            if (action == null)
                continue;

            if (entry.ActionRef?.ActionId is not uint actionId)
            {
                error = $"queue_entry_missing_id:{entry.CodecId}";
                return false;
            }

            action.OnEnqueued(popAction, actionId);
            UndoReflectionUtil.TrySetPropertyValue(action, "State", entry.State);
            actionsList.Add(action);
        }

        return true;
    }

    private static bool IsQueueStateAllowed(GameActionState state, bool allowGatheringPlayerChoice)
    {
        return state == GameActionState.WaitingForExecution
            || state == GameActionState.ReadyToResumeExecuting
            || (allowGatheringPlayerChoice && state == GameActionState.GatheringPlayerChoice);
    }

    private static bool ValidateStateForRestore(ActionKernelState state, out string? error)
    {
        error = null;
        bool pausedChoice = state.BoundaryKind == ActionKernelBoundaryKind.PausedChoice;
        if (!pausedChoice && state.PausedChoiceState != null)
        {
            error = "queue_state_paused_choice_in_stable_boundary";
            return false;
        }

        if (pausedChoice && state.PausedChoiceState == null)
        {
            error = "queue_state_paused_choice_missing";
            return false;
        }

        foreach (ActionQueueState queue in state.Queues)
        {
            foreach (ActionQueueEntryState entry in queue.PendingActions)
            {
                if (!IsQueueStateAllowed(entry.State, pausedChoice))
                {
                    error = $"queue_state_invalid:{entry.State}";
                    return false;
                }
            }
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

