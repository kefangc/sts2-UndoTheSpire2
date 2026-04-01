using System.Reflection;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;

namespace UndoTheSpire2;

internal static partial class UndoRuntimeStateCodecRegistry
{
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

    private sealed class GenericRelicScalarFieldsCodec : UndoRelicRuntimeCodec<UndoRelicScalarFieldsRuntimeComplexState>
    {
        public override string CodecId => "relic:Generic.scalarFields";

        public override bool CanHandle(RelicModel relic)
        {
            return GetDeclaredScalarRuntimeFields(relic.GetType()).Any();
        }

        public override UndoRelicScalarFieldsRuntimeComplexState? Capture(RelicModel relic, UndoRuntimeCaptureContext context)
        {
            List<UndoNamedBoolState> boolFields = [];
            List<UndoNamedIntState> intFields = [];
            List<UndoNamedEnumState> enumFields = [];
            foreach (FieldInfo field in GetDeclaredScalarRuntimeFields(relic.GetType()))
            {
                object? rawValue = field.GetValue(relic);
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

            return new UndoRelicScalarFieldsRuntimeComplexState
            {
                CodecId = CodecId,
                BoolFields = boolFields,
                IntFields = intFields,
                EnumFields = enumFields
            };
        }

        public override void Restore(RelicModel relic, UndoRelicScalarFieldsRuntimeComplexState state, UndoRuntimeRestoreContext context)
        {
            bool restoredAny = false;
            foreach (UndoNamedBoolState fieldState in state.BoolFields)
            {
                if (UndoReflectionUtil.FindField(relic.GetType(), fieldState.Name)?.FieldType == typeof(bool))
                {
                    UndoReflectionUtil.TrySetFieldValue(relic, fieldState.Name, fieldState.Value);
                    restoredAny = true;
                }
            }

            foreach (UndoNamedIntState fieldState in state.IntFields)
            {
                if (UndoReflectionUtil.FindField(relic.GetType(), fieldState.Name)?.FieldType == typeof(int))
                {
                    UndoReflectionUtil.TrySetFieldValue(relic, fieldState.Name, fieldState.Value);
                    restoredAny = true;
                }
            }

            foreach (UndoNamedEnumState fieldState in state.EnumFields)
            {
                FieldInfo? field = UndoReflectionUtil.FindField(relic.GetType(), fieldState.Name);
                if (field == null || !field.FieldType.IsEnum)
                    continue;

                UndoReflectionUtil.TrySetFieldValue(relic, fieldState.Name, Enum.ToObject(field.FieldType, fieldState.Value));
                restoredAny = true;
            }

            if (restoredAny)
                RefreshRelicCounterDisplay(relic);
        }
    }

    private sealed class GenericRelicCardRefFieldsCodec : UndoRelicRuntimeCodec<UndoNamedCardRefFieldsRuntimeComplexState>
    {
        public override string CodecId => "relic:Generic.cardRefs";

        public override bool CanHandle(RelicModel relic)
        {
            Type type = relic.GetType();
            return GetDeclaredReferenceRuntimeProperties(type, typeof(CardModel)).Any()
                || GetDeclaredReferenceRuntimeFields(type, typeof(CardModel)).Any();
        }

        public override UndoNamedCardRefFieldsRuntimeComplexState? Capture(RelicModel relic, UndoRuntimeCaptureContext context)
        {
            List<UndoNamedCardRefRuntimeEntry> entries = [];
            Type type = relic.GetType();
            foreach (PropertyInfo property in GetDeclaredReferenceRuntimeProperties(type, typeof(CardModel)))
            {
                entries.Add(new UndoNamedCardRefRuntimeEntry
                {
                    Name = property.Name,
                    Card = property.GetValue(relic) is CardModel card ? UndoStableRefs.CaptureCardRef(context.RunState, card) : null
                });
            }

            foreach (FieldInfo field in GetDeclaredReferenceRuntimeFields(type, typeof(CardModel)))
            {
                entries.Add(new UndoNamedCardRefRuntimeEntry
                {
                    Name = field.Name,
                    Card = field.GetValue(relic) is CardModel card ? UndoStableRefs.CaptureCardRef(context.RunState, card) : null
                });
            }

            return new UndoNamedCardRefFieldsRuntimeComplexState
            {
                CodecId = CodecId,
                Entries = entries
            };
        }

        public override void Restore(RelicModel relic, UndoNamedCardRefFieldsRuntimeComplexState state, UndoRuntimeRestoreContext context)
        {
            foreach (UndoNamedCardRefRuntimeEntry entry in state.Entries)
            {
                CardModel? card = entry.Card == null ? null : UndoStableRefs.ResolveCardRef(context.RunState, entry.Card);
                if (UndoReflectionUtil.FindProperty(relic.GetType(), entry.Name)?.PropertyType == typeof(CardModel))
                {
                    UndoReflectionUtil.TrySetPropertyValue(relic, entry.Name, card);
                    continue;
                }

                if (UndoReflectionUtil.FindField(relic.GetType(), entry.Name)?.FieldType == typeof(CardModel))
                    UndoReflectionUtil.TrySetFieldValue(relic, entry.Name, card);
            }
        }
    }

    private sealed class GenericRelicCardPlayFieldsCodec : UndoRelicRuntimeCodec<UndoNamedCardPlayFieldsRuntimeComplexState>
    {
        public override string CodecId => "relic:Generic.cardPlays";

        public override bool CanHandle(RelicModel relic)
        {
            Type type = relic.GetType();
            return GetDeclaredReferenceRuntimeProperties(type, typeof(CardPlay)).Any()
                || GetDeclaredReferenceRuntimeFields(type, typeof(CardPlay)).Any();
        }

        public override UndoNamedCardPlayFieldsRuntimeComplexState? Capture(RelicModel relic, UndoRuntimeCaptureContext context)
        {
            List<UndoNamedCardPlayRuntimeEntry> entries = [];
            Type type = relic.GetType();
            foreach (PropertyInfo property in GetDeclaredReferenceRuntimeProperties(type, typeof(CardPlay)))
            {
                entries.Add(new UndoNamedCardPlayRuntimeEntry
                {
                    Name = property.Name,
                    CardPlay = property.GetValue(relic) is CardPlay cardPlay
                        ? UndoCombatHistoryCodec.CaptureCardPlay(context.RunState, context.CombatState.Creatures, cardPlay)
                        : null
                });
            }

            foreach (FieldInfo field in GetDeclaredReferenceRuntimeFields(type, typeof(CardPlay)))
            {
                entries.Add(new UndoNamedCardPlayRuntimeEntry
                {
                    Name = field.Name,
                    CardPlay = field.GetValue(relic) is CardPlay cardPlay
                        ? UndoCombatHistoryCodec.CaptureCardPlay(context.RunState, context.CombatState.Creatures, cardPlay)
                        : null
                });
            }

            return new UndoNamedCardPlayFieldsRuntimeComplexState
            {
                CodecId = CodecId,
                Entries = entries
            };
        }

        public override void Restore(RelicModel relic, UndoNamedCardPlayFieldsRuntimeComplexState state, UndoRuntimeRestoreContext context)
        {
            Dictionary<string, Creature> creaturesByKey = UndoStableRefs.BuildCreatureKeyMap(context.CombatState.Creatures);
            foreach (UndoNamedCardPlayRuntimeEntry entry in state.Entries)
            {
                CardPlay? cardPlay = entry.CardPlay == null
                    ? null
                    : UndoCombatHistoryCodec.RestoreCardPlay(context.RunState, creaturesByKey, entry.CardPlay);
                if (UndoReflectionUtil.FindProperty(relic.GetType(), entry.Name)?.PropertyType == typeof(CardPlay))
                {
                    UndoReflectionUtil.TrySetPropertyValue(relic, entry.Name, cardPlay);
                    continue;
                }

                if (UndoReflectionUtil.FindField(relic.GetType(), entry.Name)?.FieldType == typeof(CardPlay))
                    UndoReflectionUtil.TrySetFieldValue(relic, entry.Name, cardPlay);
            }
        }
    }

    private sealed class GenericRelicCardRefCollectionsCodec : UndoRelicRuntimeCodec<UndoNamedCardRefCollectionsRuntimeComplexState>
    {
        public override string CodecId => "relic:Generic.cardCollections";

        public override bool CanHandle(RelicModel relic)
        {
            Type type = relic.GetType();
            return GetDeclaredCollectionRuntimeProperties(type, typeof(CardModel)).Any()
                || GetDeclaredCollectionRuntimeFields(type, typeof(CardModel)).Any();
        }

        public override UndoNamedCardRefCollectionsRuntimeComplexState? Capture(RelicModel relic, UndoRuntimeCaptureContext context)
        {
            List<UndoNamedCardRefCollectionRuntimeEntry> collections = [];
            Type type = relic.GetType();
            foreach (PropertyInfo property in GetDeclaredCollectionRuntimeProperties(type, typeof(CardModel)))
            {
                if (property.GetValue(relic) is not IEnumerable<CardModel> cards)
                    continue;

                collections.Add(new UndoNamedCardRefCollectionRuntimeEntry
                {
                    Name = property.Name,
                    Cards = cards.Select(card => UndoStableRefs.CaptureCardRef(context.RunState, card)).ToList()
                });
            }

            foreach (FieldInfo field in GetDeclaredCollectionRuntimeFields(type, typeof(CardModel)))
            {
                if (field.GetValue(relic) is not IEnumerable<CardModel> cards)
                    continue;

                collections.Add(new UndoNamedCardRefCollectionRuntimeEntry
                {
                    Name = field.Name,
                    Cards = cards.Select(card => UndoStableRefs.CaptureCardRef(context.RunState, card)).ToList()
                });
            }

            return new UndoNamedCardRefCollectionsRuntimeComplexState
            {
                CodecId = CodecId,
                Collections = collections
            };
        }

        public override void Restore(RelicModel relic, UndoNamedCardRefCollectionsRuntimeComplexState state, UndoRuntimeRestoreContext context)
        {
            foreach (UndoNamedCardRefCollectionRuntimeEntry collectionState in state.Collections)
            {
                object? collection = UndoReflectionUtil.FindProperty(relic.GetType(), collectionState.Name)?.GetValue(relic)
                    ?? UndoReflectionUtil.FindField(relic.GetType(), collectionState.Name)?.GetValue(relic);
                if (collection == null)
                    continue;

                List<CardModel> resolvedCards = collectionState.Cards
                    .Select(cardRef => UndoStableRefs.ResolveCardRef(context.RunState, cardRef))
                    .ToList();
                TryReplaceCollectionItems(collection, resolvedCards);
            }
        }
    }

    private sealed class GenericRelicPowerRefCollectionsCodec : UndoRelicRuntimeCodec<UndoNamedPowerRefCollectionsRuntimeComplexState>
    {
        public override string CodecId => "relic:Generic.powerCollections";

        public override bool CanHandle(RelicModel relic)
        {
            Type type = relic.GetType();
            return GetDeclaredCollectionRuntimeProperties(type, typeof(PowerModel)).Any()
                || GetDeclaredCollectionRuntimeFields(type, typeof(PowerModel)).Any();
        }

        public override UndoNamedPowerRefCollectionsRuntimeComplexState? Capture(RelicModel relic, UndoRuntimeCaptureContext context)
        {
            List<UndoNamedPowerRefCollectionRuntimeEntry> collections = [];
            Type type = relic.GetType();
            foreach (PropertyInfo property in GetDeclaredCollectionRuntimeProperties(type, typeof(PowerModel)))
            {
                if (property.GetValue(relic) is not IEnumerable<PowerModel> powers)
                    continue;

                collections.Add(new UndoNamedPowerRefCollectionRuntimeEntry
                {
                    Name = property.Name,
                    Entries = powers.Select(power => new UndoNamedPowerRefRuntimeEntry
                    {
                        Name = power.GetType().Name,
                        Power = UndoStableRefs.CapturePowerRef(context.CombatState.Creatures, power)
                    }).ToList()
                });
            }

            foreach (FieldInfo field in GetDeclaredCollectionRuntimeFields(type, typeof(PowerModel)))
            {
                if (field.GetValue(relic) is not IEnumerable<PowerModel> powers)
                    continue;

                collections.Add(new UndoNamedPowerRefCollectionRuntimeEntry
                {
                    Name = field.Name,
                    Entries = powers.Select(power => new UndoNamedPowerRefRuntimeEntry
                    {
                        Name = power.GetType().Name,
                        Power = UndoStableRefs.CapturePowerRef(context.CombatState.Creatures, power)
                    }).ToList()
                });
            }

            return new UndoNamedPowerRefCollectionsRuntimeComplexState
            {
                CodecId = CodecId,
                Collections = collections
            };
        }

        public override void Restore(RelicModel relic, UndoNamedPowerRefCollectionsRuntimeComplexState state, UndoRuntimeRestoreContext context)
        {
            Dictionary<string, Creature> creaturesByKey = UndoStableRefs.BuildCreatureKeyMap(context.CombatState.Creatures);
            foreach (UndoNamedPowerRefCollectionRuntimeEntry collectionState in state.Collections)
            {
                object? collection = UndoReflectionUtil.FindProperty(relic.GetType(), collectionState.Name)?.GetValue(relic)
                    ?? UndoReflectionUtil.FindField(relic.GetType(), collectionState.Name)?.GetValue(relic);
                if (collection == null)
                    continue;

                List<PowerModel> powers = [];
                foreach (UndoNamedPowerRefRuntimeEntry entry in collectionState.Entries)
                {
                    PowerModel? power = ResolvePowerRef(creaturesByKey, entry.Power);
                    if (power != null)
                        powers.Add(power);
                }

                TryReplaceCollectionItems(collection, powers);
            }
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
