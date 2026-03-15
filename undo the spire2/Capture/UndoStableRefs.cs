// 文件说明：捕获和重建恢复流程需要的稳定引用。
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace UndoTheSpire2;

internal sealed class CreatureRef
{
    public required string Key { get; init; }
}

internal sealed class CardRef
{
    public required SerializableCard Card { get; init; }

    public ulong? PlayerNetId { get; init; }

    public PileType? PileType { get; init; }

    public int? PileIndex { get; init; }
}

internal sealed class PowerRef
{
    public required string OwnerCreatureKey { get; init; }

    public required ModelId PowerId { get; init; }

    public required int Ordinal { get; init; }

    public required int Amount { get; init; }

    public string? TargetCreatureKey { get; init; }

    public string? ApplierCreatureKey { get; init; }
}

internal sealed class RelicRef
{
    public required ulong PlayerNetId { get; init; }

    public required ModelId RelicId { get; init; }

    public required int Ordinal { get; init; }
}

internal sealed class ActionRef
{
    public uint? ActionId { get; init; }

    public uint? HookId { get; init; }

    public string? TypeName { get; init; }
}

internal sealed class PotionRef
{
    public required ulong PlayerNetId { get; init; }

    public required ModelId PotionId { get; init; }

    public required int SlotIndex { get; init; }
}

internal sealed class OrbRef
{
    public required ulong PlayerNetId { get; init; }

    public required ModelId OrbId { get; init; }

    public required int OrbIndex { get; init; }
}

internal static class UndoStableRefs
{
    private static readonly PileType[] CombatPileOrder =
    [
        PileType.Hand,
        PileType.Draw,
        PileType.Discard,
        PileType.Exhaust,
        PileType.Play,
        PileType.Deck
    ];

    public static string BuildCreatureKey(Creature creature, int index)
    {
        if (creature.Player != null)
            return $"player:{index}:{creature.Player.NetId}";

        if (creature.PetOwner != null && creature.Monster != null)
            return $"pet:{index}:{creature.PetOwner.NetId}:{creature.Monster.Id.Entry}";

        if (creature.Monster != null)
            return $"monster:{index}:{creature.Monster.Id.Entry}";

        return $"creature:{index}";
    }

    public static string? TryResolveCreatureKey(IReadOnlyList<Creature> creatures, Creature? target)
    {
        if (target == null)
            return null;

        for (int i = 0; i < creatures.Count; i++)
        {
            if (ReferenceEquals(creatures[i], target))
                return BuildCreatureKey(creatures[i], i);
        }

        if (target.CombatId.HasValue)
        {
            for (int i = 0; i < creatures.Count; i++)
            {
                if (creatures[i].CombatId == target.CombatId)
                    return BuildCreatureKey(creatures[i], i);
            }
        }

        if (target.Player != null)
        {
            for (int i = 0; i < creatures.Count; i++)
            {
                if (creatures[i].Player?.NetId == target.Player.NetId)
                    return BuildCreatureKey(creatures[i], i);
            }
        }

        if (target.PetOwner != null && target.Monster != null)
        {
            for (int i = 0; i < creatures.Count; i++)
            {
                Creature candidate = creatures[i];
                if (candidate.PetOwner?.NetId != target.PetOwner.NetId)
                    continue;

                if (candidate.Monster?.Id != target.Monster.Id)
                    continue;

                return BuildCreatureKey(candidate, i);
            }
        }

        if (target.Monster != null)
        {
            for (int i = 0; i < creatures.Count; i++)
            {
                Creature candidate = creatures[i];
                if (candidate.Monster?.Id != target.Monster.Id)
                    continue;

                if (!string.IsNullOrWhiteSpace(target.SlotName) && candidate.SlotName == target.SlotName)
                    return BuildCreatureKey(candidate, i);
            }

            for (int i = 0; i < creatures.Count; i++)
            {
                Creature candidate = creatures[i];
                if (candidate.Monster?.Id == target.Monster.Id)
                    return BuildCreatureKey(candidate, i);
            }
        }

        return null;
    }

    public static Dictionary<string, Creature> BuildCreatureKeyMap(IReadOnlyList<Creature> creatures)
    {
        Dictionary<string, Creature> map = [];
        for (int i = 0; i < creatures.Count; i++)
            map[BuildCreatureKey(creatures[i], i)] = creatures[i];

        return map;
    }

    public static Creature? ResolveCreature(IReadOnlyDictionary<string, Creature> creaturesByKey, string? creatureKey)
    {
        if (string.IsNullOrWhiteSpace(creatureKey))
            return null;

        return creaturesByKey.TryGetValue(creatureKey, out Creature? creature) ? creature : null;
    }

    public static CreatureRef? CaptureCreatureRef(IReadOnlyList<Creature> creatures, Creature? creature)
    {
        string? key = TryResolveCreatureKey(creatures, creature);
        return string.IsNullOrWhiteSpace(key) ? null : new CreatureRef { Key = key };
    }

    public static CardRef CaptureCardRef(RunState runState, CardModel card)
    {
        SerializableCard serializableCard = UndoSerializationUtil.ClonePacketSerializable(card.ToSerializable());
        if (TryLocateCard(runState, card, out ulong playerNetId, out PileType pileType, out int pileIndex))
        {
            return new CardRef
            {
                Card = serializableCard,
                PlayerNetId = playerNetId,
                PileType = pileType,
                PileIndex = pileIndex
            };
        }

        return new CardRef
        {
            Card = serializableCard,
            PlayerNetId = card.Owner?.NetId
        };
    }

    public static CardModel ResolveCardRef(RunState runState, CardRef cardRef)
    {
        if (cardRef.PlayerNetId.HasValue && cardRef.PileType.HasValue && cardRef.PileIndex.HasValue)
        {
            Player? player = runState.GetPlayer(cardRef.PlayerNetId.Value);
            CardPile? pile = player == null ? null : CardPile.Get(cardRef.PileType.Value, player);
            if (pile != null
                && cardRef.PileIndex.Value >= 0
                && cardRef.PileIndex.Value < pile.Cards.Count)
            {
                CardModel candidate = pile.Cards[cardRef.PileIndex.Value];
                if (UndoSerializationUtil.PacketDataEquals(candidate.ToSerializable(), cardRef.Card))
                    return candidate;
            }
        }

        foreach (Player player in runState.Players)
        {
            foreach (PileType pileType in CombatPileOrder)
            {
                CardPile? pile = CardPile.Get(pileType, player);
                if (pile == null)
                    continue;

                foreach (CardModel candidate in pile.Cards)
                {
                    if (UndoSerializationUtil.PacketDataEquals(candidate.ToSerializable(), cardRef.Card))
                        return candidate;
                }
            }
        }

        CardModel detachedCard = CardModel.FromSerializable(UndoSerializationUtil.ClonePacketSerializable(cardRef.Card));
        if (detachedCard.Owner == null && cardRef.PlayerNetId.HasValue)
            detachedCard.Owner = runState.GetPlayer(cardRef.PlayerNetId.Value);
        return detachedCard;
    }

    public static PowerRef CapturePowerRef(IReadOnlyList<Creature> creatures, PowerModel power)
    {
        Creature owner = power.Owner;
        string ownerCreatureKey = TryResolveCreatureKey(creatures, owner)
            ?? throw new InvalidOperationException($"Could not resolve owner ref for power {power.Id}.");

        int ordinal = 0;
        foreach (PowerModel current in owner.Powers)
        {
            if (current.Id == power.Id)
            {
                if (ReferenceEquals(current, power))
                    break;

                ordinal++;
            }
        }

        return new PowerRef
        {
            OwnerCreatureKey = ownerCreatureKey,
            PowerId = power.Id,
            Ordinal = ordinal,
            Amount = power.Amount,
            TargetCreatureKey = TryResolveCreatureKey(creatures, power.Target),
            ApplierCreatureKey = TryResolveCreatureKey(creatures, power.Applier)
        };
    }

    public static RelicRef CaptureRelicRef(Player player, RelicModel relic)
    {
        int ordinal = 0;
        foreach (RelicModel current in player.Relics)
        {
            if (current.Id == relic.Id)
            {
                if (ReferenceEquals(current, relic))
                    break;

                ordinal++;
            }
        }

        return new RelicRef
        {
            PlayerNetId = player.NetId,
            RelicId = relic.Id,
            Ordinal = ordinal
        };
    }

    private static bool TryLocateCard(RunState runState, CardModel card, out ulong playerNetId, out PileType pileType, out int pileIndex)
    {
        foreach (Player player in runState.Players)
        {
            foreach (PileType currentPileType in CombatPileOrder)
            {
                CardPile? pile = CardPile.Get(currentPileType, player);
                if (pile == null)
                    continue;

                for (int i = 0; i < pile.Cards.Count; i++)
                {
                    if (!ReferenceEquals(pile.Cards[i], card))
                        continue;

                    playerNetId = player.NetId;
                    pileType = currentPileType;
                    pileIndex = i;
                    return true;
                }
            }
        }

        playerNetId = 0;
        pileType = default;
        pileIndex = -1;
        return false;
    }
}


