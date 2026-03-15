// 文件说明：保存 creature 拓扑与挂载关系快照。
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace UndoTheSpire2;

// Captures live creature-topology state that NetFullCombatState cannot represent,
// especially pet ownership and cross-monster topology links.
internal enum CreatureRole
{
    Player,
    Enemy,
    Pet
}

internal sealed class CreatureTopologyState
{
    public CreatureRef? CreatureRef { get; init; }

    public CreatureRole Role { get; init; } = CreatureRole.Enemy;

    public CombatSide Side { get; init; }

    public ModelId? MonsterId { get; init; }

    public ulong? PetOwnerPlayerNetId { get; init; }

    public string? SlotName { get; init; }

    public bool Exists { get; init; }

    public bool IsDead { get; init; }

    public bool IsHalfDead { get; init; }

    public string? CurrentMoveId { get; init; }

    public string? NextMoveId { get; init; }

    public string? CurrentStateType { get; init; }

    public string? FollowUpStateType { get; init; }

    public IReadOnlyList<CreatureRef> LinkedCreatureRefs { get; init; } = [];

    public string? RuntimeCodecId { get; init; }

    public UndoCreatureTopologyRuntimeState? RuntimePayload { get; init; }
}

internal abstract class UndoCreatureTopologyRuntimeState
{
    public required string CodecId { get; init; }
}

internal sealed class UndoDoorTopologyRuntimeState : UndoCreatureTopologyRuntimeState
{
    public CreatureRef? DoormakerRef { get; init; }

    public string? DeadStateFollowUpStateId { get; init; }

    public int? TimesGotBackIn { get; init; }

    public bool? IsDoorVisible { get; init; }

    public UndoDormantCreatureState? DormantDoormakerState { get; init; }
}

internal sealed class UndoDormantCreatureState
{
    public NetFullCombatState.CreatureState? CreatureState { get; init; }

    public UndoMonsterState? MonsterState { get; init; }
}

internal sealed class UndoDecimillipedeTopologyRuntimeState : UndoCreatureTopologyRuntimeState
{
    public int StarterMoveIdx { get; init; }

    public IReadOnlyList<CreatureRef> SegmentRefs { get; init; } = [];
}

internal sealed class UndoTestSubjectTopologyRuntimeState : UndoCreatureTopologyRuntimeState
{
    public bool IsReviving { get; init; }
}

internal sealed class UndoQueenTopologyRuntimeState : UndoCreatureTopologyRuntimeState
{
    public CreatureRef? AmalgamRef { get; init; }
}

internal sealed class UndoCreatureTopologyCaptureContext
{
    public required IReadOnlyList<Creature> Creatures { get; init; }
}

internal sealed class UndoCreatureTopologyRestoreContext
{
    public required IReadOnlyList<Creature> Creatures { get; init; }
}
