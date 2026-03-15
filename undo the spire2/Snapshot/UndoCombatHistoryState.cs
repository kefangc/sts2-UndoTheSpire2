// 文件说明：保存官方战斗历史的可恢复表示。
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

namespace UndoTheSpire2;

internal enum UndoCombatHistoryEntryKind
{
    CardPlayStarted,
    CardPlayFinished,
    CardAfflicted,
    CardDiscarded,
    CardDrawn,
    CardExhausted,
    CardGenerated,
    CreatureAttacked,
    DamageReceived,
    BlockGained,
    EnergySpent,
    MonsterPerformedMove,
    OrbChanneled,
    PotionUsed,
    PowerReceived,
    StarsModified,
    Summoned
}

internal sealed class UndoCombatHistoryState
{
    public static UndoCombatHistoryState Empty { get; } = new();

    public IReadOnlyList<UndoCombatHistoryEntryState> Entries { get; init; } = [];
}

internal sealed class UndoCombatHistoryEntryState
{
    public required UndoCombatHistoryEntryKind Kind { get; init; }

    public required CreatureRef Actor { get; init; }

    public int RoundNumber { get; init; }

    public CombatSide CurrentSide { get; init; }

    public CardRef? Card { get; init; }

    public CardRef? CardSource { get; init; }

    public ModelId? AfflictionId { get; init; }

    public UndoCardPlayState? CardPlay { get; init; }

    public UndoDamageResultState? DamageResult { get; init; }

    public IReadOnlyList<UndoDamageResultState> DamageResults { get; init; } = [];

    public CreatureRef? OtherCreature { get; init; }

    public PowerRef? Power { get; init; }

    public PotionRef? Potion { get; init; }

    public OrbRef? Orb { get; init; }

    public UndoMonsterPerformedMoveState? MonsterMove { get; init; }

    public ValueProp Props { get; init; }

    public bool BoolValue { get; init; }

    public int IntValue { get; init; }

    public decimal DecimalValue { get; init; }
}

internal sealed class UndoCardPlayState
{
    public required CardRef Card { get; init; }

    public CreatureRef? Target { get; init; }

    public PileType ResultPile { get; init; }

    public UndoResourceInfoState Resources { get; init; } = new();

    public bool IsAutoPlay { get; init; }

    public int PlayIndex { get; init; }

    public int PlayCount { get; init; }
}

internal sealed class UndoResourceInfoState
{
    public int EnergySpent { get; init; }

    public int EnergyValue { get; init; }

    public int StarsSpent { get; init; }

    public int StarValue { get; init; }
}

internal sealed class UndoDamageResultState
{
    public required CreatureRef Receiver { get; init; }

    public ValueProp Props { get; init; }

    public int BlockedDamage { get; init; }

    public int UnblockedDamage { get; init; }

    public int OverkillDamage { get; init; }

    public bool WasBlockBroken { get; init; }

    public bool WasFullyBlocked { get; init; }

    public bool WasTargetKilled { get; init; }
}

internal sealed class UndoMonsterPerformedMoveState
{
    public required CreatureRef Monster { get; init; }

    public required ModelId MonsterId { get; init; }

    public required string MoveId { get; init; }

    public IReadOnlyList<CreatureRef> Targets { get; init; } = [];
}


