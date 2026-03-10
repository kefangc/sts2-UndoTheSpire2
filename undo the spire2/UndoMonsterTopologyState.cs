using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
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

internal sealed class MonsterTopologyState
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

    public UndoMonsterTopologyRuntimeState? RuntimePayload { get; init; }
}

internal abstract class UndoMonsterTopologyRuntimeState
{
    public required string CodecId { get; init; }
}

internal sealed class UndoDoorTopologyRuntimeState : UndoMonsterTopologyRuntimeState
{
    public CreatureRef? DoormakerRef { get; init; }

    public string? DeadStateFollowUpStateId { get; init; }

    public int? TimesGotBackIn { get; init; }
}

internal sealed class UndoDecimillipedeTopologyRuntimeState : UndoMonsterTopologyRuntimeState
{
    public int StarterMoveIdx { get; init; }

    public IReadOnlyList<CreatureRef> SegmentRefs { get; init; } = [];
}

internal sealed class UndoTestSubjectTopologyRuntimeState : UndoMonsterTopologyRuntimeState
{
    public bool IsReviving { get; init; }
}

internal sealed class UndoMonsterTopologyCaptureContext
{
    public required IReadOnlyList<Creature> Creatures { get; init; }
}

internal sealed class UndoMonsterTopologyRestoreContext
{
    public required IReadOnlyList<Creature> Creatures { get; init; }
}
