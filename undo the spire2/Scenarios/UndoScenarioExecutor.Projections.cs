using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace UndoTheSpire2;

internal static partial class UndoScenarioExecutor
{
    private static bool TryGetLocalOstyVisualState(out Player? owner, out Creature? osty, out NCreature? ownerNode, out NCreature? ostyNode, out string detail)
    {
        owner = null;
        osty = null;
        ownerNode = null;
        ostyNode = null;
        detail = "osty_missing";

        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        NCombatRoom? combatRoom = NCombatRoom.Instance;
        if (combatState == null || combatRoom == null)
        {
            detail = combatState == null ? "combat_state_missing" : "combat_room_missing";
            return false;
        }

        owner = LocalContext.GetMe(combatState);
        Player? localOwner = owner;
        if (localOwner == null || !IsLocalNecrobinder(localOwner))
        {
            detail = "local_necrobinder_required";
            return false;
        }

        osty = combatState.Allies.FirstOrDefault(creature => creature.PetOwner == localOwner && creature.Monster is Osty);
        if (osty == null)
        {
            detail = "osty_creature_missing";
            return false;
        }

        ownerNode = combatRoom.GetCreatureNode(owner.Creature);
        ostyNode = combatRoom.GetCreatureNode(osty);
        if (ownerNode == null || ostyNode == null)
        {
            detail = ownerNode == null ? "owner_node_missing" : "osty_node_missing";
            return false;
        }

        detail = "matched";
        return true;
    }

    private static bool IsLocalNecrobinder(Player player)
    {
        return string.Equals(player.Character.GetType().Name, "Necrobinder", StringComparison.Ordinal);
    }

    private static bool ReadPrivateBool(object target, string propertyName)
    {
        if (UndoReflectionUtil.FindProperty(target.GetType(), propertyName)?.GetValue(target) is bool propertyValue)
            return propertyValue;

        string fieldName = '_' + char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
        return UndoReflectionUtil.FindField(target.GetType(), fieldName)?.GetValue(target) is bool fieldValue && fieldValue;
    }

    private static object ProjectCreatureStatusRuntime(IReadOnlyList<CreatureStatusRuntimeState> states)
    {
        return states
            .OrderBy(static state => state.CreatureKey, StringComparer.Ordinal)
            .Select(state => new
            {
                state.CreatureKey,
                state.CodecId,
                Payload = state.Payload switch
                {
                    UndoBoolCreatureStatusRuntimePayload boolPayload => (object?)new { boolPayload.CodecId, boolPayload.Value },
                    UndoIntCreatureStatusRuntimePayload intPayload => (object?)new { intPayload.CodecId, intPayload.Value },
                    UndoCreatureStatusRuntimePayload payload => (object?)new { payload.CodecId },
                    _ => null
                }
            })
            .ToList();
    }

    private static object ProjectTopology(IReadOnlyList<CreatureTopologyState> states)
    {
        return states
            .OrderBy(static state => state.CreatureRef?.Key, StringComparer.Ordinal)
            .Select(state => new
            {
                CreatureKey = state.CreatureRef?.Key,
                state.Role,
                state.Side,
                MonsterId = state.MonsterId?.Entry,
                state.PetOwnerPlayerNetId,
                state.SlotName,
                state.Exists,
                state.IsDead,
                state.IsHalfDead,
                state.CurrentMoveId,
                state.NextMoveId,
                state.CurrentStateType,
                state.FollowUpStateType,
                LinkedCreatureRefs = state.LinkedCreatureRefs.Select(static linked => linked.Key).OrderBy(static key => key, StringComparer.Ordinal).ToList(),
                state.RuntimeCodecId,
                RuntimePayload = ProjectRuntimePayload(state.RuntimePayload)
            })
            .ToList();
    }

    private static object? ProjectRuntimePayload(UndoCreatureTopologyRuntimeState? payload)
    {
        return payload switch
        {
            UndoDoorTopologyRuntimeState door => new
            {
                door.CodecId,
                DoormakerRef = door.DoormakerRef?.Key,
                door.DeadStateFollowUpStateId,
                door.TimesGotBackIn,
                door.IsDoorVisible
            },
            UndoDecimillipedeTopologyRuntimeState decimillipede => new
            {
                decimillipede.CodecId,
                decimillipede.StarterMoveIdx,
                SegmentRefs = decimillipede.SegmentRefs.Select(static segment => segment.Key).OrderBy(static key => key, StringComparer.Ordinal).ToList()
            },
            UndoTestSubjectTopologyRuntimeState testSubject => new
            {
                testSubject.CodecId,
                testSubject.IsReviving
            },
            UndoCreatureTopologyRuntimeState topologyPayload => new
            {
                topologyPayload.CodecId
            },
            _ => null
        };
    }
}
