using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace UndoTheSpire2;

internal sealed class MonsterTopologyState
{
    public CreatureRef? CreatureRef { get; init; }

    public ModelId? MonsterId { get; init; }

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
