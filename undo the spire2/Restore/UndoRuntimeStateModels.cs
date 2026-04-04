using MegaCrit.Sts2.Core.Saves.Runs;

namespace UndoTheSpire2;

internal abstract class UndoComplexRuntimeState
{
    public required string CodecId { get; init; }
}

internal sealed class UndoIntRuntimeComplexState : UndoComplexRuntimeState
{
    public int Value { get; init; }
}

internal sealed class UndoBoolRuntimeComplexState : UndoComplexRuntimeState
{
    public bool Value { get; init; }
}

internal sealed class UndoPairIntRuntimeComplexState : UndoComplexRuntimeState
{
    public int FirstValue { get; init; }

    public int SecondValue { get; init; }
}

internal sealed class UndoPairDecimalRuntimeComplexState : UndoComplexRuntimeState
{
    public decimal FirstValue { get; init; }

    public decimal SecondValue { get; init; }
}

internal sealed class UndoSovereignBladeRuntimeComplexState : UndoComplexRuntimeState
{
    public decimal CurrentDamage { get; init; }

    public decimal CurrentRepeats { get; init; }

    public bool CreatedThroughForge { get; init; }
}

internal sealed class UndoCardRefRuntimeComplexState : UndoComplexRuntimeState
{
    public CardRef? Card { get; init; }
}

internal sealed class UndoCardPlayRuntimeComplexState : UndoComplexRuntimeState
{
    public UndoCardPlayState? CardPlay { get; init; }
}

internal sealed class UndoDetachedCardRuntimeComplexState : UndoComplexRuntimeState
{
    public SerializableCard? Card { get; init; }
}

internal sealed class UndoCreatureSetRuntimeComplexState : UndoComplexRuntimeState
{
    public IReadOnlyList<CreatureRef> Creatures { get; init; } = [];
}

internal sealed class UndoCardIntMapEntry
{
    public required CardRef Card { get; init; }

    public int Value { get; init; }
}

internal sealed class UndoCardIntMapRuntimeComplexState : UndoComplexRuntimeState
{
    public IReadOnlyList<UndoCardIntMapEntry> Entries { get; init; } = [];
}

internal sealed class UndoNamedIntRuntimeEntry
{
    public required string Name { get; init; }

    public int Value { get; init; }
}

internal sealed class UndoNamedIntFieldsRuntimeComplexState : UndoComplexRuntimeState
{
    public IReadOnlyList<UndoNamedIntRuntimeEntry> Entries { get; init; } = [];
}

internal sealed class UndoNamedDecimalRuntimeEntry
{
    public required string Name { get; init; }

    public decimal Value { get; init; }
}

internal sealed class UndoNamedDecimalFieldsRuntimeComplexState : UndoComplexRuntimeState
{
    public IReadOnlyList<UndoNamedDecimalRuntimeEntry> Entries { get; init; } = [];
}

internal sealed class UndoNamedCardRefRuntimeEntry
{
    public required string Name { get; init; }

    public CardRef? Card { get; init; }
}

internal sealed class UndoNamedCardRefFieldsRuntimeComplexState : UndoComplexRuntimeState
{
    public IReadOnlyList<UndoNamedCardRefRuntimeEntry> Entries { get; init; } = [];
}

internal sealed class UndoNamedCardPlayRuntimeEntry
{
    public required string Name { get; init; }

    public UndoCardPlayState? CardPlay { get; init; }
}

internal sealed class UndoNamedCardPlayFieldsRuntimeComplexState : UndoComplexRuntimeState
{
    public IReadOnlyList<UndoNamedCardPlayRuntimeEntry> Entries { get; init; } = [];
}

internal sealed class UndoNamedPowerRefRuntimeEntry
{
    public required string Name { get; init; }

    public required PowerRef Power { get; init; }
}

internal sealed class UndoNamedPowerRefCollectionRuntimeEntry
{
    public required string Name { get; init; }

    public IReadOnlyList<UndoNamedPowerRefRuntimeEntry> Entries { get; init; } = [];
}

internal sealed class UndoNamedPowerRefCollectionsRuntimeComplexState : UndoComplexRuntimeState
{
    public IReadOnlyList<UndoNamedPowerRefCollectionRuntimeEntry> Collections { get; init; } = [];
}

internal sealed class UndoPowerScalarFieldsRuntimeComplexState : UndoComplexRuntimeState
{
    public IReadOnlyList<UndoNamedBoolState> DirectBoolFields { get; init; } = [];

    public IReadOnlyList<UndoNamedIntState> DirectIntFields { get; init; } = [];

    public IReadOnlyList<UndoNamedDecimalRuntimeEntry> DirectDecimalFields { get; init; } = [];

    public IReadOnlyList<UndoNamedEnumState> DirectEnumFields { get; init; } = [];

    public IReadOnlyList<UndoNamedBoolState> InternalBoolFields { get; init; } = [];

    public IReadOnlyList<UndoNamedIntState> InternalIntFields { get; init; } = [];

    public IReadOnlyList<UndoNamedDecimalRuntimeEntry> InternalDecimalFields { get; init; } = [];

    public IReadOnlyList<UndoNamedEnumState> InternalEnumFields { get; init; } = [];
}

internal sealed class UndoPowerCardRefFieldsRuntimeComplexState : UndoComplexRuntimeState
{
    public IReadOnlyList<UndoNamedCardRefRuntimeEntry> DirectEntries { get; init; } = [];

    public IReadOnlyList<UndoNamedCardRefRuntimeEntry> InternalEntries { get; init; } = [];
}

internal sealed class UndoPowerCardPlayFieldsRuntimeComplexState : UndoComplexRuntimeState
{
    public IReadOnlyList<UndoNamedCardPlayRuntimeEntry> DirectEntries { get; init; } = [];

    public IReadOnlyList<UndoNamedCardPlayRuntimeEntry> InternalEntries { get; init; } = [];
}

internal sealed class UndoPowerCardRefCollectionsRuntimeComplexState : UndoComplexRuntimeState
{
    public IReadOnlyList<UndoNamedCardRefCollectionRuntimeEntry> DirectCollections { get; init; } = [];

    public IReadOnlyList<UndoNamedCardRefCollectionRuntimeEntry> InternalCollections { get; init; } = [];
}

internal sealed class UndoRelicScalarFieldsRuntimeComplexState : UndoComplexRuntimeState
{
    public IReadOnlyList<UndoNamedBoolState> BoolFields { get; init; } = [];

    public IReadOnlyList<UndoNamedIntState> IntFields { get; init; } = [];

    public IReadOnlyList<UndoNamedDecimalRuntimeEntry> DecimalFields { get; init; } = [];

    public IReadOnlyList<UndoNamedEnumState> EnumFields { get; init; } = [];
}

internal sealed class UndoNamedCardRefCollectionRuntimeEntry
{
    public required string Name { get; init; }

    public IReadOnlyList<CardRef> Cards { get; init; } = [];
}

internal sealed class UndoNamedCardRefCollectionsRuntimeComplexState : UndoComplexRuntimeState
{
    public IReadOnlyList<UndoNamedCardRefCollectionRuntimeEntry> Collections { get; init; } = [];
}

internal sealed class UndoDampenRuntimeComplexState : UndoComplexRuntimeState
{
    public IReadOnlyList<CreatureRef> Casters { get; init; } = [];

    public IReadOnlyList<UndoCardIntMapEntry> DowngradedCards { get; init; } = [];
}
