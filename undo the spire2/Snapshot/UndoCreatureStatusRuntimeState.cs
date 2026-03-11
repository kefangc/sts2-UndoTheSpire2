using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace UndoTheSpire2;

// Captures per-creature runtime booleans that affect live combat behavior but
// are not represented by official full state or creature topology.
internal sealed class CreatureStatusRuntimeState
{
    public required string CreatureKey { get; init; }

    public required string CodecId { get; init; }

    public UndoCreatureStatusRuntimePayload? Payload { get; init; }
}

internal abstract class UndoCreatureStatusRuntimePayload
{
    public required string CodecId { get; init; }
}

internal sealed class UndoBoolCreatureStatusRuntimePayload : UndoCreatureStatusRuntimePayload
{
    public bool Value { get; init; }
}

internal sealed class UndoCreatureStatusRestoreContext
{
    public required IReadOnlyDictionary<string, Creature> CreaturesByKey { get; init; }
}