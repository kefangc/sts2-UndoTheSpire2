// 文件说明：恢复卡牌、能力、遗物等通用运行时属性。
using System.Reflection;
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
        new DamageGrowthCardCodec(),
        new SovereignBladeCardCodec()
    ];

    private static readonly IReadOnlyList<IUndoPowerRuntimeCodec> PowerCodecs =
    [
        new AutomationCardsLeftPowerCodec(),
        new CardsPlayedThisTurnPowerCodec(),
        new JugglingAttacksPlayedPowerCodec(),
        new PowerIntFieldsCodec<DarkEmbracePower>("power:DarkEmbracePower.etherealCount", true, "etherealCount"),
        new PowerIntFieldsCodec<FeralPower>("power:FeralPower.zeroCostAttacksPlayed", true, "zeroCostAttacksPlayed"),
        new PowerIntFieldsCodec<OrbitPower>("power:OrbitPower.progress", true, "energySpent", "triggerCount"),
        new PowerIntFieldsCodec<OutbreakPower>("power:OutbreakPower.timesPoisoned", true, "timesPoisoned"),
        new VitalSparkTriggeredPlayersPowerCodec(),
        new AfterimagePlayedCardsPowerCodec(),
        new NightmareSelectedCardPowerCodec(),
        new DampenPowerCodec(),
        new DoorRevivalHalfDeadPowerCodec(),
        new RevivePendingPowerCodec(),
        new PrivateBoolFieldPowerCodec<BeaconOfHopePower>("power:BeaconOfHopePower.hasAlreadyBeenGivenBlock", "_hasAlreadyBeenGivenBlock"),
        new PrivateBoolFieldPowerCodec<NemesisPower>("power:NemesisPower.shouldApplyIntangible", "_shouldApplyIntangible"),
        new PrivateBoolFieldPowerCodec<RitualPower>("power:RitualPower.wasJustAppliedByEnemy", "_wasJustAppliedByEnemy"),
        new PrivateBoolFieldPowerCodec<TemporaryDexterityPower>("power:TemporaryDexterityPower.shouldIgnoreNextInstance", "_shouldIgnoreNextInstance"),
        new PrivateBoolFieldPowerCodec<TemporaryFocusPower>("power:TemporaryFocusPower.shouldIgnoreNextInstance", "_shouldIgnoreNextInstance"),
        new PrivateBoolFieldPowerCodec<TemporaryStrengthPower>("power:TemporaryStrengthPower.shouldIgnoreNextInstance", "_shouldIgnoreNextInstance"),
        new ChainsOfBindingBoundCardPlayedPowerCodec()
    ];

    private static readonly IReadOnlyList<IUndoRelicRuntimeCodec> RelicCodecs =
    [
        new PaelsLegionAffectedCardPlayRelicCodec(),
        new PenNibAttackToDoubleRelicCodec(),
        new CardsPlayedTurnCounterRelicCodec()
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

    private static bool TryGetPowerCardsPlayedThisTurn(PowerModel power, out int value)
    {
        object? internalData = GetPowerInternalData(power);
        if (internalData != null && UndoReflectionUtil.FindField(internalData.GetType(), "cardsPlayedThisTurn")?.GetValue(internalData) is int internalValue)
        {
            value = internalValue;
            return true;
        }

        if (UndoReflectionUtil.FindProperty(power.GetType(), "CardsPlayedThisTurn")?.GetValue(power) is int propertyValue)
        {
            value = propertyValue;
            return true;
        }

        if (UndoReflectionUtil.FindField(power.GetType(), "_cardsPlayedThisTurn")?.GetValue(power) is int fieldValue)
        {
            value = fieldValue;
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TrySetPowerCardsPlayedThisTurn(PowerModel power, int value)
    {
        object? internalData = GetPowerInternalData(power);
        if (internalData != null && UndoReflectionUtil.FindField(internalData.GetType(), "cardsPlayedThisTurn") != null)
            return UndoReflectionUtil.TrySetFieldValue(internalData, "cardsPlayedThisTurn", value);

        if (UndoReflectionUtil.FindProperty(power.GetType(), "CardsPlayedThisTurn") != null)
            return UndoReflectionUtil.TrySetPropertyValue(power, "CardsPlayedThisTurn", value);

        return UndoReflectionUtil.TrySetFieldValue(power, "_cardsPlayedThisTurn", value);
    }

    private static bool TryGetPowerIntField(PowerModel power, string fieldName, bool preferInternalData, out int value)
    {
        object? internalData = GetPowerInternalData(power);
        if (preferInternalData && internalData != null && UndoReflectionUtil.FindField(internalData.GetType(), fieldName)?.GetValue(internalData) is int internalValue)
        {
            value = internalValue;
            return true;
        }

        if (UndoReflectionUtil.FindField(power.GetType(), fieldName)?.GetValue(power) is int fieldValue)
        {
            value = fieldValue;
            return true;
        }

        if (!preferInternalData && internalData != null && UndoReflectionUtil.FindField(internalData.GetType(), fieldName)?.GetValue(internalData) is int fallbackInternalValue)
        {
            value = fallbackInternalValue;
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TrySetPowerIntField(PowerModel power, string fieldName, int value, bool preferInternalData)
    {
        object? internalData = GetPowerInternalData(power);
        if (preferInternalData && internalData != null && UndoReflectionUtil.FindField(internalData.GetType(), fieldName) != null)
            return UndoReflectionUtil.TrySetFieldValue(internalData, fieldName, value);

        if (UndoReflectionUtil.FindField(power.GetType(), fieldName) != null)
            return UndoReflectionUtil.TrySetFieldValue(power, fieldName, value);

        if (!preferInternalData && internalData != null && UndoReflectionUtil.FindField(internalData.GetType(), fieldName) != null)
            return UndoReflectionUtil.TrySetFieldValue(internalData, fieldName, value);

        return false;
    }

    private static bool TryGetPowerBoolField(PowerModel power, string fieldName, bool preferInternalData, out bool value)
    {
        object? internalData = GetPowerInternalData(power);
        if (preferInternalData && internalData != null && UndoReflectionUtil.FindField(internalData.GetType(), fieldName)?.GetValue(internalData) is bool internalBool)
        {
            value = internalBool;
            return true;
        }

        if (UndoReflectionUtil.FindField(power.GetType(), fieldName)?.GetValue(power) is bool fieldBool)
        {
            value = fieldBool;
            return true;
        }

        if (!preferInternalData && internalData != null && UndoReflectionUtil.FindField(internalData.GetType(), fieldName)?.GetValue(internalData) is bool fallbackInternalBool)
        {
            value = fallbackInternalBool;
            return true;
        }

        value = false;
        return false;
    }

    private static bool TrySetPowerBoolField(PowerModel power, string fieldName, bool value, bool preferInternalData)
    {
        object? internalData = GetPowerInternalData(power);
        if (preferInternalData && internalData != null && UndoReflectionUtil.FindField(internalData.GetType(), fieldName) != null)
            return UndoReflectionUtil.TrySetFieldValue(internalData, fieldName, value);

        if (UndoReflectionUtil.FindField(power.GetType(), fieldName) != null)
            return UndoReflectionUtil.TrySetFieldValue(power, fieldName, value);

        if (!preferInternalData && internalData != null && UndoReflectionUtil.FindField(internalData.GetType(), fieldName) != null)
            return UndoReflectionUtil.TrySetFieldValue(internalData, fieldName, value);

        return false;
    }

    private static bool IsCardsPlayedTurnCounterRelic(RelicModel relic)
    {
        return relic is BrilliantScarf or DiamondDiadem or Pocketwatch or VelvetChoker;
    }

    private static bool TryGetRelicCardsPlayedThisTurn(RelicModel relic, out int value)
    {
        if (UndoReflectionUtil.FindProperty(relic.GetType(), "CardsPlayedThisTurn")?.GetValue(relic) is int propertyValue)
        {
            value = propertyValue;
            return true;
        }

        if (UndoReflectionUtil.FindField(relic.GetType(), "_cardsPlayedThisTurn")?.GetValue(relic) is int fieldValue)
        {
            value = fieldValue;
            return true;
        }

        value = 0;
        return false;
    }

    private static void SetRelicCardsPlayedThisTurn(RelicModel relic, int value)
    {
        if (UndoReflectionUtil.FindProperty(relic.GetType(), "CardsPlayedThisTurn") != null)
        {
            UndoReflectionUtil.TrySetPropertyValue(relic, "CardsPlayedThisTurn", value);
            return;
        }

        UndoReflectionUtil.TrySetFieldValue(relic, "_cardsPlayedThisTurn", value);
    }

    private static void RefreshRelicCounterDisplay(RelicModel relic)
    {
        if (UndoReflectionUtil.FindMethod(relic.GetType(), "RefreshCounter") is MethodInfo refreshCounter)
        {
            refreshCounter.Invoke(relic, []);
            return;
        }

        if (UndoReflectionUtil.FindMethod(relic.GetType(), "UpdateDisplay") is MethodInfo updateDisplay)
        {
            updateDisplay.Invoke(relic, []);
            return;
        }

        UndoReflectionUtil.FindMethod(relic.GetType(), "InvokeDisplayAmountChanged")?.Invoke(relic, []);
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
    private sealed class DamageGrowthCardCodec : UndoCardRuntimeCodec<UndoPairDecimalRuntimeComplexState>
    {
        public override string CodecId => "card:DamageGrowth.baseAndAccumulator";

        public override bool CanHandle(CardModel card)
        {
            return TryGetAccumulatorFieldName(card) != null;
        }

        public override UndoPairDecimalRuntimeComplexState? Capture(CardModel card, UndoRuntimeCaptureContext context)
        {
            string? accumulatorFieldName = TryGetAccumulatorFieldName(card);
            if (accumulatorFieldName == null || !card.DynamicVars.ContainsKey("Damage"))
                return null;

            decimal currentDamage = card.DynamicVars.Damage.BaseValue;
            decimal accumulatedDamage = UndoReflectionUtil.FindField(card.GetType(), accumulatorFieldName)?.GetValue(card) is decimal value ? value : 0m;
            return new UndoPairDecimalRuntimeComplexState
            {
                CodecId = CodecId,
                FirstValue = accumulatedDamage,
                SecondValue = currentDamage
            };
        }

        public override void Restore(CardModel card, UndoPairDecimalRuntimeComplexState state, UndoRuntimeRestoreContext context)
        {
            string? accumulatorFieldName = TryGetAccumulatorFieldName(card);
            if (accumulatorFieldName == null || !card.DynamicVars.ContainsKey("Damage"))
                return;

            UndoReflectionUtil.TrySetFieldValue(card, accumulatorFieldName, state.FirstValue);
            card.DynamicVars.Damage.BaseValue = state.SecondValue;
        }

        // Official source currently implements combat-persistent self damage growth
        // for these cards by mutating DynamicVars.Damage.BaseValue and storing a
        // private accumulator that AfterDowngraded re-applies.
        private static string? TryGetAccumulatorFieldName(CardModel card)
        {
            return card switch
            {
                Claw => "_extraDamageFromClawPlays",
                Rampage => "_extraDamageFromPlays",
                Thrash => "_extraDamage",
                KinglyPunch => "_extraDamage",
                Maul => "_extraDamageFromMaulPlays",
                _ => null
            };
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

    private sealed class CardsPlayedThisTurnPowerCodec : UndoPowerRuntimeCodec<UndoIntRuntimeComplexState>
    {
        public override string CodecId => "power:CardsPlayedThisTurn.counter";

        public override bool CanHandle(PowerModel power)
        {
            return power is VoidFormPower or TenderPower or SlothPower;
        }

        public override UndoIntRuntimeComplexState? Capture(PowerModel power, UndoRuntimeCaptureContext context)
        {
            if (!TryGetPowerCardsPlayedThisTurn(power, out int cardsPlayedThisTurn))
                return null;

            return new UndoIntRuntimeComplexState
            {
                CodecId = CodecId,
                Value = cardsPlayedThisTurn
            };
        }

        public override void Restore(PowerModel power, UndoIntRuntimeComplexState state, UndoRuntimeRestoreContext context)
        {
            if (!TrySetPowerCardsPlayedThisTurn(power, state.Value))
                return;

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

    private sealed class PowerIntFieldsCodec<TPower> : UndoPowerRuntimeCodec<UndoNamedIntFieldsRuntimeComplexState>
        where TPower : PowerModel
    {
        private readonly string _codecId;
        private readonly bool _preferInternalData;
        private readonly IReadOnlyList<string> _fieldNames;

        public PowerIntFieldsCodec(string codecId, bool preferInternalData, params string[] fieldNames)
        {
            _codecId = codecId;
            _preferInternalData = preferInternalData;
            _fieldNames = fieldNames;
        }

        public override string CodecId => _codecId;

        public override bool CanHandle(PowerModel power)
        {
            return power is TPower && _fieldNames.All(fieldName => TryGetPowerIntField(power, fieldName, _preferInternalData, out _));
        }

        public override UndoNamedIntFieldsRuntimeComplexState? Capture(PowerModel power, UndoRuntimeCaptureContext context)
        {
            List<UndoNamedIntRuntimeEntry> entries = [];
            foreach (string fieldName in _fieldNames)
            {
                if (!TryGetPowerIntField(power, fieldName, _preferInternalData, out int value))
                    return null;

                entries.Add(new UndoNamedIntRuntimeEntry
                {
                    Name = fieldName,
                    Value = value
                });
            }

            return new UndoNamedIntFieldsRuntimeComplexState
            {
                CodecId = CodecId,
                Entries = entries
            };
        }

        public override void Restore(PowerModel power, UndoNamedIntFieldsRuntimeComplexState state, UndoRuntimeRestoreContext context)
        {
            bool restoredAny = false;
            foreach (UndoNamedIntRuntimeEntry entry in state.Entries)
                restoredAny |= TrySetPowerIntField(power, entry.Name, entry.Value, _preferInternalData);

            if (restoredAny)
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

    private sealed class RevivePendingPowerCodec : UndoPowerRuntimeCodec<UndoBoolRuntimeComplexState>
    {
        public override string CodecId => "power:RevivePending.isReviving";

        public override bool CanHandle(PowerModel power)
        {
            object? internalData = GetPowerInternalData(power);
            return internalData != null && UndoReflectionUtil.FindField(internalData.GetType(), "isReviving") != null;
        }

        public override UndoBoolRuntimeComplexState? Capture(PowerModel power, UndoRuntimeCaptureContext context)
        {
            if (!TryGetPowerBoolField(power, "isReviving", preferInternalData: true, out bool value))
                return null;

            return new UndoBoolRuntimeComplexState
            {
                CodecId = CodecId,
                Value = value
            };
        }

        public override void Restore(PowerModel power, UndoBoolRuntimeComplexState state, UndoRuntimeRestoreContext context)
        {
            if (TrySetPowerBoolField(power, "isReviving", state.Value, preferInternalData: true))
                InvokeDisplayAmountChanged(power);
        }
    }

    private sealed class PrivateBoolFieldPowerCodec<TPower> : UndoPowerRuntimeCodec<UndoBoolRuntimeComplexState>
        where TPower : PowerModel
    {
        private readonly string _codecId;
        private readonly string _fieldName;
        private readonly bool _preferInternalData;

        public PrivateBoolFieldPowerCodec(string codecId, string fieldName, bool preferInternalData = false)
        {
            _codecId = codecId;
            _fieldName = fieldName;
            _preferInternalData = preferInternalData;
        }

        public override string CodecId => _codecId;

        public override bool CanHandle(PowerModel power)
        {
            return power is TPower && TryGetPowerBoolField(power, _fieldName, _preferInternalData, out _);
        }

        public override UndoBoolRuntimeComplexState? Capture(PowerModel power, UndoRuntimeCaptureContext context)
        {
            if (!TryGetPowerBoolField(power, _fieldName, _preferInternalData, out bool value))
                return null;

            return new UndoBoolRuntimeComplexState
            {
                CodecId = CodecId,
                Value = value
            };
        }

        public override void Restore(PowerModel power, UndoBoolRuntimeComplexState state, UndoRuntimeRestoreContext context)
        {
            if (TrySetPowerBoolField(power, _fieldName, state.Value, _preferInternalData))
                InvokeDisplayAmountChanged(power);
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

    private sealed class CardsPlayedTurnCounterRelicCodec : UndoRelicRuntimeCodec<UndoPairIntRuntimeComplexState>
    {
        public override string CodecId => "relic:CardsPlayedTurnCounters.data";

        public override bool CanHandle(RelicModel relic)
        {
            return IsCardsPlayedTurnCounterRelic(relic);
        }

        public override UndoPairIntRuntimeComplexState? Capture(RelicModel relic, UndoRuntimeCaptureContext context)
        {
            if (!TryGetRelicCardsPlayedThisTurn(relic, out int thisTurn))
                return null;

            return new UndoPairIntRuntimeComplexState
            {
                CodecId = CodecId,
                FirstValue = thisTurn,
                SecondValue = UndoReflectionUtil.FindField(relic.GetType(), "_cardsPlayedLastTurn")?.GetValue(relic) is int lastTurn ? lastTurn : 0
            };
        }

        public override void Restore(RelicModel relic, UndoPairIntRuntimeComplexState state, UndoRuntimeRestoreContext context)
        {
            SetRelicCardsPlayedThisTurn(relic, state.FirstValue);
            UndoReflectionUtil.TrySetFieldValue(relic, "_cardsPlayedLastTurn", state.SecondValue);
            RefreshRelicCounterDisplay(relic);
        }
    }
}





