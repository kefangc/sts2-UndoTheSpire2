using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace UndoTheSpire2;

internal static partial class UndoRuntimeStateCodecRegistry
{
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
}
