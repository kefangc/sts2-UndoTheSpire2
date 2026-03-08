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
    public bool HasSingleTurnRetain { get; init; }

    public bool HasSingleTurnSly { get; init; }

    public bool ExhaustOnNextPlay { get; init; }

    public UndoEnchantmentRuntimeState? EnchantmentState { get; init; }
}

internal sealed class UndoEnchantmentRuntimeState
{
    public EnchantmentStatus Status { get; init; }
}

internal sealed class UndoPowerRuntimeState
{
    public required string OwnerCreatureKey { get; init; }

    public required ModelId PowerId { get; init; }

    public required int Ordinal { get; init; }

    public string? TargetCreatureKey { get; init; }

    public string? ApplierCreatureKey { get; init; }

    public SerializableCard? StolenCard { get; init; }
}

internal sealed class UndoRelicRuntimeState
{
    public required ulong PlayerNetId { get; init; }

    public required ModelId RelicId { get; init; }

    public required int Ordinal { get; init; }

    public RelicStatus Status { get; init; }

    public bool? IsActivating { get; init; }
}

internal sealed class UndoSelectionSessionState
{
    public bool HandSelectionActive { get; init; }

    public bool OverlaySelectionActive { get; init; }

    public bool SupportedChoiceUiActive { get; init; }

    public string? OverlayScreenType { get; init; }

    public UndoChoiceSpec? ChoiceSpec { get; init; }
}
