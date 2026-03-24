// 文件说明：保存通用运行时图状态与选择会话状态。
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Enchantments;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace UndoTheSpire2;

internal sealed class UndoPlayerPileCardRuntimeState
{
    public required ulong PlayerNetId { get; init; }

    public required PileType PileType { get; init; }

    public required IReadOnlyList<UndoCardRuntimeState> Cards { get; init; }
}

internal sealed class UndoCardRuntimeState
{
    public int BaseReplayCount { get; init; }

    public bool HasSingleTurnRetain { get; init; }

    public bool HasSingleTurnSly { get; init; }

    public bool ExhaustOnNextPlay { get; init; }

    public CardRef? DeckVersionRef { get; init; }

    public UndoEnchantmentRuntimeState? EnchantmentState { get; init; }

    public UndoAfflictionRuntimeState? AfflictionState { get; init; }

    public IReadOnlyList<UndoComplexRuntimeState> ComplexStates { get; init; } = [];
}

internal sealed class UndoEnchantmentRuntimeState
{
    public SerializableEnchantment? Serializable { get; init; }

    public EnchantmentStatus Status { get; init; }

    public IReadOnlyList<UndoNamedBoolState> BoolProperties { get; init; } = [];

    public IReadOnlyList<UndoNamedIntState> IntProperties { get; init; } = [];

    public IReadOnlyList<UndoNamedEnumState> EnumProperties { get; init; } = [];
}

internal sealed class UndoAfflictionRuntimeState
{
    public IReadOnlyList<UndoNamedBoolState> BoolProperties { get; init; } = [];

    public IReadOnlyList<UndoNamedIntState> IntProperties { get; init; } = [];

    public IReadOnlyList<UndoNamedEnumState> EnumProperties { get; init; } = [];
}

internal sealed class UndoNamedBoolState
{
    public required string Name { get; init; }

    public bool Value { get; init; }
}

internal sealed class UndoNamedIntState
{
    public required string Name { get; init; }

    public int Value { get; init; }
}

internal sealed class UndoNamedEnumState
{
    public required string Name { get; init; }

    public required string EnumTypeName { get; init; }

    public int Value { get; init; }
}

internal sealed class UndoPowerRuntimeState
{
    public required string OwnerCreatureKey { get; init; }

    public required ModelId PowerId { get; init; }

    public required int Ordinal { get; init; }

    public string? TargetCreatureKey { get; init; }

    public string? ApplierCreatureKey { get; init; }

    public SerializableCard? StolenCard { get; init; }

    public SerializableCard? StolenCardDeckVersion { get; init; }

    public IReadOnlyList<ulong> TriggeredPlayerNetIds { get; init; } = [];

    public IReadOnlyList<UndoNamedBoolState> BoolProperties { get; init; } = [];

    public IReadOnlyList<UndoNamedIntState> IntProperties { get; init; } = [];

    public IReadOnlyList<UndoNamedEnumState> EnumProperties { get; init; } = [];

    public IReadOnlyList<UndoComplexRuntimeState> ComplexStates { get; init; } = [];
}

internal sealed class UndoRelicRuntimeState
{
    public required ulong PlayerNetId { get; init; }

    public required ModelId RelicId { get; init; }

    public required int Ordinal { get; init; }

    public RelicStatus Status { get; init; }

    public bool? IsActivating { get; init; }

    public IReadOnlyList<UndoNamedBoolState> BoolProperties { get; init; } = [];

    public IReadOnlyList<UndoNamedIntState> IntProperties { get; init; } = [];

    public IReadOnlyList<UndoNamedEnumState> EnumProperties { get; init; } = [];

    public IReadOnlyList<UndoComplexRuntimeState> ComplexStates { get; init; } = [];
}

internal sealed class UndoSelectionSessionState
{
    public bool HandSelectionActive { get; init; }

    public bool OverlaySelectionActive { get; init; }

    public bool SupportedChoiceUiActive { get; init; }

    public string? OverlayScreenType { get; init; }

    public UndoChoiceSpec? ChoiceSpec { get; init; }
}

internal sealed class UndoFirstInSeriesPlayCountState
{
    public required string CreatureKey { get; init; }

    public int Count { get; init; }
}
