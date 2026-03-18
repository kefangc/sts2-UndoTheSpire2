// 文件说明：捕获和恢复 creature 拓扑、挂载与特殊位置关系。
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace UndoTheSpire2;

// Monster topology covers linked-monster runtime and pet ownership that the
// official full combat snapshot does not preserve.
internal static class UndoCreatureTopologyCodecRegistry
{
    public static HashSet<string> GetImplementedCodecIds()
    {
        return
        [
            "topology:DoorAndDoormaker",
            "topology:Decimillipede",
            "topology:TestSubject",
            "topology:InfestedPrism",
            "topology:QueenAmalgam"
        ];
    }

    public static IReadOnlyList<CreatureTopologyState> Capture(IReadOnlyList<Creature> creatures)
    {
        UndoCreatureTopologyCaptureContext context = new()
        {
            Creatures = creatures
        };

        List<CreatureTopologyState> states = [];
        for (int i = 0; i < creatures.Count; i++)
        {
            Creature creature = creatures[i];
            MonsterModel? monster = creature.Monster;
            if (monster == null)
                continue;

            states.Add(CaptureCreatureTopologyState(monster, creatures, i, context));
        }

        return states;
    }

    public static RestoreCapabilityReport Restore(IReadOnlyList<CreatureTopologyState> states, IReadOnlyList<Creature> creatures)
    {
        if (states.Count == 0)
            return RestoreCapabilityReport.SupportedReport();

        Dictionary<string, Creature> creaturesByKey = UndoStableRefs.BuildCreatureKeyMap(creatures);
        UndoCreatureTopologyRestoreContext context = new()
        {
            Creatures = creatures
        };

        foreach (CreatureTopologyState state in states)
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

            if (!ValidateCreatureRole(creature, state, context))
            {
                return new RestoreCapabilityReport
                {
                    Result = RestoreCapabilityResult.TopologyMismatch,
                    Detail = $"topology_role_mismatch:{state.CreatureRef.Key}:{state.Role}"
                };
            }
        }

        return RestoreCapabilityReport.SupportedReport();
    }

    private static CreatureTopologyState CaptureCreatureTopologyState(MonsterModel monster, IReadOnlyList<Creature> creatures, int index, UndoCreatureTopologyCaptureContext context)
    {
        MonsterMoveStateMachine? moveStateMachine = monster.MoveStateMachine;
        MonsterState? currentState = moveStateMachine == null
            ? null
            : UndoReflectionUtil.FindField(moveStateMachine.GetType(), "_currentState")?.GetValue(moveStateMachine) as MonsterState;
        string? followUpStateType = (currentState as MoveState)?.FollowUpState?.Id;
        bool isHalfDead = monster.Creature.GetPower<DoorRevivalPower>()?.IsHalfDead == true;

        string? runtimeCodecId = null;
        UndoCreatureTopologyRuntimeState? runtimePayload = null;
        IReadOnlyList<CreatureRef> linkedRefs = [];
        switch (monster)
        {
            case Door door:
                runtimeCodecId = "topology:DoorAndDoormaker";
                Creature doormakerCreature = door.Doormaker;
                bool doormakerInCombat = doormakerCreature.CombatState == door.Creature.CombatState
                    && creatures.Contains(doormakerCreature);
                linkedRefs = [.. CaptureLinkedCreatureRef(creatures, doormakerCreature)];
                runtimePayload = new UndoDoorTopologyRuntimeState
                {
                    CodecId = runtimeCodecId,
                    DoormakerRef = UndoStableRefs.CaptureCreatureRef(creatures, doormakerCreature),
                    DeadStateFollowUpStateId = door.DeadState.FollowUpState?.Id,
                    TimesGotBackIn = doormakerCreature.Monster is Doormaker doormaker ? doormaker.TimesGotBackIn : null,
                    IsDoorVisible = TryDeriveDoorVisibilityState(door.Creature.CombatState, doormakerCreature, isHalfDead),
                    DormantDoormakerState = !doormakerInCombat
                        ? CaptureDormantCreatureState(doormakerCreature)
                        : null
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
            case Queen queen:
                runtimeCodecId = "topology:QueenAmalgam";
                Creature? amalgam = UndoReflectionUtil.FindProperty(queen.GetType(), "Amalgam")?.GetValue(queen) as Creature
                    ?? UndoReflectionUtil.FindField(queen.GetType(), "_amalgam")?.GetValue(queen) as Creature;
                linkedRefs = [.. CaptureLinkedCreatureRef(creatures, amalgam)];
                runtimePayload = new UndoQueenTopologyRuntimeState
                {
                    CodecId = runtimeCodecId,
                    AmalgamRef = UndoStableRefs.CaptureCreatureRef(creatures, amalgam)
                };
                break;
            default:
                if (monster.Creature.GetPower<VitalSparkPower>() != null)
                    runtimeCodecId = "topology:InfestedPrism";
                break;
        }

        Creature creature = monster.Creature;
        CreatureRole role = creature.PetOwner != null ? CreatureRole.Pet : CreatureRole.Enemy;
        return new CreatureTopologyState
        {
            CreatureRef = new CreatureRef { Key = UndoStableRefs.BuildCreatureKey(creature, index) },
            Role = role,
            Side = creature.Side,
            MonsterId = monster.Id,
            PetOwnerPlayerNetId = creature.PetOwner?.NetId,
            SlotName = creature.SlotName,
            Exists = true,
            IsDead = creature.IsDead,
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

    private static void RestoreCommonMonsterTopology(MonsterModel monster, CreatureTopologyState state)
    {
        // Topology owns slot and linked-creature relationships only. Move-state
        // restoration is handled by UndoMonsterState plus reconciliation.
        monster.Creature.SlotName = state.SlotName;
    }

    private static bool RestoreCodecState(MonsterModel monster, CreatureTopologyState state, IReadOnlyDictionary<string, Creature> creaturesByKey, UndoCreatureTopologyRestoreContext context)
    {
        switch (state.RuntimePayload)
        {
            case UndoDoorTopologyRuntimeState doorState when monster is Door door:
                Creature? doormakerCreature = null;
                if (doorState.DoormakerRef != null && !creaturesByKey.TryGetValue(doorState.DoormakerRef.Key, out doormakerCreature))
                    return false;

                doormakerCreature ??= doorState.DormantDoormakerState == null
                    ? TryGetDoorDoormaker(door)
                    : RestoreDormantDoormaker(door, doorState.DormantDoormakerState);
                if (doormakerCreature == null)
                    return false;

                UndoReflectionUtil.TrySetPropertyValue(door, "Doormaker", doormakerCreature);
                if (door.DeadState != null && doorState.DeadStateFollowUpStateId != null && door.MoveStateMachine.States.TryGetValue(doorState.DeadStateFollowUpStateId, out MonsterState? followUpState) && followUpState is MoveState moveState)
                    door.DeadState.FollowUpState = moveState;
                if (door.Doormaker.Monster is Doormaker doormaker && doorState.TimesGotBackIn.HasValue)
                    UndoReflectionUtil.TrySetPropertyValue(doormaker, "TimesGotBackIn", doorState.TimesGotBackIn.Value);
                ApplyDoorVisibilityState(door, doorState.IsDoorVisible);
                return true;
            case UndoDecimillipedeTopologyRuntimeState decimillipedeState when monster is DecimillipedeSegment segment:
                segment.StarterMoveIdx = decimillipedeState.StarterMoveIdx;
                return decimillipedeState.SegmentRefs.All(creatureRef => creaturesByKey.ContainsKey(creatureRef.Key));
            case UndoTestSubjectTopologyRuntimeState:
                return true;
            case UndoQueenTopologyRuntimeState queenState when monster is Queen queen:
                return RestoreQueenTopology(queen, queenState, creaturesByKey);
            default:
                return true;
        }
    }

    private static bool ValidateCreatureRole(Creature creature, CreatureTopologyState state, UndoCreatureTopologyRestoreContext context)
    {
        return state.Role switch
        {
            CreatureRole.Pet => ValidatePetTopology(creature, state, context),
            CreatureRole.Enemy => creature.PetOwner == null && creature.Side == CombatSide.Enemy,
            _ => true
        };
    }

    private static bool ValidatePetTopology(Creature creature, CreatureTopologyState state, UndoCreatureTopologyRestoreContext context)
    {
        if (state.PetOwnerPlayerNetId is not ulong ownerNetId)
            return false;

        Player? owner = TryResolvePlayer(ownerNetId, context.Creatures);
        if (owner == null)
            return false;

        return creature.PetOwner == owner
            && creature.Side == owner.Creature.Side
            && owner.PlayerCombatState.Pets.Contains(creature);
    }

    private static Player? TryResolvePlayer(ulong ownerNetId, IReadOnlyList<Creature> creatures)
    {
        return creatures.Select(static creature => creature.Player)
            .FirstOrDefault(player => player?.NetId == ownerNetId);
    }


    private static UndoDormantCreatureState? CaptureDormantCreatureState(Creature creature)
    {
        if (creature.Monster == null)
            return null;

        return new UndoDormantCreatureState
        {
            CreatureState = CaptureCreatureState(creature),
            MonsterState = CaptureDetachedMonsterState(creature)
        };
    }

    private static NetFullCombatState.CreatureState CaptureCreatureState(Creature creature)
    {
        return new NetFullCombatState.CreatureState
        {
            monsterId = creature.Monster?.Id,
            playerId = creature.Player?.NetId,
            currentHp = creature.CurrentHp,
            maxHp = creature.MaxHp,
            block = creature.Block,
            powers = [.. creature.Powers.Select(static power => new NetFullCombatState.PowerState
            {
                id = power.Id,
                amount = power.Amount
            })]
        };
    }

    private static UndoMonsterState? CaptureDetachedMonsterState(Creature creature)
    {
        MonsterModel? monster = creature.Monster;
        MonsterMoveStateMachine? moveStateMachine = monster?.MoveStateMachine;
        if (moveStateMachine == null)
            return null;

        string? currentStateId = UndoReflectionUtil.FindField(moveStateMachine.GetType(), "_currentState")?.GetValue(moveStateMachine) as MonsterState is MonsterState currentState
            ? currentState.Id
            : null;
        bool performedFirstMove = UndoReflectionUtil.FindField(moveStateMachine.GetType(), "_performedFirstMove")?.GetValue(moveStateMachine) is true;
        bool nextMovePerformedAtLeastOnce = monster!.NextMove != null
            && UndoReflectionUtil.FindField(monster.NextMove.GetType(), "_performedAtLeastOnce")?.GetValue(monster.NextMove) is true;
        string? transientNextMoveFollowUpId = monster.NextMove?.Id == MonsterModel.stunnedMoveId
            ? monster.NextMove.FollowUpState?.Id ?? monster.NextMove.FollowUpStateId
            : null;

        return new UndoMonsterState
        {
            CreatureKey = $"dormant:{monster.Id.Entry}",
            SlotName = string.IsNullOrWhiteSpace(creature.SlotName) ? null : creature.SlotName,
            CurrentStateId = currentStateId,
            NextMoveId = monster.NextMove?.Id,
            IsHovering = false,
            SpawnedThisTurn = monster.SpawnedThisTurn,
            PerformedFirstMove = performedFirstMove,
            NextMovePerformedAtLeastOnce = nextMovePerformedAtLeastOnce,
            TransientNextMoveFollowUpId = transientNextMoveFollowUpId,
            SpecialNodeStateKey = null,
            StateLogIds = [.. moveStateMachine.StateLog.Select(static state => state.Id)]
        };
    }

    private static bool? TryDeriveDoorVisibilityState(CombatState? combatState, Creature? doormakerCreature, bool isHalfDead)
    {
        if (!isHalfDead)
            return true;

        bool doormakerInCombat = combatState != null
            && doormakerCreature?.CombatState == combatState
            && combatState.ContainsCreature(doormakerCreature);
        return !doormakerInCombat;
    }

    private static void ApplyDoorVisibilityState(Door door, bool? isDoorVisible)
    {
        if (!isDoorVisible.HasValue)
            return;

        if (isDoorVisible.Value)
            door.Close();
        else
            door.Open();
    }

    private static void RemoveCreatureNode(NCombatRoom? combatRoom, NCreature creatureNode)
    {
        if (combatRoom == null || !GodotObject.IsInstanceValid(creatureNode))
            return;

        MethodInfo? removeNodeMethod = UndoReflectionUtil.FindMethod(combatRoom.GetType(), "RemoveCreatureNode");
        if (removeNodeMethod != null)
        {
            removeNodeMethod.Invoke(combatRoom, [creatureNode]);
            return;
        }

        creatureNode.QueueFree();
    }

    private static Creature? TryGetDoorDoormaker(Door door)
    {
        return UndoReflectionUtil.FindProperty(door.GetType(), "Doormaker")?.GetValue(door) as Creature
            ?? UndoReflectionUtil.FindField(door.GetType(), "_doormaker")?.GetValue(door) as Creature;
    }

    private static Creature? RestoreDormantDoormaker(Door door, UndoDormantCreatureState dormantState)
    {
        CombatState? combatState = door.Creature.CombatState;
        if (combatState == null)
            return null;

        Creature? currentDoormaker = TryGetDoorDoormaker(door);
        if (currentDoormaker?.CombatState == combatState)
        {
            NCombatRoom? combatRoom = NCombatRoom.Instance;
            NCreature? currentNode = combatRoom?.GetCreatureNode(currentDoormaker);
            if (currentNode != null)
                RemoveCreatureNode(combatRoom, currentNode);

            if (combatState.ContainsCreature(currentDoormaker))
                combatState.RemoveCreature(currentDoormaker, unattach: false);
            CombatManager.Instance.RemoveCreature(currentDoormaker);
        }

        MonsterModel monster = ModelDb.Monster<Doormaker>().ToMutable();
        Creature doormaker = combatState.CreateCreature(monster, CombatSide.Enemy, dormantState.MonsterState?.SlotName ?? "doormaker");

        if (dormantState.CreatureState != null)
            RestoreSnapshotCreatureState(doormaker, dormantState.CreatureState.Value);
        if (dormantState.MonsterState != null && doormaker.Monster != null)
            RestoreDetachedMonsterState(doormaker.Monster, dormantState.MonsterState);
        return doormaker;
    }

    private static void RestoreSnapshotCreatureState(Creature creature, NetFullCombatState.CreatureState saved)
    {
        creature.SetMaxHpInternal(saved.maxHp);
        creature.SetCurrentHpInternal(saved.currentHp);
        if (creature.Block < saved.block)
            creature.GainBlockInternal(saved.block - creature.Block);
        else if (creature.Block > saved.block)
            creature.LoseBlockInternal(creature.Block - saved.block);

        List<PowerModel> remainingCurrentPowers = creature.Powers.ToList();
        foreach (NetFullCombatState.PowerState powerState in saved.powers)
        {
            PowerModel? existingPower = remainingCurrentPowers.FirstOrDefault(power => power.Id == powerState.id);
            if (existingPower != null)
            {
                remainingCurrentPowers.Remove(existingPower);
                existingPower.SetAmount(powerState.amount, true);
                existingPower.AmountOnTurnStart = existingPower.Amount;
                continue;
            }

            PowerModel power = ModelDb.GetById<PowerModel>(powerState.id).ToMutable();
            power.ApplyInternal(creature, powerState.amount, true);
            power.AmountOnTurnStart = power.Amount;
        }

        foreach (PowerModel power in remainingCurrentPowers)
            power.RemoveInternal();
    }

    private static void RestoreDetachedMonsterState(MonsterModel monster, UndoMonsterState state)
    {
        MonsterMoveStateMachine? moveStateMachine = monster.MoveStateMachine;
        if (moveStateMachine == null)
            return;

        monster.Creature.SlotName = state.SlotName;
        UndoReflectionUtil.TrySetPropertyValue(monster, "SpawnedThisTurn", state.SpawnedThisTurn);
        UndoReflectionUtil.TrySetFieldValue(moveStateMachine, "_performedFirstMove", state.PerformedFirstMove);
        if (moveStateMachine.StateLog is List<MonsterState> stateLog)
        {
            stateLog.Clear();
            foreach (string stateId in state.StateLogIds)
            {
                if (moveStateMachine.States.TryGetValue(stateId, out MonsterState? loggedState))
                    stateLog.Add(loggedState);
            }
        }

        if ((monster.Creature.IsDead || monster.Creature.GetPower<ReattachPower>() is ReattachPower reattachPower
                && UndoReflectionUtil.FindProperty(reattachPower.GetType(), "IsReviving")?.GetValue(reattachPower) is bool isReviving
                && isReviving)
            && UndoReflectionUtil.FindProperty(monster.GetType(), "DeadState")?.GetValue(monster) is MoveState deadState)
        {
            if (UndoMonsterMoveStateUtil.ShouldKeepDeadState(moveStateMachine, state, deadState))
            {
                monster.SetMoveImmediate(deadState, true);
                moveStateMachine.ForceCurrentState(deadState);
                UndoReflectionUtil.TrySetFieldValue(deadState, "_performedAtLeastOnce", state.NextMovePerformedAtLeastOnce);
                return;
            }
        }

        if (state.CurrentStateId != null && moveStateMachine.States.TryGetValue(state.CurrentStateId, out MonsterState? currentState))
            moveStateMachine.ForceCurrentState(currentState);

        if (state.NextMoveId == MonsterModel.stunnedMoveId)
            return;

        if (state.NextMoveId != null && moveStateMachine.States.TryGetValue(state.NextMoveId, out MonsterState? nextState) && nextState is MoveState moveState)
        {
            monster.SetMoveImmediate(moveState, true);
            if (state.CurrentStateId == null)
                moveStateMachine.ForceCurrentState(moveState);
            UndoReflectionUtil.TrySetFieldValue(moveState, "_performedAtLeastOnce", state.NextMovePerformedAtLeastOnce);
        }
    }

    private static bool RestoreQueenTopology(Queen queen, UndoQueenTopologyRuntimeState state, IReadOnlyDictionary<string, Creature> creaturesByKey)
    {
        Creature? amalgam = null;
        if (state.AmalgamRef != null && !creaturesByKey.TryGetValue(state.AmalgamRef.Key, out amalgam))
            return false;

        Creature? currentAmalgam = TryGetQueenAmalgam(queen);
        if (currentAmalgam != null)
            TryUnwireQueenAmalgamDeathHook(queen, currentAmalgam);

        UndoReflectionUtil.TrySetPropertyValue(queen, "Amalgam", amalgam);
        if (amalgam != null && !amalgam.IsDead)
            TryWireQueenAmalgamDeathHook(queen, amalgam);

        return true;
    }

    private static Creature? TryGetQueenAmalgam(Queen queen)
    {
        return UndoReflectionUtil.FindProperty(queen.GetType(), "Amalgam")?.GetValue(queen) as Creature
            ?? UndoReflectionUtil.FindField(queen.GetType(), "_amalgam")?.GetValue(queen) as Creature;
    }

    private static void TryWireQueenAmalgamDeathHook(Queen queen, Creature amalgam)
    {
        Action<Creature>? handler = CreateQueenAmalgamDeathHandler(queen);
        if (handler != null)
            amalgam.Died += handler;
    }

    private static void TryUnwireQueenAmalgamDeathHook(Queen queen, Creature amalgam)
    {
        Action<Creature>? handler = CreateQueenAmalgamDeathHandler(queen);
        if (handler != null)
            amalgam.Died -= handler;
    }

    private static Action<Creature>? CreateQueenAmalgamDeathHandler(Queen queen)
    {
        MethodInfo? method = UndoReflectionUtil.FindMethod(queen.GetType(), "AmalgamDeathResponse");
        if (method == null)
            return null;

        return Delegate.CreateDelegate(typeof(Action<Creature>), queen, method, throwOnBindFailure: false) as Action<Creature>;
    }
}










