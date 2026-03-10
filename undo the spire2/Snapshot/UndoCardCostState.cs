using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace UndoTheSpire2;

internal sealed class UndoCardCostState
{
    public int EnergyBaseCost { get; init; }

    public int CapturedXValue { get; init; }

    public bool EnergyWasJustUpgraded { get; init; }

    public IReadOnlyList<UndoLocalCostModifierState> EnergyLocalModifiers { get; init; } = [];

    public bool StarCostSet { get; init; }

    public int BaseStarCost { get; init; }

    public bool StarWasJustUpgraded { get; init; }

    public IReadOnlyList<UndoTemporaryStarCostState> TemporaryStarCosts { get; init; } = [];
}

internal sealed class UndoLocalCostModifierState
{
    public int Amount { get; init; }

    public LocalCostType Type { get; init; }

    public LocalCostModifierExpiration Expiration { get; init; }

    public bool IsReduceOnly { get; init; }
}

internal sealed class UndoTemporaryStarCostState
{
    public int Cost { get; init; }

    public bool ClearsWhenTurnEnds { get; init; }

    public bool ClearsWhenCardIsPlayed { get; init; }
}
