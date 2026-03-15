// 文件说明：恢复卡牌、能力、遗物等通用运行时属性。
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Entities.Models;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace UndoTheSpire2;

// Runtime codecs persist official private state that is not covered by
// SavedProperty or NetFullCombatState. They do not own creature topology or
// paused choice continuation.
internal sealed class UndoRuntimeCaptureContext
{
    public required RunState RunState { get; init; }

    public required CombatState CombatState { get; init; }
}

internal sealed class UndoRuntimeRestoreContext
{
    public required RunState RunState { get; init; }

    public required CombatState CombatState { get; init; }
}

internal interface IUndoRuntimeCodec<TLive, TState>
{
    string CodecId { get; }

    bool CanHandle(TLive live);

    TState? Capture(TLive live, UndoRuntimeCaptureContext context);

    void Restore(TLive live, TState state, UndoRuntimeRestoreContext context);
}

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

internal sealed class UndoDampenRuntimeComplexState : UndoComplexRuntimeState
{
    public IReadOnlyList<CreatureRef> Casters { get; init; } = [];

    public IReadOnlyList<UndoCardIntMapEntry> DowngradedCards { get; init; } = [];
}

internal interface IUndoCardRuntimeCodec
{
    string CodecId { get; }

    bool CanHandle(CardModel card);

    UndoComplexRuntimeState? Capture(CardModel card, UndoRuntimeCaptureContext context);

    void Restore(CardModel card, UndoComplexRuntimeState state, UndoRuntimeRestoreContext context);
}

internal interface IUndoPowerRuntimeCodec
{
    string CodecId { get; }

    bool CanHandle(PowerModel power);

    UndoComplexRuntimeState? Capture(PowerModel power, UndoRuntimeCaptureContext context);

    void Restore(PowerModel power, UndoComplexRuntimeState state, UndoRuntimeRestoreContext context);
}

internal interface IUndoRelicRuntimeCodec
{
    string CodecId { get; }

    bool CanHandle(RelicModel relic);

    UndoComplexRuntimeState? Capture(RelicModel relic, UndoRuntimeCaptureContext context);

    void Restore(RelicModel relic, UndoComplexRuntimeState state, UndoRuntimeRestoreContext context);
}

internal abstract class UndoCardRuntimeCodec<TState> : IUndoCardRuntimeCodec, IUndoRuntimeCodec<CardModel, TState>
    where TState : UndoComplexRuntimeState
{
    public abstract string CodecId { get; }

    public abstract bool CanHandle(CardModel card);

    public abstract TState? Capture(CardModel card, UndoRuntimeCaptureContext context);

    public abstract void Restore(CardModel card, TState state, UndoRuntimeRestoreContext context);

    UndoComplexRuntimeState? IUndoCardRuntimeCodec.Capture(CardModel card, UndoRuntimeCaptureContext context)
    {
        return Capture(card, context);
    }

    void IUndoCardRuntimeCodec.Restore(CardModel card, UndoComplexRuntimeState state, UndoRuntimeRestoreContext context)
    {
        if (state is TState typed)
            Restore(card, typed, context);
    }
}

internal abstract class UndoPowerRuntimeCodec<TState> : IUndoPowerRuntimeCodec, IUndoRuntimeCodec<PowerModel, TState>
    where TState : UndoComplexRuntimeState
{
    public abstract string CodecId { get; }

    public abstract bool CanHandle(PowerModel power);

    public abstract TState? Capture(PowerModel power, UndoRuntimeCaptureContext context);

    public abstract void Restore(PowerModel power, TState state, UndoRuntimeRestoreContext context);

    UndoComplexRuntimeState? IUndoPowerRuntimeCodec.Capture(PowerModel power, UndoRuntimeCaptureContext context)
    {
        return Capture(power, context);
    }

    void IUndoPowerRuntimeCodec.Restore(PowerModel power, UndoComplexRuntimeState state, UndoRuntimeRestoreContext context)
    {
        if (state is TState typed)
            Restore(power, typed, context);
    }
}

internal abstract class UndoRelicRuntimeCodec<TState> : IUndoRelicRuntimeCodec, IUndoRuntimeCodec<RelicModel, TState>
    where TState : UndoComplexRuntimeState
{
    public abstract string CodecId { get; }

    public abstract bool CanHandle(RelicModel relic);

    public abstract TState? Capture(RelicModel relic, UndoRuntimeCaptureContext context);

    public abstract void Restore(RelicModel relic, TState state, UndoRuntimeRestoreContext context);

    UndoComplexRuntimeState? IUndoRelicRuntimeCodec.Capture(RelicModel relic, UndoRuntimeCaptureContext context)
    {
        return Capture(relic, context);
    }

    void IUndoRelicRuntimeCodec.Restore(RelicModel relic, UndoComplexRuntimeState state, UndoRuntimeRestoreContext context)
    {
        if (state is TState typed)
            Restore(relic, typed, context);
    }
}

internal static class UndoRuntimeStateCodecRegistry
{
    private static readonly IReadOnlyList<IUndoCardRuntimeCodec> CardCodecs =
    [
        new UpMySleeveCardCodec(),
        new ClawCardCodec(),
        new SovereignBladeCardCodec()
    ];

    private static readonly IReadOnlyList<IUndoPowerRuntimeCodec> PowerCodecs =
    [
        new AutomationCardsLeftPowerCodec(),
        new JugglingAttacksPlayedPowerCodec(),
        new VitalSparkTriggeredPlayersPowerCodec(),
        new AfterimagePlayedCardsPowerCodec(),
        new NightmareSelectedCardPowerCodec(),
        new DampenPowerCodec(),
        new DoorRevivalHalfDeadPowerCodec(),
        new ChainsOfBindingBoundCardPlayedPowerCodec()
    ];

    private static readonly IReadOnlyList<IUndoRelicRuntimeCodec> RelicCodecs =
    [
        new PaelsLegionAffectedCardPlayRelicCodec(),
        new PenNibAttackToDoubleRelicCodec(),
        new PocketwatchTurnCountRelicCodec(),
        new VelvetChokerCardsPlayedRelicCodec()
    ];

    public static HashSet<string> GetImplementedCodecIds()
    {
        return CardCodecs.Select(static codec => codec.CodecId)
            .Concat(PowerCodecs.Select(static codec => codec.CodecId))
            .Concat(RelicCodecs.Select(static codec => codec.CodecId))
            .ToHashSet(StringComparer.Ordinal);
    }

    public static IReadOnlyList<UndoComplexRuntimeState> CaptureCardStates(CardModel card, UndoRuntimeCaptureContext context)
    {
        List<UndoComplexRuntimeState> states = [];
        foreach (IUndoCardRuntimeCodec codec in CardCodecs)
        {
            if (!codec.CanHandle(card))
                continue;

            UndoComplexRuntimeState? state = codec.Capture(card, context);
            if (state != null)
                states.Add(state);
        }

        return states;
    }

    public static void RestoreCardStates(CardModel card, IReadOnlyList<UndoComplexRuntimeState> states, UndoRuntimeRestoreContext context)
    {
        foreach (UndoComplexRuntimeState state in states)
        {
            IUndoCardRuntimeCodec? codec = CardCodecs.FirstOrDefault(candidate => candidate.CodecId == state.CodecId && candidate.CanHandle(card));
            codec?.Restore(card, state, context);
        }
    }

    public static IReadOnlyList<UndoComplexRuntimeState> CapturePowerStates(PowerModel power, UndoRuntimeCaptureContext context)
    {
        List<UndoComplexRuntimeState> states = [];
        foreach (IUndoPowerRuntimeCodec codec in PowerCodecs)
        {
            if (!codec.CanHandle(power))
                continue;

            UndoComplexRuntimeState? state = codec.Capture(power, context);
            if (state != null)
                states.Add(state);
        }

        return states;
    }

    public static void RestorePowerStates(PowerModel power, IReadOnlyList<UndoComplexRuntimeState> states, UndoRuntimeRestoreContext context)
    {
        foreach (UndoComplexRuntimeState state in states)
        {
            IUndoPowerRuntimeCodec? codec = PowerCodecs.FirstOrDefault(candidate => candidate.CodecId == state.CodecId && candidate.CanHandle(power));
            codec?.Restore(power, state, context);
        }
    }

    public static IReadOnlyList<UndoComplexRuntimeState> CaptureRelicStates(RelicModel relic, UndoRuntimeCaptureContext context)
    {
        List<UndoComplexRuntimeState> states = [];
        foreach (IUndoRelicRuntimeCodec codec in RelicCodecs)
        {
            if (!codec.CanHandle(relic))
                continue;

            UndoComplexRuntimeState? state = codec.Capture(relic, context);
            if (state != null)
                states.Add(state);
        }

        return states;
    }

    public static void RestoreRelicStates(RelicModel relic, IReadOnlyList<UndoComplexRuntimeState> states, UndoRuntimeRestoreContext context)
    {
        foreach (UndoComplexRuntimeState state in states)
        {
            IUndoRelicRuntimeCodec? codec = RelicCodecs.FirstOrDefault(candidate => candidate.CodecId == state.CodecId && candidate.CanHandle(relic));
            codec?.Restore(relic, state, context);
        }
    }

    public static void RefreshPowerDisplays(CombatState combatState)
    {
        foreach (Creature creature in combatState.Creatures)
        {
            foreach (PowerModel power in creature.Powers)
                InvokeDisplayAmountChanged(power);
        }
    }

    private static void InvokeDisplayAmountChanged(PowerModel power)
    {
        UndoReflectionUtil.FindMethod(power.GetType(), "InvokeDisplayAmountChanged")?.Invoke(power, []);
    }

    private static object? GetPowerInternalData(PowerModel power)
    {
        return UndoReflectionUtil.FindField(typeof(PowerModel), "_internalData")?.GetValue(power);
    }

    private sealed class UpMySleeveCardCodec : UndoCardRuntimeCodec<UndoIntRuntimeComplexState>
    {
        public override string CodecId => "card:UpMySleeve.timesPlayedThisCombat";

        public override bool CanHandle(CardModel card)
        {
            return card is UpMySleeve;
        }

        public override UndoIntRuntimeComplexState? Capture(CardModel card, UndoRuntimeCaptureContext context)
        {
            return new UndoIntRuntimeComplexState
            {
                CodecId = CodecId,
                Value = UndoReflectionUtil.FindField(card.GetType(), "_timesPlayedThisCombat")?.GetValue(card) is int value ? value : 0
            };
        }

        public override void Restore(CardModel card, UndoIntRuntimeComplexState state, UndoRuntimeRestoreContext context)
        {
            UndoReflectionUtil.TrySetFieldValue(card, "_timesPlayedThisCombat", state.Value);
        }
    }
    private sealed class ClawCardCodec : UndoCardRuntimeCodec<UndoPairIntRuntimeComplexState>
    {
        public override string CodecId => "card:Claw.damageGrowth";

        public override bool CanHandle(CardModel card)
        {
            return card is Claw;
        }

        public override UndoPairIntRuntimeComplexState? Capture(CardModel card, UndoRuntimeCaptureContext context)
        {
            if (card is not Claw claw)
                return null;

            decimal currentDamage = claw.DynamicVars.Damage.BaseValue;
            decimal extraDamage = UndoReflectionUtil.FindField(claw.GetType(), "_extraDamageFromClawPlays")?.GetValue(claw) is decimal value ? value : 0m;
            return new UndoPairIntRuntimeComplexState
            {
                CodecId = CodecId,
                FirstValue = (int)extraDamage,
                SecondValue = (int)currentDamage
            };
        }

        public override void Restore(CardModel card, UndoPairIntRuntimeComplexState state, UndoRuntimeRestoreContext context)
        {
            if (card is not Claw claw)
                return;

            UndoReflectionUtil.TrySetFieldValue(claw, "_extraDamageFromClawPlays", (decimal)state.FirstValue);
            claw.DynamicVars.Damage.BaseValue = state.SecondValue;
        }
    }

    private sealed class SovereignBladeCardCodec : UndoCardRuntimeCodec<UndoSovereignBladeRuntimeComplexState>
    {
        public override string CodecId => "card:SovereignBlade.damageAndRepeats";

        public override bool CanHandle(CardModel card)
        {
            return card is SovereignBlade;
        }

        public override UndoSovereignBladeRuntimeComplexState? Capture(CardModel card, UndoRuntimeCaptureContext context)
        {
            if (card is not SovereignBlade sovereignBlade)
                return null;

            decimal currentDamage = UndoReflectionUtil.FindField(sovereignBlade.GetType(), "_currentDamage")?.GetValue(sovereignBlade) is decimal damage ? damage : sovereignBlade.DynamicVars.Damage.BaseValue;
            decimal currentRepeats = UndoReflectionUtil.FindField(sovereignBlade.GetType(), "_currentRepeats")?.GetValue(sovereignBlade) is decimal repeats ? repeats : sovereignBlade.DynamicVars.Repeat.BaseValue;
            bool createdThroughForge = UndoReflectionUtil.FindField(sovereignBlade.GetType(), "_createdThroughForge")?.GetValue(sovereignBlade) is bool created && created;

            return new UndoSovereignBladeRuntimeComplexState
            {
                CodecId = CodecId,
                CurrentDamage = currentDamage,
                CurrentRepeats = currentRepeats,
                CreatedThroughForge = createdThroughForge
            };
        }

        public override void Restore(CardModel card, UndoSovereignBladeRuntimeComplexState state, UndoRuntimeRestoreContext context)
        {
            if (card is not SovereignBlade sovereignBlade)
                return;

            UndoReflectionUtil.TrySetFieldValue(sovereignBlade, "_currentDamage", state.CurrentDamage);
            UndoReflectionUtil.TrySetFieldValue(sovereignBlade, "_currentRepeats", state.CurrentRepeats);
            UndoReflectionUtil.TrySetFieldValue(sovereignBlade, "_createdThroughForge", state.CreatedThroughForge);
            sovereignBlade.DynamicVars.Damage.BaseValue = state.CurrentDamage;
            sovereignBlade.DynamicVars.Repeat.BaseValue = state.CurrentRepeats;
        }
    }

    private sealed class AutomationCardsLeftPowerCodec : UndoPowerRuntimeCodec<UndoIntRuntimeComplexState>
    {
        public override string CodecId => "power:AutomationPower.cardsLeft";

        public override bool CanHandle(PowerModel power)
        {
            return power is AutomationPower;
        }

        public override UndoIntRuntimeComplexState? Capture(PowerModel power, UndoRuntimeCaptureContext context)
        {
            object? internalData = GetPowerInternalData(power);
            if (internalData == null || UndoReflectionUtil.FindField(internalData.GetType(), "cardsLeft")?.GetValue(internalData) is not int cardsLeft)
                return null;

            return new UndoIntRuntimeComplexState
            {
                CodecId = CodecId,
                Value = cardsLeft
            };
        }

        public override void Restore(PowerModel power, UndoIntRuntimeComplexState state, UndoRuntimeRestoreContext context)
        {
            object? internalData = GetPowerInternalData(power);
            if (internalData == null)
                return;

            UndoReflectionUtil.TrySetFieldValue(internalData, "cardsLeft", state.Value);
            InvokeDisplayAmountChanged(power);
        }
    }

    private sealed class JugglingAttacksPlayedPowerCodec : UndoPowerRuntimeCodec<UndoIntRuntimeComplexState>
    {
        public override string CodecId => "power:JugglingPower.attacksPlayedThisTurn";

        public override bool CanHandle(PowerModel power)
        {
            return power is JugglingPower;
        }

        public override UndoIntRuntimeComplexState? Capture(PowerModel power, UndoRuntimeCaptureContext context)
        {
            object? internalData = GetPowerInternalData(power);
            if (internalData == null || UndoReflectionUtil.FindField(internalData.GetType(), "attacksPlayedThisTurn")?.GetValue(internalData) is not int attacksPlayedThisTurn)
                return null;

            return new UndoIntRuntimeComplexState
            {
                CodecId = CodecId,
                Value = attacksPlayedThisTurn
            };
        }

        public override void Restore(PowerModel power, UndoIntRuntimeComplexState state, UndoRuntimeRestoreContext context)
        {
            object? internalData = GetPowerInternalData(power);
            if (internalData == null)
                return;

            UndoReflectionUtil.TrySetFieldValue(internalData, "attacksPlayedThisTurn", state.Value);
            InvokeDisplayAmountChanged(power);
        }
    }

    private sealed class VitalSparkTriggeredPlayersPowerCodec : UndoPowerRuntimeCodec<UndoCreatureSetRuntimeComplexState>
    {
        public override string CodecId => "power:VitalSparkPower.playersTriggeredThisTurn";

        public override bool CanHandle(PowerModel power)
        {
            return power is VitalSparkPower;
        }

        public override UndoCreatureSetRuntimeComplexState? Capture(PowerModel power, UndoRuntimeCaptureContext context)
        {
            object? internalData = GetPowerInternalData(power);
            if (internalData == null || UndoReflectionUtil.FindField(internalData.GetType(), "playersTriggeredThisTurn")?.GetValue(internalData) is not HashSet<Player> players)
                return null;

            return new UndoCreatureSetRuntimeComplexState
            {
                CodecId = CodecId,
                Creatures = players
                    .Select(player => UndoStableRefs.CaptureCreatureRef(context.CombatState.Creatures, player.Creature))
                    .Where(static creature => creature != null)
                    .Cast<CreatureRef>()
                    .ToList()
            };
        }

        public override void Restore(PowerModel power, UndoCreatureSetRuntimeComplexState state, UndoRuntimeRestoreContext context)
        {
            object? internalData = GetPowerInternalData(power);
            if (internalData == null || UndoReflectionUtil.FindField(internalData.GetType(), "playersTriggeredThisTurn")?.GetValue(internalData) is not HashSet<Player> players)
                return;

            players.Clear();
            Dictionary<string, Creature> creaturesByKey = UndoStableRefs.BuildCreatureKeyMap(context.CombatState.Creatures);
            foreach (CreatureRef creatureRef in state.Creatures)
            {
                Player? player = UndoStableRefs.ResolveCreature(creaturesByKey, creatureRef.Key)?.Player;
                if (player != null)
                    players.Add(player);
            }
        }
    }

    private sealed class AfterimagePlayedCardsPowerCodec : UndoPowerRuntimeCodec<UndoCardIntMapRuntimeComplexState>
    {
        public override string CodecId => "power:AfterimagePower.amountsForPlayedCards";

        public override bool CanHandle(PowerModel power)
        {
            return power is AfterimagePower;
        }

        public override UndoCardIntMapRuntimeComplexState? Capture(PowerModel power, UndoRuntimeCaptureContext context)
        {
            object? internalData = GetPowerInternalData(power);
            if (internalData == null || UndoReflectionUtil.FindField(internalData.GetType(), "amountsForPlayedCards")?.GetValue(internalData) is not Dictionary<CardModel, int> cards)
                return null;

            return new UndoCardIntMapRuntimeComplexState
            {
                CodecId = CodecId,
                Entries = cards.Select(pair => new UndoCardIntMapEntry
                {
                    Card = UndoStableRefs.CaptureCardRef(context.RunState, pair.Key),
                    Value = pair.Value
                }).ToList()
            };
        }

        public override void Restore(PowerModel power, UndoCardIntMapRuntimeComplexState state, UndoRuntimeRestoreContext context)
        {
            object? internalData = GetPowerInternalData(power);
            if (internalData == null || UndoReflectionUtil.FindField(internalData.GetType(), "amountsForPlayedCards")?.GetValue(internalData) is not Dictionary<CardModel, int> cards)
                return;

            cards.Clear();
            foreach (UndoCardIntMapEntry entry in state.Entries)
                cards[UndoStableRefs.ResolveCardRef(context.RunState, entry.Card)] = entry.Value;
        }
    }

    private sealed class NightmareSelectedCardPowerCodec : UndoPowerRuntimeCodec<UndoDetachedCardRuntimeComplexState>
    {
        public override string CodecId => "power:NightmarePower.selectedCard";

        public override bool CanHandle(PowerModel power)
        {
            return power is NightmarePower;
        }

        public override UndoDetachedCardRuntimeComplexState? Capture(PowerModel power, UndoRuntimeCaptureContext context)
        {
            object? internalData = GetPowerInternalData(power);
            if (internalData == null || UndoReflectionUtil.FindField(internalData.GetType(), "selectedCard")?.GetValue(internalData) is not CardModel selectedCard)
                return null;

            return new UndoDetachedCardRuntimeComplexState
            {
                CodecId = CodecId,
                Card = UndoSerializationUtil.ClonePacketSerializable(selectedCard.ToSerializable())
            };
        }

        public override void Restore(PowerModel power, UndoDetachedCardRuntimeComplexState state, UndoRuntimeRestoreContext context)
        {
            object? internalData = GetPowerInternalData(power);
            if (internalData == null)
                return;

            CardModel? selectedCard = null;
            if (state.Card != null)
            {
                selectedCard = CardModel.FromSerializable(UndoSerializationUtil.ClonePacketSerializable(state.Card));
                if (selectedCard.Owner == null && power.Owner.Player != null)
                    selectedCard.Owner = power.Owner.Player;
            }

            UndoReflectionUtil.TrySetFieldValue(internalData, "selectedCard", selectedCard);
        }
    }

    private sealed class DampenPowerCodec : UndoPowerRuntimeCodec<UndoDampenRuntimeComplexState>
    {
        public override string CodecId => "power:DampenPower.data";

        public override bool CanHandle(PowerModel power)
        {
            return power is DampenPower;
        }

        public override UndoDampenRuntimeComplexState? Capture(PowerModel power, UndoRuntimeCaptureContext context)
        {
            object? internalData = GetPowerInternalData(power);
            if (internalData == null)
                return null;

            HashSet<Creature>? casters = UndoReflectionUtil.FindField(internalData.GetType(), "casters")?.GetValue(internalData) as HashSet<Creature>;
            Dictionary<CardModel, int>? downgradedCards = UndoReflectionUtil.FindField(internalData.GetType(), "downgradedCardsToOldUpgradeLevels")?.GetValue(internalData) as Dictionary<CardModel, int>;
            if (casters == null || downgradedCards == null)
                return null;

            return new UndoDampenRuntimeComplexState
            {
                CodecId = CodecId,
                Casters = casters
                    .Select(caster => UndoStableRefs.CaptureCreatureRef(context.CombatState.Creatures, caster))
                    .Where(static creature => creature != null)
                    .Cast<CreatureRef>()
                    .ToList(),
                DowngradedCards = downgradedCards.Select(pair => new UndoCardIntMapEntry
                {
                    Card = UndoStableRefs.CaptureCardRef(context.RunState, pair.Key),
                    Value = pair.Value
                }).ToList()
            };
        }

        public override void Restore(PowerModel power, UndoDampenRuntimeComplexState state, UndoRuntimeRestoreContext context)
        {
            object? internalData = GetPowerInternalData(power);
            if (internalData == null)
                return;

            HashSet<Creature>? casters = UndoReflectionUtil.FindField(internalData.GetType(), "casters")?.GetValue(internalData) as HashSet<Creature>;
            Dictionary<CardModel, int>? downgradedCards = UndoReflectionUtil.FindField(internalData.GetType(), "downgradedCardsToOldUpgradeLevels")?.GetValue(internalData) as Dictionary<CardModel, int>;
            if (casters == null || downgradedCards == null)
                return;

            casters.Clear();
            downgradedCards.Clear();

            Dictionary<string, Creature> creaturesByKey = UndoStableRefs.BuildCreatureKeyMap(context.CombatState.Creatures);
            foreach (CreatureRef creatureRef in state.Casters)
            {
                Creature? creature = UndoStableRefs.ResolveCreature(creaturesByKey, creatureRef.Key);
                if (creature != null)
                    casters.Add(creature);
            }

            foreach (UndoCardIntMapEntry entry in state.DowngradedCards)
                downgradedCards[UndoStableRefs.ResolveCardRef(context.RunState, entry.Card)] = entry.Value;
        }
    }

    private sealed class DoorRevivalHalfDeadPowerCodec : UndoPowerRuntimeCodec<UndoBoolRuntimeComplexState>
    {
        public override string CodecId => "power:DoorRevivalPower.isHalfDead";

        public override bool CanHandle(PowerModel power)
        {
            return power is DoorRevivalPower;
        }

        public override UndoBoolRuntimeComplexState? Capture(PowerModel power, UndoRuntimeCaptureContext context)
        {
            return new UndoBoolRuntimeComplexState
            {
                CodecId = CodecId,
                Value = power is DoorRevivalPower doorRevivalPower && doorRevivalPower.IsHalfDead
            };
        }

        public override void Restore(PowerModel power, UndoBoolRuntimeComplexState state, UndoRuntimeRestoreContext context)
        {
            object? internalData = GetPowerInternalData(power);
            if (internalData == null)
                return;

            UndoReflectionUtil.TrySetFieldValue(internalData, "isHalfDead", state.Value);
        }
    }

    private sealed class ChainsOfBindingBoundCardPlayedPowerCodec : UndoPowerRuntimeCodec<UndoBoolRuntimeComplexState>
    {
        public override string CodecId => "power:ChainsOfBindingPower.boundCardPlayed";

        public override bool CanHandle(PowerModel power)
        {
            return power is ChainsOfBindingPower;
        }

        public override UndoBoolRuntimeComplexState? Capture(PowerModel power, UndoRuntimeCaptureContext context)
        {
            object? internalData = GetPowerInternalData(power);
            if (internalData == null)
                return null;

            return new UndoBoolRuntimeComplexState
            {
                CodecId = CodecId,
                Value = UndoReflectionUtil.FindField(internalData.GetType(), "boundCardPlayed")?.GetValue(internalData) is bool value && value
            };
        }

        public override void Restore(PowerModel power, UndoBoolRuntimeComplexState state, UndoRuntimeRestoreContext context)
        {
            object? internalData = GetPowerInternalData(power);
            if (internalData == null)
                return;

            UndoReflectionUtil.TrySetFieldValue(internalData, "boundCardPlayed", state.Value);
        }
    }

    private sealed class PaelsLegionAffectedCardPlayRelicCodec : UndoRelicRuntimeCodec<UndoCardPlayRuntimeComplexState>
    {
        public override string CodecId => "relic:PaelsLegion.affectedCardPlay";

        public override bool CanHandle(RelicModel relic)
        {
            return relic is PaelsLegion;
        }

        public override UndoCardPlayRuntimeComplexState? Capture(RelicModel relic, UndoRuntimeCaptureContext context)
        {
            CardPlay? affectedCardPlay = UndoReflectionUtil.FindProperty(relic.GetType(), "AffectedCardPlay")?.GetValue(relic) as CardPlay;
            return new UndoCardPlayRuntimeComplexState
            {
                CodecId = CodecId,
                CardPlay = affectedCardPlay == null ? null : UndoCombatHistoryCodec.CaptureCardPlay(context.RunState, context.CombatState.Creatures, affectedCardPlay)
            };
        }

        public override void Restore(RelicModel relic, UndoCardPlayRuntimeComplexState state, UndoRuntimeRestoreContext context)
        {
            Dictionary<string, Creature> creaturesByKey = UndoStableRefs.BuildCreatureKeyMap(context.CombatState.Creatures);
            CardPlay? affectedCardPlay = state.CardPlay == null
                ? null
                : UndoCombatHistoryCodec.RestoreCardPlay(context.RunState, creaturesByKey, state.CardPlay);
            UndoReflectionUtil.TrySetPropertyValue(relic, "AffectedCardPlay", affectedCardPlay);
        }
    }
    private sealed class PenNibAttackToDoubleRelicCodec : UndoRelicRuntimeCodec<UndoCardRefRuntimeComplexState>
    {
        public override string CodecId => "relic:PenNib.AttackToDouble";

        public override bool CanHandle(RelicModel relic)
        {
            return relic is PenNib;
        }

        public override UndoCardRefRuntimeComplexState? Capture(RelicModel relic, UndoRuntimeCaptureContext context)
        {
            if (UndoReflectionUtil.FindProperty(relic.GetType(), "AttackToDouble")?.GetValue(relic) is not CardModel attackToDouble)
                return null;

            return new UndoCardRefRuntimeComplexState
            {
                CodecId = CodecId,
                Card = UndoStableRefs.CaptureCardRef(context.RunState, attackToDouble)
            };
        }

        public override void Restore(RelicModel relic, UndoCardRefRuntimeComplexState state, UndoRuntimeRestoreContext context)
        {
            CardModel? card = state.Card == null ? null : UndoStableRefs.ResolveCardRef(context.RunState, state.Card);
            UndoReflectionUtil.TrySetPropertyValue(relic, "AttackToDouble", card);
        }
    }

    private sealed class PocketwatchTurnCountRelicCodec : UndoRelicRuntimeCodec<UndoPairIntRuntimeComplexState>
    {
        public override string CodecId => "relic:Pocketwatch.turnCounts";

        public override bool CanHandle(RelicModel relic)
        {
            return relic is Pocketwatch;
        }

        public override UndoPairIntRuntimeComplexState? Capture(RelicModel relic, UndoRuntimeCaptureContext context)
        {
            return new UndoPairIntRuntimeComplexState
            {
                CodecId = CodecId,
                FirstValue = UndoReflectionUtil.FindField(relic.GetType(), "_cardsPlayedThisTurn")?.GetValue(relic) is int thisTurn ? thisTurn : 0,
                SecondValue = UndoReflectionUtil.FindField(relic.GetType(), "_cardsPlayedLastTurn")?.GetValue(relic) is int lastTurn ? lastTurn : 0
            };
        }

        public override void Restore(RelicModel relic, UndoPairIntRuntimeComplexState state, UndoRuntimeRestoreContext context)
        {
            UndoReflectionUtil.TrySetFieldValue(relic, "_cardsPlayedThisTurn", state.FirstValue);
            UndoReflectionUtil.TrySetFieldValue(relic, "_cardsPlayedLastTurn", state.SecondValue);
            UndoReflectionUtil.TrySetPropertyValue(relic, "Status", state.FirstValue <= 3 ? RelicStatus.Active : RelicStatus.Normal);
            UndoReflectionUtil.FindMethod(relic.GetType(), "InvokeDisplayAmountChanged")?.Invoke(relic, []);
        }
    }

    private sealed class VelvetChokerCardsPlayedRelicCodec : UndoRelicRuntimeCodec<UndoIntRuntimeComplexState>
    {
        public override string CodecId => "relic:VelvetChoker.cardsPlayedThisTurn";

        public override bool CanHandle(RelicModel relic)
        {
            return relic is VelvetChoker;
        }

        public override UndoIntRuntimeComplexState? Capture(RelicModel relic, UndoRuntimeCaptureContext context)
        {
            return new UndoIntRuntimeComplexState
            {
                CodecId = CodecId,
                Value = UndoReflectionUtil.FindField(relic.GetType(), "_cardsPlayedThisTurn")?.GetValue(relic) is int value ? value : 0
            };
        }

        public override void Restore(RelicModel relic, UndoIntRuntimeComplexState state, UndoRuntimeRestoreContext context)
        {
            UndoReflectionUtil.TrySetFieldValue(relic, "_cardsPlayedThisTurn", state.Value);
            UndoReflectionUtil.FindMethod(relic.GetType(), "InvokeDisplayAmountChanged")?.Invoke(relic, []);
        }
    }
}





