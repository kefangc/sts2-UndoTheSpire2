using System;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat.History.Entries;
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
                cards[UndoStableRefs.ResolveCardRef(context.RunState, entry.Card, context.CardResolutionIndex)] = entry.Value;
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
                downgradedCards[UndoStableRefs.ResolveCardRef(context.RunState, entry.Card, context.CardResolutionIndex)] = entry.Value;
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

    private sealed class GenericPowerScalarFieldsCodec : UndoPowerRuntimeCodec<UndoPowerScalarFieldsRuntimeComplexState>
    {
        public override string CodecId => "power:Generic.scalarFields";

        public override bool CanHandle(PowerModel power)
        {
            object? internalData = GetPowerInternalData(power);
            return GetDeclaredScalarRuntimeFields(power.GetType()).Any()
                || (internalData != null && GetDeclaredScalarRuntimeFields(internalData.GetType()).Any());
        }

        public override UndoPowerScalarFieldsRuntimeComplexState? Capture(PowerModel power, UndoRuntimeCaptureContext context)
        {
            CaptureScalarFields(
                power,
                out List<UndoNamedBoolState> directBoolFields,
                out List<UndoNamedIntState> directIntFields,
                out List<UndoNamedDecimalRuntimeEntry> directDecimalFields,
                out List<UndoNamedEnumState> directEnumFields);

            object? internalData = GetPowerInternalData(power);
            CaptureScalarFields(
                internalData,
                out List<UndoNamedBoolState> internalBoolFields,
                out List<UndoNamedIntState> internalIntFields,
                out List<UndoNamedDecimalRuntimeEntry> internalDecimalFields,
                out List<UndoNamedEnumState> internalEnumFields);

            if (directBoolFields.Count == 0
                && directIntFields.Count == 0
                && directDecimalFields.Count == 0
                && directEnumFields.Count == 0
                && internalBoolFields.Count == 0
                && internalIntFields.Count == 0
                && internalDecimalFields.Count == 0
                && internalEnumFields.Count == 0)
            {
                return null;
            }

            return new UndoPowerScalarFieldsRuntimeComplexState
            {
                CodecId = CodecId,
                DirectBoolFields = directBoolFields,
                DirectIntFields = directIntFields,
                DirectDecimalFields = directDecimalFields,
                DirectEnumFields = directEnumFields,
                InternalBoolFields = internalBoolFields,
                InternalIntFields = internalIntFields,
                InternalDecimalFields = internalDecimalFields,
                InternalEnumFields = internalEnumFields
            };
        }

        public override void Restore(PowerModel power, UndoPowerScalarFieldsRuntimeComplexState state, UndoRuntimeRestoreContext context)
        {
            bool restoredAny = RestoreScalarFields(power, state.DirectBoolFields, state.DirectIntFields, state.DirectDecimalFields, state.DirectEnumFields);
            object? internalData = GetPowerInternalData(power);
            restoredAny |= RestoreScalarFields(internalData, state.InternalBoolFields, state.InternalIntFields, state.InternalDecimalFields, state.InternalEnumFields);
            if (restoredAny)
                InvokeDisplayAmountChanged(power);
        }

        private static void CaptureScalarFields(
            object? target,
            out List<UndoNamedBoolState> boolFields,
            out List<UndoNamedIntState> intFields,
            out List<UndoNamedDecimalRuntimeEntry> decimalFields,
            out List<UndoNamedEnumState> enumFields)
        {
            boolFields = [];
            intFields = [];
            decimalFields = [];
            enumFields = [];
            if (target == null)
                return;

            foreach (FieldInfo field in GetDeclaredScalarRuntimeFields(target.GetType()))
            {
                object? rawValue = field.GetValue(target);
                if (field.FieldType == typeof(bool))
                {
                    boolFields.Add(new UndoNamedBoolState
                    {
                        Name = field.Name,
                        Value = rawValue is bool value && value
                    });
                }
                else if (field.FieldType == typeof(int))
                {
                    intFields.Add(new UndoNamedIntState
                    {
                        Name = field.Name,
                        Value = rawValue is int value ? value : 0
                    });
                }
                else if (field.FieldType == typeof(decimal))
                {
                    decimalFields.Add(new UndoNamedDecimalRuntimeEntry
                    {
                        Name = field.Name,
                        Value = rawValue is decimal value ? value : 0m
                    });
                }
                else if (field.FieldType.IsEnum)
                {
                    enumFields.Add(new UndoNamedEnumState
                    {
                        Name = field.Name,
                        EnumTypeName = field.FieldType.AssemblyQualifiedName ?? field.FieldType.FullName ?? field.FieldType.Name,
                        Value = rawValue == null ? 0 : Convert.ToInt32(rawValue)
                    });
                }
            }
        }

        private static bool RestoreScalarFields(
            object? target,
            IReadOnlyList<UndoNamedBoolState> boolFields,
            IReadOnlyList<UndoNamedIntState> intFields,
            IReadOnlyList<UndoNamedDecimalRuntimeEntry> decimalFields,
            IReadOnlyList<UndoNamedEnumState> enumFields)
        {
            if (target == null)
                return false;

            bool restoredAny = false;
            Type targetType = target.GetType();
            foreach (UndoNamedBoolState fieldState in boolFields)
            {
                if (UndoReflectionUtil.FindField(targetType, fieldState.Name)?.FieldType == typeof(bool))
                {
                    UndoReflectionUtil.TrySetFieldValue(target, fieldState.Name, fieldState.Value);
                    restoredAny = true;
                }
            }

            foreach (UndoNamedIntState fieldState in intFields)
            {
                if (UndoReflectionUtil.FindField(targetType, fieldState.Name)?.FieldType == typeof(int))
                {
                    UndoReflectionUtil.TrySetFieldValue(target, fieldState.Name, fieldState.Value);
                    restoredAny = true;
                }
            }

            foreach (UndoNamedDecimalRuntimeEntry fieldState in decimalFields)
            {
                if (UndoReflectionUtil.FindField(targetType, fieldState.Name)?.FieldType == typeof(decimal))
                {
                    UndoReflectionUtil.TrySetFieldValue(target, fieldState.Name, fieldState.Value);
                    restoredAny = true;
                }
            }

            foreach (UndoNamedEnumState fieldState in enumFields)
            {
                FieldInfo? field = UndoReflectionUtil.FindField(targetType, fieldState.Name);
                if (field == null || !field.FieldType.IsEnum)
                    continue;

                UndoReflectionUtil.TrySetFieldValue(target, fieldState.Name, Enum.ToObject(field.FieldType, fieldState.Value));
                restoredAny = true;
            }

            return restoredAny;
        }
    }

    private sealed class GenericPowerCardRefFieldsCodec : UndoPowerRuntimeCodec<UndoPowerCardRefFieldsRuntimeComplexState>
    {
        public override string CodecId => "power:Generic.cardRefs";

        public override bool CanHandle(PowerModel power)
        {
            object? internalData = GetPowerInternalData(power);
            Type type = power.GetType();
            return GetDeclaredReferenceRuntimeProperties(type, typeof(CardModel)).Any()
                || GetDeclaredReferenceRuntimeFields(type, typeof(CardModel)).Any()
                || (internalData != null
                    && (GetDeclaredReferenceRuntimeProperties(internalData.GetType(), typeof(CardModel)).Any()
                        || GetDeclaredReferenceRuntimeFields(internalData.GetType(), typeof(CardModel)).Any()));
        }

        public override UndoPowerCardRefFieldsRuntimeComplexState? Capture(PowerModel power, UndoRuntimeCaptureContext context)
        {
            List<UndoNamedCardRefRuntimeEntry> directEntries = CaptureCardRefEntries(power, context);
            List<UndoNamedCardRefRuntimeEntry> internalEntries = CaptureCardRefEntries(GetPowerInternalData(power), context);
            if (directEntries.Count == 0 && internalEntries.Count == 0)
                return null;

            return new UndoPowerCardRefFieldsRuntimeComplexState
            {
                CodecId = CodecId,
                DirectEntries = directEntries,
                InternalEntries = internalEntries
            };
        }

        public override void Restore(PowerModel power, UndoPowerCardRefFieldsRuntimeComplexState state, UndoRuntimeRestoreContext context)
        {
            RestoreCardRefEntries(power, state.DirectEntries, context);
            RestoreCardRefEntries(GetPowerInternalData(power), state.InternalEntries, context);
        }

        private static List<UndoNamedCardRefRuntimeEntry> CaptureCardRefEntries(object? target, UndoRuntimeCaptureContext context)
        {
            List<UndoNamedCardRefRuntimeEntry> entries = [];
            if (target == null)
                return entries;

            Type type = target.GetType();
            foreach (PropertyInfo property in GetDeclaredReferenceRuntimeProperties(type, typeof(CardModel)))
            {
                entries.Add(new UndoNamedCardRefRuntimeEntry
                {
                    Name = property.Name,
                    Card = property.GetValue(target) is CardModel card ? UndoStableRefs.CaptureCardRef(context.RunState, card) : null
                });
            }

            foreach (FieldInfo field in GetDeclaredReferenceRuntimeFields(type, typeof(CardModel)))
            {
                entries.Add(new UndoNamedCardRefRuntimeEntry
                {
                    Name = field.Name,
                    Card = field.GetValue(target) is CardModel card ? UndoStableRefs.CaptureCardRef(context.RunState, card) : null
                });
            }

            return entries;
        }

        private static void RestoreCardRefEntries(object? target, IReadOnlyList<UndoNamedCardRefRuntimeEntry> entries, UndoRuntimeRestoreContext context)
        {
            if (target == null)
                return;

            Type type = target.GetType();
            foreach (UndoNamedCardRefRuntimeEntry entry in entries)
            {
                CardModel? card = entry.Card == null ? null : UndoStableRefs.ResolveCardRef(context.RunState, entry.Card, context.CardResolutionIndex);
                if (UndoReflectionUtil.FindProperty(type, entry.Name)?.PropertyType == typeof(CardModel))
                {
                    UndoReflectionUtil.TrySetPropertyValue(target, entry.Name, card);
                    continue;
                }

                if (UndoReflectionUtil.FindField(type, entry.Name)?.FieldType == typeof(CardModel))
                    UndoReflectionUtil.TrySetFieldValue(target, entry.Name, card);
            }
        }
    }

    private sealed class GenericPowerCardPlayFieldsCodec : UndoPowerRuntimeCodec<UndoPowerCardPlayFieldsRuntimeComplexState>
    {
        public override string CodecId => "power:Generic.cardPlays";

        public override bool CanHandle(PowerModel power)
        {
            object? internalData = GetPowerInternalData(power);
            Type type = power.GetType();
            return GetDeclaredReferenceRuntimeProperties(type, typeof(CardPlay)).Any()
                || GetDeclaredReferenceRuntimeFields(type, typeof(CardPlay)).Any()
                || (internalData != null
                    && (GetDeclaredReferenceRuntimeProperties(internalData.GetType(), typeof(CardPlay)).Any()
                        || GetDeclaredReferenceRuntimeFields(internalData.GetType(), typeof(CardPlay)).Any()));
        }

        public override UndoPowerCardPlayFieldsRuntimeComplexState? Capture(PowerModel power, UndoRuntimeCaptureContext context)
        {
            List<UndoNamedCardPlayRuntimeEntry> directEntries = CaptureCardPlayEntries(power, context);
            List<UndoNamedCardPlayRuntimeEntry> internalEntries = CaptureCardPlayEntries(GetPowerInternalData(power), context);
            if (directEntries.Count == 0 && internalEntries.Count == 0)
                return null;

            return new UndoPowerCardPlayFieldsRuntimeComplexState
            {
                CodecId = CodecId,
                DirectEntries = directEntries,
                InternalEntries = internalEntries
            };
        }

        public override void Restore(PowerModel power, UndoPowerCardPlayFieldsRuntimeComplexState state, UndoRuntimeRestoreContext context)
        {
            Dictionary<string, Creature> creaturesByKey = UndoStableRefs.BuildCreatureKeyMap(context.CombatState.Creatures);
            RestoreCardPlayEntries(power, state.DirectEntries, context, creaturesByKey);
            RestoreCardPlayEntries(GetPowerInternalData(power), state.InternalEntries, context, creaturesByKey);
        }

        private static List<UndoNamedCardPlayRuntimeEntry> CaptureCardPlayEntries(object? target, UndoRuntimeCaptureContext context)
        {
            List<UndoNamedCardPlayRuntimeEntry> entries = [];
            if (target == null)
                return entries;

            Type type = target.GetType();
            foreach (PropertyInfo property in GetDeclaredReferenceRuntimeProperties(type, typeof(CardPlay)))
            {
                entries.Add(new UndoNamedCardPlayRuntimeEntry
                {
                    Name = property.Name,
                    CardPlay = property.GetValue(target) is CardPlay cardPlay
                        ? UndoCombatHistoryCodec.CaptureCardPlay(context.RunState, context.CombatState.Creatures, cardPlay)
                        : null
                });
            }

            foreach (FieldInfo field in GetDeclaredReferenceRuntimeFields(type, typeof(CardPlay)))
            {
                entries.Add(new UndoNamedCardPlayRuntimeEntry
                {
                    Name = field.Name,
                    CardPlay = field.GetValue(target) is CardPlay cardPlay
                        ? UndoCombatHistoryCodec.CaptureCardPlay(context.RunState, context.CombatState.Creatures, cardPlay)
                        : null
                });
            }

            return entries;
        }

        private static void RestoreCardPlayEntries(
            object? target,
            IReadOnlyList<UndoNamedCardPlayRuntimeEntry> entries,
            UndoRuntimeRestoreContext context,
            IReadOnlyDictionary<string, Creature> creaturesByKey)
        {
            if (target == null)
                return;

            Type type = target.GetType();
            foreach (UndoNamedCardPlayRuntimeEntry entry in entries)
            {
                CardPlay? cardPlay = entry.CardPlay == null
                    ? null
                    : UndoCombatHistoryCodec.RestoreCardPlay(context.RunState, creaturesByKey, entry.CardPlay, context.CardResolutionIndex);
                if (UndoReflectionUtil.FindProperty(type, entry.Name)?.PropertyType == typeof(CardPlay))
                {
                    UndoReflectionUtil.TrySetPropertyValue(target, entry.Name, cardPlay);
                    continue;
                }

                if (UndoReflectionUtil.FindField(type, entry.Name)?.FieldType == typeof(CardPlay))
                    UndoReflectionUtil.TrySetFieldValue(target, entry.Name, cardPlay);
            }
        }
    }

    private sealed class GenericPowerCardRefCollectionsCodec : UndoPowerRuntimeCodec<UndoPowerCardRefCollectionsRuntimeComplexState>
    {
        public override string CodecId => "power:Generic.cardCollections";

        public override bool CanHandle(PowerModel power)
        {
            object? internalData = GetPowerInternalData(power);
            Type type = power.GetType();
            return GetDeclaredCollectionRuntimeProperties(type, typeof(CardModel)).Any()
                || GetDeclaredCollectionRuntimeFields(type, typeof(CardModel)).Any()
                || (internalData != null
                    && (GetDeclaredCollectionRuntimeProperties(internalData.GetType(), typeof(CardModel)).Any()
                        || GetDeclaredCollectionRuntimeFields(internalData.GetType(), typeof(CardModel)).Any()));
        }

        public override UndoPowerCardRefCollectionsRuntimeComplexState? Capture(PowerModel power, UndoRuntimeCaptureContext context)
        {
            List<UndoNamedCardRefCollectionRuntimeEntry> directCollections = CaptureCardCollections(power, context);
            List<UndoNamedCardRefCollectionRuntimeEntry> internalCollections = CaptureCardCollections(GetPowerInternalData(power), context);
            if (directCollections.Count == 0 && internalCollections.Count == 0)
                return null;

            return new UndoPowerCardRefCollectionsRuntimeComplexState
            {
                CodecId = CodecId,
                DirectCollections = directCollections,
                InternalCollections = internalCollections
            };
        }

        public override void Restore(PowerModel power, UndoPowerCardRefCollectionsRuntimeComplexState state, UndoRuntimeRestoreContext context)
        {
            RestoreCardCollections(power, state.DirectCollections, context);
            RestoreCardCollections(GetPowerInternalData(power), state.InternalCollections, context);
        }

        private static List<UndoNamedCardRefCollectionRuntimeEntry> CaptureCardCollections(object? target, UndoRuntimeCaptureContext context)
        {
            List<UndoNamedCardRefCollectionRuntimeEntry> collections = [];
            if (target == null)
                return collections;

            Type type = target.GetType();
            foreach (PropertyInfo property in GetDeclaredCollectionRuntimeProperties(type, typeof(CardModel)))
            {
                if (property.GetValue(target) is not IEnumerable<CardModel> cards)
                    continue;

                collections.Add(new UndoNamedCardRefCollectionRuntimeEntry
                {
                    Name = property.Name,
                    Cards = cards.Select(card => UndoStableRefs.CaptureCardRef(context.RunState, card)).ToList()
                });
            }

            foreach (FieldInfo field in GetDeclaredCollectionRuntimeFields(type, typeof(CardModel)))
            {
                if (field.GetValue(target) is not IEnumerable<CardModel> cards)
                    continue;

                collections.Add(new UndoNamedCardRefCollectionRuntimeEntry
                {
                    Name = field.Name,
                    Cards = cards.Select(card => UndoStableRefs.CaptureCardRef(context.RunState, card)).ToList()
                });
            }

            return collections;
        }

        private static void RestoreCardCollections(object? target, IReadOnlyList<UndoNamedCardRefCollectionRuntimeEntry> collections, UndoRuntimeRestoreContext context)
        {
            if (target == null)
                return;

            Type type = target.GetType();
            foreach (UndoNamedCardRefCollectionRuntimeEntry collectionState in collections)
            {
                object? collection = UndoReflectionUtil.FindProperty(type, collectionState.Name)?.GetValue(target)
                    ?? UndoReflectionUtil.FindField(type, collectionState.Name)?.GetValue(target);
                if (collection == null)
                    continue;

                List<CardModel> resolvedCards = collectionState.Cards
                    .Select(cardRef => UndoStableRefs.ResolveCardRef(context.RunState, cardRef, context.CardResolutionIndex))
                    .ToList();
                TryReplaceCollectionItems(collection, resolvedCards);
            }
        }
    }

}
