using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;

namespace UndoTheSpire2;

internal static class UndoMonsterTopologyCodecRegistry
{
    public static HashSet<string> GetImplementedCodecIds()
    {
        return [
            "topology:DoorAndDoormaker",
            "topology:Decimillipede",
            "topology:TestSubject",
            "topology:InfestedPrism"
        ];
    }

    public static IReadOnlyList<MonsterTopologyState> Capture(IReadOnlyList<Creature> creatures)
    {
        UndoMonsterTopologyCaptureContext context = new()
        {
            Creatures = creatures
        };

        List<MonsterTopologyState> states = [];
        for (int i = 0; i < creatures.Count; i++)
        {
            Creature creature = creatures[i];
            MonsterModel? monster = creature.Monster;
            if (monster == null)
                continue;

            states.Add(CaptureMonsterTopologyState(monster, creatures, i, context));
        }

        return states;
    }

    public static RestoreCapabilityReport Restore(IReadOnlyList<MonsterTopologyState> states, IReadOnlyList<Creature> creatures)
    {
        if (states.Count == 0)
            return RestoreCapabilityReport.SupportedReport();

        Dictionary<string, Creature> creaturesByKey = UndoStableRefs.BuildCreatureKeyMap(creatures);
        UndoMonsterTopologyRestoreContext context = new()
        {
            Creatures = creatures
        };

        foreach (MonsterTopologyState state in states)
        {
            if (state.CreatureRef == null || !creaturesByKey.TryGetValue(state.CreatureRef.Key, out Creature? creature) || creature.Monster == null)
            {
                return new RestoreCapabilityReport
                {
                    Result = RestoreCapabilityResult.TopologyMismatch,
                    Detail = $"missing_creature:{state.CreatureRef?.Key}"
                };
            }

            RestoreCommonMonsterTopology(creature.Monster, state);
            if (!RestoreCodecState(creature.Monster, state, creaturesByKey, context))
            {
                return new RestoreCapabilityReport
                {
                    Result = RestoreCapabilityResult.TopologyMismatch,
                    Detail = $"topology_codec_failed:{state.RuntimeCodecId ?? "none"}"
                };
            }
        }

        return RestoreCapabilityReport.SupportedReport();
    }

    private static MonsterTopologyState CaptureMonsterTopologyState(MonsterModel monster, IReadOnlyList<Creature> creatures, int index, UndoMonsterTopologyCaptureContext context)
    {
        MonsterMoveStateMachine? moveStateMachine = monster.MoveStateMachine;
        MonsterState? currentState = moveStateMachine == null
            ? null
            : UndoReflectionUtil.FindField(moveStateMachine.GetType(), "_currentState")?.GetValue(moveStateMachine) as MonsterState;
        string? followUpStateType = (currentState as MoveState)?.FollowUpState?.Id;
        bool isHalfDead = monster.Creature.GetPower<DoorRevivalPower>()?.IsHalfDead == true;

        string? runtimeCodecId = null;
        UndoMonsterTopologyRuntimeState? runtimePayload = null;
        IReadOnlyList<CreatureRef> linkedRefs = [];
        switch (monster)
        {
            case Door door:
                runtimeCodecId = "topology:DoorAndDoormaker";
                linkedRefs = [.. CaptureLinkedCreatureRef(creatures, door.Doormaker)];
                runtimePayload = new UndoDoorTopologyRuntimeState
                {
                    CodecId = runtimeCodecId,
                    DoormakerRef = UndoStableRefs.CaptureCreatureRef(creatures, door.Doormaker),
                    DeadStateFollowUpStateId = door.DeadState.FollowUpState?.Id,
                    TimesGotBackIn = door.Doormaker.Monster is Doormaker doormaker ? doormaker.TimesGotBackIn : null
                };
                break;
            case DecimillipedeSegment segment:
                runtimeCodecId = "topology:Decimillipede";
                linkedRefs = creatures
                    .Where(creature => creature.HasPower<ReattachPower>())
                    .Select(creature => UndoStableRefs.CaptureCreatureRef(creatures, creature))
                    .Where(static creatureRef => creatureRef != null)
                    .Cast<CreatureRef>()
                    .ToList();
                runtimePayload = new UndoDecimillipedeTopologyRuntimeState
                {
                    CodecId = runtimeCodecId,
                    StarterMoveIdx = segment.StarterMoveIdx,
                    SegmentRefs = linkedRefs
                };
                break;
            case TestSubject testSubject:
                runtimeCodecId = "topology:TestSubject";
                runtimePayload = new UndoTestSubjectTopologyRuntimeState
                {
                    CodecId = runtimeCodecId,
                    IsReviving = testSubject.Creature.GetPower<AdaptablePower>() != null
                        && UndoReflectionUtil.FindProperty(typeof(AdaptablePower), "IsReviving")?.GetValue(testSubject.Creature.GetPower<AdaptablePower>()) is bool isReviving
                        && isReviving
                };
                break;
            default:
                if (monster.Creature.GetPower<VitalSparkPower>() != null)
                    runtimeCodecId = "topology:InfestedPrism";
                break;
        }

        return new MonsterTopologyState
        {
            CreatureRef = new CreatureRef { Key = UndoStableRefs.BuildCreatureKey(monster.Creature, index) },
            MonsterId = monster.Id,
            SlotName = monster.Creature.SlotName,
            Exists = true,
            IsDead = monster.Creature.IsDead,
            IsHalfDead = isHalfDead,
            CurrentMoveId = currentState?.Id,
            NextMoveId = monster.NextMove?.Id,
            CurrentStateType = currentState?.GetType().FullName,
            FollowUpStateType = followUpStateType,
            LinkedCreatureRefs = linkedRefs,
            RuntimeCodecId = runtimeCodecId,
            RuntimePayload = runtimePayload
        };
    }

    private static IEnumerable<CreatureRef> CaptureLinkedCreatureRef(IReadOnlyList<Creature> creatures, Creature? creature)
    {
        CreatureRef? creatureRef = UndoStableRefs.CaptureCreatureRef(creatures, creature);
        if (creatureRef != null)
            yield return creatureRef;
    }

    private static void RestoreCommonMonsterTopology(MonsterModel monster, MonsterTopologyState state)
    {
        monster.Creature.SlotName = state.SlotName;
        MonsterMoveStateMachine? moveStateMachine = monster.MoveStateMachine;
        if (moveStateMachine == null)
            return;

        if (state.CurrentMoveId != null && moveStateMachine.States.TryGetValue(state.CurrentMoveId, out MonsterState? currentState))
            moveStateMachine.ForceCurrentState(currentState);

        if (state.NextMoveId != null && moveStateMachine.States.TryGetValue(state.NextMoveId, out MonsterState? nextState) && nextState is MoveState moveState)
            monster.SetMoveImmediate(moveState, true);
    }

    private static bool RestoreCodecState(MonsterModel monster, MonsterTopologyState state, IReadOnlyDictionary<string, Creature> creaturesByKey, UndoMonsterTopologyRestoreContext context)
    {
        switch (state.RuntimePayload)
        {
            case UndoDoorTopologyRuntimeState doorState when monster is Door door:
                Creature? doormakerCreature = null;
                if (doorState.DoormakerRef != null && !creaturesByKey.TryGetValue(doorState.DoormakerRef.Key, out doormakerCreature))
                    return false;

                if (doorState.DoormakerRef != null)
                    UndoReflectionUtil.TrySetPropertyValue(door, "Doormaker", doormakerCreature);
                if (door.DeadState != null && doorState.DeadStateFollowUpStateId != null && door.MoveStateMachine.States.TryGetValue(doorState.DeadStateFollowUpStateId, out MonsterState? followUpState) && followUpState is MoveState moveState)
                    door.DeadState.FollowUpState = moveState;
                if (door.Doormaker.Monster is Doormaker doormaker && doorState.TimesGotBackIn.HasValue)
                    UndoReflectionUtil.TrySetPropertyValue(doormaker, "TimesGotBackIn", doorState.TimesGotBackIn.Value);
                return true;
            case UndoDecimillipedeTopologyRuntimeState decimillipedeState when monster is DecimillipedeSegment segment:
                segment.StarterMoveIdx = decimillipedeState.StarterMoveIdx;
                return decimillipedeState.SegmentRefs.All(creatureRef => creaturesByKey.ContainsKey(creatureRef.Key));
            case UndoTestSubjectTopologyRuntimeState:
                return true;
            default:
                return true;
        }
    }
}
