using System.Collections;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Orbs;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Entities.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace UndoTheSpire2;

internal static class UndoCombatHistoryCodec
{
    public static UndoCombatHistoryState Capture(RunState runState, CombatState combatState)
    {
        IReadOnlyList<Creature> creatures = combatState.Creatures;
        List<UndoCombatHistoryEntryState> entries = [];
        foreach (CombatHistoryEntry entry in CombatManager.Instance.History.Entries)
        {
            try
            {
                entries.Add(CaptureEntry(runState, creatures, entry));
            }
            catch (Exception ex)
            {
                MainFile.Logger.Warn($"Skipping combat history entry {entry.GetType().Name} during undo capture: {ex.Message}");
                UndoDebugLog.Write($"history entry skipped type={entry.GetType().Name} reason={ex.GetType().Name}:{ex.Message}");
            }
        }

        return new UndoCombatHistoryState
        {
            Entries = entries
        };
    }

    public static void Restore(RunState runState, CombatState combatState, UndoCombatHistoryState historyState)
    {
        CombatHistory history = CombatManager.Instance.History;
        history.Clear();
        if (UndoReflectionUtil.FindField(typeof(CombatHistory), "_entries")?.GetValue(history) is not IList entries)
            throw new InvalidOperationException("Could not access CombatHistory._entries.");

        Dictionary<string, Creature> creaturesByKey = UndoStableRefs.BuildCreatureKeyMap(combatState.Creatures);
        foreach (UndoCombatHistoryEntryState state in historyState.Entries)
            entries.Add(RestoreEntry(runState, creaturesByKey, history, state));

        if (UndoReflectionUtil.FindField(typeof(CombatHistory), "Changed")?.GetValue(history) is Action changed)
            changed();
    }

    private static UndoCombatHistoryEntryState CaptureEntry(RunState runState, IReadOnlyList<Creature> creatures, CombatHistoryEntry entry)
    {
        CreatureRef actor = CaptureEntryActor(creatures, entry);

        return entry switch
        {
            CardPlayStartedEntry value => new UndoCombatHistoryEntryState
            {
                Kind = UndoCombatHistoryEntryKind.CardPlayStarted,
                Actor = actor,
                RoundNumber = value.RoundNumber,
                CurrentSide = value.CurrentSide,
                CardPlay = CaptureCardPlay(runState, creatures, value.CardPlay)
            },
            CardPlayFinishedEntry value => new UndoCombatHistoryEntryState
            {
                Kind = UndoCombatHistoryEntryKind.CardPlayFinished,
                Actor = actor,
                RoundNumber = value.RoundNumber,
                CurrentSide = value.CurrentSide,
                CardPlay = CaptureCardPlay(runState, creatures, value.CardPlay),
                BoolValue = value.WasEthereal
            },
            CardAfflictedEntry value => new UndoCombatHistoryEntryState
            {
                Kind = UndoCombatHistoryEntryKind.CardAfflicted,
                Actor = actor,
                RoundNumber = value.RoundNumber,
                CurrentSide = value.CurrentSide,
                Card = UndoStableRefs.CaptureCardRef(runState, value.Card),
                AfflictionId = value.Affliction.Id
            },
            CardDiscardedEntry value => new UndoCombatHistoryEntryState
            {
                Kind = UndoCombatHistoryEntryKind.CardDiscarded,
                Actor = actor,
                RoundNumber = value.RoundNumber,
                CurrentSide = value.CurrentSide,
                Card = UndoStableRefs.CaptureCardRef(runState, value.Card)
            },
            CardDrawnEntry value => new UndoCombatHistoryEntryState
            {
                Kind = UndoCombatHistoryEntryKind.CardDrawn,
                Actor = actor,
                RoundNumber = value.RoundNumber,
                CurrentSide = value.CurrentSide,
                Card = UndoStableRefs.CaptureCardRef(runState, value.Card),
                BoolValue = value.FromHandDraw
            },
            CardExhaustedEntry value => new UndoCombatHistoryEntryState
            {
                Kind = UndoCombatHistoryEntryKind.CardExhausted,
                Actor = actor,
                RoundNumber = value.RoundNumber,
                CurrentSide = value.CurrentSide,
                Card = UndoStableRefs.CaptureCardRef(runState, value.Card)
            },
            CardGeneratedEntry value => new UndoCombatHistoryEntryState
            {
                Kind = UndoCombatHistoryEntryKind.CardGenerated,
                Actor = actor,
                RoundNumber = value.RoundNumber,
                CurrentSide = value.CurrentSide,
                Card = UndoStableRefs.CaptureCardRef(runState, value.Card),
                BoolValue = value.GeneratedByPlayer
            },
            CreatureAttackedEntry value => new UndoCombatHistoryEntryState
            {
                Kind = UndoCombatHistoryEntryKind.CreatureAttacked,
                Actor = actor,
                RoundNumber = value.RoundNumber,
                CurrentSide = value.CurrentSide,
                DamageResults = [.. value.DamageResults.Select(result => CaptureDamageResult(creatures, result))]
            },
            DamageReceivedEntry value => new UndoCombatHistoryEntryState
            {
                Kind = UndoCombatHistoryEntryKind.DamageReceived,
                Actor = actor,
                RoundNumber = value.RoundNumber,
                CurrentSide = value.CurrentSide,
                DamageResult = CaptureDamageResult(creatures, value.Result),
                OtherCreature = UndoStableRefs.CaptureCreatureRef(creatures, value.Dealer),
                CardSource = value.CardSource == null ? null : UndoStableRefs.CaptureCardRef(runState, value.CardSource)
            },
            BlockGainedEntry value => new UndoCombatHistoryEntryState
            {
                Kind = UndoCombatHistoryEntryKind.BlockGained,
                Actor = actor,
                RoundNumber = value.RoundNumber,
                CurrentSide = value.CurrentSide,
                Props = value.Props,
                IntValue = value.Amount,
                CardPlay = value.CardPlay == null ? null : CaptureCardPlay(runState, creatures, value.CardPlay)
            },
            EnergySpentEntry value => new UndoCombatHistoryEntryState
            {
                Kind = UndoCombatHistoryEntryKind.EnergySpent,
                Actor = actor,
                RoundNumber = value.RoundNumber,
                CurrentSide = value.CurrentSide,
                IntValue = value.Amount
            },
            MonsterPerformedMoveEntry value => new UndoCombatHistoryEntryState
            {
                Kind = UndoCombatHistoryEntryKind.MonsterPerformedMove,
                Actor = actor,
                RoundNumber = value.RoundNumber,
                CurrentSide = value.CurrentSide,
                MonsterMove = new UndoMonsterPerformedMoveState
                {
                    Monster = actor,
                    MonsterId = value.Monster.Id,
                    MoveId = value.Move.Id,
                    Targets = value.Targets?.Select(target => UndoStableRefs.CaptureCreatureRef(creatures, target))
                        .Where(static target => target != null)
                        .Cast<CreatureRef>()
                        .ToList() ?? []
                }
            },
            OrbChanneledEntry value => new UndoCombatHistoryEntryState
            {
                Kind = UndoCombatHistoryEntryKind.OrbChanneled,
                Actor = actor,
                RoundNumber = value.RoundNumber,
                CurrentSide = value.CurrentSide,
                Orb = CaptureOrbRef(value.Orb)
            },
            PotionUsedEntry value => new UndoCombatHistoryEntryState
            {
                Kind = UndoCombatHistoryEntryKind.PotionUsed,
                Actor = actor,
                RoundNumber = value.RoundNumber,
                CurrentSide = value.CurrentSide,
                Potion = CapturePotionRef(value.Potion),
                OtherCreature = UndoStableRefs.CaptureCreatureRef(creatures, value.Target)
            },
            PowerReceivedEntry value => new UndoCombatHistoryEntryState
            {
                Kind = UndoCombatHistoryEntryKind.PowerReceived,
                Actor = actor,
                RoundNumber = value.RoundNumber,
                CurrentSide = value.CurrentSide,
                Power = UndoStableRefs.CapturePowerRef(creatures, value.Power),
                OtherCreature = UndoStableRefs.CaptureCreatureRef(creatures, value.Applier),
                DecimalValue = value.Amount
            },
            StarsModifiedEntry value => new UndoCombatHistoryEntryState
            {
                Kind = UndoCombatHistoryEntryKind.StarsModified,
                Actor = actor,
                RoundNumber = value.RoundNumber,
                CurrentSide = value.CurrentSide,
                IntValue = value.Amount
            },
            SummonedEntry value => new UndoCombatHistoryEntryState
            {
                Kind = UndoCombatHistoryEntryKind.Summoned,
                Actor = actor,
                RoundNumber = value.RoundNumber,
                CurrentSide = value.CurrentSide,
                IntValue = value.Amount
            },
            _ => throw new NotSupportedException($"Unsupported combat history entry type {entry.GetType().FullName}.")
        };
    }

    private static CombatHistoryEntry RestoreEntry(
        RunState runState,
        IReadOnlyDictionary<string, Creature> creaturesByKey,
        CombatHistory history,
        UndoCombatHistoryEntryState state)
    {
        Creature actor = UndoStableRefs.ResolveCreature(creaturesByKey, state.Actor.Key)
            ?? throw new InvalidOperationException($"Could not resolve history actor {state.Actor.Key}.");

        return state.Kind switch
        {
            UndoCombatHistoryEntryKind.CardPlayStarted => new CardPlayStartedEntry(
                RestoreCardPlay(runState, creaturesByKey, state.CardPlay!),
                state.RoundNumber,
                state.CurrentSide,
                history),
            UndoCombatHistoryEntryKind.CardPlayFinished => RestoreCardPlayFinishedEntry(runState, creaturesByKey, history, state),
            UndoCombatHistoryEntryKind.CardAfflicted => new CardAfflictedEntry(
                UndoStableRefs.ResolveCardRef(runState, state.Card!),
                ModelDb.GetById<AfflictionModel>(state.AfflictionId!).ToMutable(),
                state.RoundNumber,
                state.CurrentSide,
                history),
            UndoCombatHistoryEntryKind.CardDiscarded => new CardDiscardedEntry(
                UndoStableRefs.ResolveCardRef(runState, state.Card!),
                state.RoundNumber,
                state.CurrentSide,
                history),
            UndoCombatHistoryEntryKind.CardDrawn => new CardDrawnEntry(
                UndoStableRefs.ResolveCardRef(runState, state.Card!),
                state.RoundNumber,
                state.CurrentSide,
                state.BoolValue,
                history),
            UndoCombatHistoryEntryKind.CardExhausted => new CardExhaustedEntry(
                UndoStableRefs.ResolveCardRef(runState, state.Card!),
                state.RoundNumber,
                state.CurrentSide,
                history),
            UndoCombatHistoryEntryKind.CardGenerated => new CardGeneratedEntry(
                UndoStableRefs.ResolveCardRef(runState, state.Card!),
                state.BoolValue,
                state.RoundNumber,
                state.CurrentSide,
                history),
            UndoCombatHistoryEntryKind.CreatureAttacked => new CreatureAttackedEntry(
                actor,
                [.. state.DamageResults.Select(result => RestoreDamageResult(creaturesByKey, result))],
                state.RoundNumber,
                state.CurrentSide,
                history),
            UndoCombatHistoryEntryKind.DamageReceived => new DamageReceivedEntry(
                RestoreDamageResult(creaturesByKey, state.DamageResult!),
                actor,
                UndoStableRefs.ResolveCreature(creaturesByKey, state.OtherCreature?.Key),
                state.CardSource == null ? null : UndoStableRefs.ResolveCardRef(runState, state.CardSource),
                state.RoundNumber,
                state.CurrentSide,
                history),
            UndoCombatHistoryEntryKind.BlockGained => new BlockGainedEntry(
                state.IntValue,
                state.Props,
                state.CardPlay == null ? null : RestoreCardPlay(runState, creaturesByKey, state.CardPlay),
                actor,
                state.RoundNumber,
                state.CurrentSide,
                history),
            UndoCombatHistoryEntryKind.EnergySpent => new EnergySpentEntry(state.IntValue, actor.Player!, state.RoundNumber, state.CurrentSide, history),
            UndoCombatHistoryEntryKind.MonsterPerformedMove => new MonsterPerformedMoveEntry(
                actor.Monster!,
                CreatePlaceholderMove(state.MonsterMove!.MoveId),
                state.MonsterMove.Targets.Select(target => UndoStableRefs.ResolveCreature(creaturesByKey, target.Key)).Where(static target => target != null)!,
                state.RoundNumber,
                state.CurrentSide,
                history),
            UndoCombatHistoryEntryKind.OrbChanneled => new OrbChanneledEntry(
                RestoreOrbRef(runState, state.Orb!),
                state.RoundNumber,
                state.CurrentSide,
                history),
            UndoCombatHistoryEntryKind.PotionUsed => new PotionUsedEntry(
                RestorePotionRef(runState, state.Potion!),
                UndoStableRefs.ResolveCreature(creaturesByKey, state.OtherCreature?.Key),
                state.RoundNumber,
                state.CurrentSide,
                history),
            UndoCombatHistoryEntryKind.PowerReceived => new PowerReceivedEntry(
                RestorePowerRef(creaturesByKey, state.Power!),
                state.DecimalValue,
                UndoStableRefs.ResolveCreature(creaturesByKey, state.OtherCreature?.Key),
                state.RoundNumber,
                state.CurrentSide,
                history),
            UndoCombatHistoryEntryKind.StarsModified => new StarsModifiedEntry(state.IntValue, actor.Player!, state.RoundNumber, state.CurrentSide, history),
            UndoCombatHistoryEntryKind.Summoned => new SummonedEntry(state.IntValue, actor.Player!, state.RoundNumber, state.CurrentSide, history),
            _ => throw new NotSupportedException($"Unsupported combat history entry kind {state.Kind}.")
        };
    }

    private static CreatureRef CaptureEntryActor(IReadOnlyList<Creature> creatures, CombatHistoryEntry entry)
    {
        CreatureRef? actor = UndoStableRefs.CaptureCreatureRef(creatures, entry.Actor);
        if (actor != null)
            return actor;

        if (entry is PowerReceivedEntry powerReceived)
        {
            actor = UndoStableRefs.CaptureCreatureRef(creatures, powerReceived.Power?.Owner);
            if (actor != null)
                return actor;

            actor = UndoStableRefs.CaptureCreatureRef(creatures, powerReceived.Applier);
            if (actor != null)
                return actor;
        }

        throw new InvalidOperationException($"Could not capture history actor for {entry.GetType().Name}.");
    }

    private static UndoCardPlayState CaptureCardPlay(RunState runState, IReadOnlyList<Creature> creatures, CardPlay cardPlay)
    {
        return new UndoCardPlayState
        {
            Card = UndoStableRefs.CaptureCardRef(runState, cardPlay.Card),
            Target = UndoStableRefs.CaptureCreatureRef(creatures, cardPlay.Target),
            ResultPile = cardPlay.ResultPile,
            Resources = new UndoResourceInfoState
            {
                EnergySpent = cardPlay.Resources.EnergySpent,
                EnergyValue = cardPlay.Resources.EnergyValue,
                StarsSpent = cardPlay.Resources.StarsSpent,
                StarValue = cardPlay.Resources.StarValue
            },
            IsAutoPlay = cardPlay.IsAutoPlay,
            PlayIndex = cardPlay.PlayIndex,
            PlayCount = cardPlay.PlayCount
        };
    }

    private static CardPlay RestoreCardPlay(RunState runState, IReadOnlyDictionary<string, Creature> creaturesByKey, UndoCardPlayState state)
    {
        return new CardPlay
        {
            Card = UndoStableRefs.ResolveCardRef(runState, state.Card),
            Target = UndoStableRefs.ResolveCreature(creaturesByKey, state.Target?.Key),
            ResultPile = state.ResultPile,
            Resources = new ResourceInfo
            {
                EnergySpent = state.Resources.EnergySpent,
                EnergyValue = state.Resources.EnergyValue,
                StarsSpent = state.Resources.StarsSpent,
                StarValue = state.Resources.StarValue
            },
            IsAutoPlay = state.IsAutoPlay,
            PlayIndex = state.PlayIndex,
            PlayCount = state.PlayCount
        };
    }

    private static UndoDamageResultState CaptureDamageResult(IReadOnlyList<Creature> creatures, DamageResult result)
    {
        CreatureRef receiver = UndoStableRefs.CaptureCreatureRef(creatures, result.Receiver)
            ?? throw new InvalidOperationException("Could not capture damage result receiver ref.");
        return new UndoDamageResultState
        {
            Receiver = receiver,
            Props = result.Props,
            BlockedDamage = result.BlockedDamage,
            UnblockedDamage = result.UnblockedDamage,
            OverkillDamage = result.OverkillDamage,
            WasBlockBroken = result.WasBlockBroken,
            WasFullyBlocked = result.WasFullyBlocked,
            WasTargetKilled = result.WasTargetKilled
        };
    }

    private static DamageResult RestoreDamageResult(IReadOnlyDictionary<string, Creature> creaturesByKey, UndoDamageResultState state)
    {
        Creature receiver = UndoStableRefs.ResolveCreature(creaturesByKey, state.Receiver.Key)
            ?? throw new InvalidOperationException($"Could not resolve damage receiver {state.Receiver.Key}.");
        DamageResult result = new(receiver, state.Props)
        {
            BlockedDamage = state.BlockedDamage,
            UnblockedDamage = state.UnblockedDamage,
            OverkillDamage = state.OverkillDamage,
            WasBlockBroken = state.WasBlockBroken,
            WasFullyBlocked = state.WasFullyBlocked,
            WasTargetKilled = state.WasTargetKilled
        };
        return result;
    }

    private static CardPlayFinishedEntry RestoreCardPlayFinishedEntry(
        RunState runState,
        IReadOnlyDictionary<string, Creature> creaturesByKey,
        CombatHistory history,
        UndoCombatHistoryEntryState state)
    {
        CardPlayFinishedEntry entry = new(
            RestoreCardPlay(runState, creaturesByKey, state.CardPlay!),
            state.RoundNumber,
            state.CurrentSide,
            history);
        UndoReflectionUtil.TrySetFieldValue(entry, "<WasEthereal>k__BackingField", state.BoolValue);
        return entry;
    }

    private static PowerModel RestorePowerRef(IReadOnlyDictionary<string, Creature> creaturesByKey, PowerRef powerRef)
    {
        Creature owner = UndoStableRefs.ResolveCreature(creaturesByKey, powerRef.OwnerCreatureKey)
            ?? throw new InvalidOperationException($"Could not resolve power owner {powerRef.OwnerCreatureKey}.");

        int ordinal = 0;
        foreach (PowerModel power in owner.Powers)
        {
            if (power.Id != powerRef.PowerId)
                continue;

            if (ordinal == powerRef.Ordinal)
                return power;

            ordinal++;
        }

        PowerModel detachedPower = ModelDb.GetById<PowerModel>(powerRef.PowerId).ToMutable();
        UndoReflectionUtil.TrySetFieldValue(detachedPower, "_owner", owner);
        UndoReflectionUtil.TrySetFieldValue(detachedPower, "_amount", powerRef.Amount);
        detachedPower.AmountOnTurnStart = powerRef.Amount;
        detachedPower.Target = UndoStableRefs.ResolveCreature(creaturesByKey, powerRef.TargetCreatureKey);
        detachedPower.Applier = UndoStableRefs.ResolveCreature(creaturesByKey, powerRef.ApplierCreatureKey);
        return detachedPower;
    }

    private static PotionRef CapturePotionRef(PotionModel potion)
    {
        Player owner = potion.Owner;
        int slotIndex = 0;
        for (; slotIndex < owner.MaxPotionCount; slotIndex++)
        {
            if (ReferenceEquals(owner.GetPotionAtSlotIndex(slotIndex), potion))
                break;
        }

        return new PotionRef
        {
            PlayerNetId = owner.NetId,
            PotionId = potion.Id,
            SlotIndex = slotIndex
        };
    }

    private static PotionModel RestorePotionRef(RunState runState, PotionRef potionRef)
    {
        Player player = runState.GetPlayer(potionRef.PlayerNetId)
            ?? throw new InvalidOperationException($"Could not resolve potion owner {potionRef.PlayerNetId}.");
        PotionModel? livePotion = player.GetPotionAtSlotIndex(potionRef.SlotIndex);
        if (livePotion?.Id == potionRef.PotionId)
            return livePotion;

        PotionModel detachedPotion = PotionModel.FromSerializable(new SerializablePotion
        {
            Id = potionRef.PotionId,
            SlotIndex = potionRef.SlotIndex
        });
        detachedPotion.Owner = player;
        return detachedPotion;
    }

    private static OrbRef CaptureOrbRef(OrbModel orb)
    {
        Player owner = orb.Owner;
        OrbQueue orbQueue = owner.PlayerCombatState!.OrbQueue;
        int orbIndex = 0;
        for (; orbIndex < orbQueue.Orbs.Count; orbIndex++)
        {
            if (ReferenceEquals(orbQueue.Orbs[orbIndex], orb))
                break;
        }

        return new OrbRef
        {
            PlayerNetId = owner.NetId,
            OrbId = orb.Id,
            OrbIndex = orbIndex
        };
    }

    private static OrbModel RestoreOrbRef(RunState runState, OrbRef orbRef)
    {
        Player player = runState.GetPlayer(orbRef.PlayerNetId)
            ?? throw new InvalidOperationException($"Could not resolve orb owner {orbRef.PlayerNetId}.");
        OrbQueue orbQueue = player.PlayerCombatState!.OrbQueue;
        if (orbRef.OrbIndex >= 0 && orbRef.OrbIndex < orbQueue.Orbs.Count && orbQueue.Orbs[orbRef.OrbIndex].Id == orbRef.OrbId)
            return orbQueue.Orbs[orbRef.OrbIndex];

        OrbModel detachedOrb = ModelDb.GetById<OrbModel>(orbRef.OrbId).ToMutable();
        detachedOrb.Owner = player;
        return detachedOrb;
    }

    private static MoveState CreatePlaceholderMove(string moveId)
    {
        return new MoveState(moveId, _ => Task.CompletedTask);
    }
}





