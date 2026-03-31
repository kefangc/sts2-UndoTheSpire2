using System.Reflection;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace UndoTheSpire2;

internal static partial class UndoRuntimeStateCodecRegistry
{
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

    private sealed class PowerDecimalFieldsCodec<TPower> : UndoPowerRuntimeCodec<UndoNamedDecimalFieldsRuntimeComplexState>
        where TPower : PowerModel
    {
        private readonly string _codecId;
        private readonly bool _preferInternalData;
        private readonly IReadOnlyList<string> _fieldNames;

        public PowerDecimalFieldsCodec(string codecId, bool preferInternalData, params string[] fieldNames)
        {
            _codecId = codecId;
            _preferInternalData = preferInternalData;
            _fieldNames = fieldNames;
        }

        public override string CodecId => _codecId;

        public override bool CanHandle(PowerModel power)
        {
            return power is TPower && _fieldNames.All(fieldName => TryGetPowerDecimalField(power, fieldName, _preferInternalData, out _));
        }

        public override UndoNamedDecimalFieldsRuntimeComplexState? Capture(PowerModel power, UndoRuntimeCaptureContext context)
        {
            List<UndoNamedDecimalRuntimeEntry> entries = [];
            foreach (string fieldName in _fieldNames)
            {
                if (!TryGetPowerDecimalField(power, fieldName, _preferInternalData, out decimal value))
                    return null;

                entries.Add(new UndoNamedDecimalRuntimeEntry
                {
                    Name = fieldName,
                    Value = value
                });
            }

            return new UndoNamedDecimalFieldsRuntimeComplexState
            {
                CodecId = CodecId,
                Entries = entries
            };
        }

        public override void Restore(PowerModel power, UndoNamedDecimalFieldsRuntimeComplexState state, UndoRuntimeRestoreContext context)
        {
            bool restoredAny = false;
            foreach (UndoNamedDecimalRuntimeEntry entry in state.Entries)
                restoredAny |= TrySetPowerDecimalField(power, entry.Name, entry.Value, _preferInternalData);

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

}
