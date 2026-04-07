using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace UndoTheSpire2;

internal sealed class CardResolutionIndex
{
    private static readonly IReadOnlyList<PileType> CombatPileOrder =
    [
        .. UndoSharedConstants.CombatPileOrder,
        PileType.Deck
    ];

    private readonly Dictionary<CardLocationKey, CardModel> _cardsByLocation = [];
    private readonly Dictionary<string, List<CardModel>> _cardsByFingerprint = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, CardModel> _cardsByCombatCardId = [];
    private readonly Dictionary<CardLocationKey, uint> _combatCardIdsByLocation = [];
    private readonly Dictionary<string, List<uint>> _combatCardIdsByFingerprint = new(StringComparer.Ordinal);

    public CardResolutionIndex(RunState runState, UndoCombatCardDbState? combatCardDbState = null)
    {
        BuildLivePileIndexes(runState);
        BuildLiveCombatCardDbIndex();
        if (combatCardDbState != null)
            MergeSnapshotCombatCardDbIndex(combatCardDbState);
    }

    public bool TryResolve(CardRef cardRef, out CardModel? resolvedCard)
    {
        string targetFingerprint = UndoSerializationUtil.GetPacketFingerprint(cardRef.Card);

        if (TryResolveByLocation(cardRef, targetFingerprint, out resolvedCard))
            return true;

        if (TryResolveByCombatCardDb(cardRef, targetFingerprint, out resolvedCard))
            return true;

        if (_cardsByFingerprint.TryGetValue(targetFingerprint, out List<CardModel>? matches) && matches.Count > 0)
        {
            resolvedCard = matches[0];
            return true;
        }

        resolvedCard = null;
        return false;
    }

    private void BuildLivePileIndexes(RunState runState)
    {
        foreach (Player player in runState.Players)
        {
            foreach (PileType pileType in CombatPileOrder)
            {
                CardPile? pile = CardPile.Get(pileType, player);
                if (pile == null)
                    continue;

                for (int pileIndex = 0; pileIndex < pile.Cards.Count; pileIndex++)
                {
                    CardModel card = pile.Cards[pileIndex];
                    _cardsByLocation[new CardLocationKey(player.NetId, pileType, pileIndex)] = card;
                    AddCardFingerprint(card);
                }
            }
        }
    }

    private void BuildLiveCombatCardDbIndex()
    {
        NetCombatCardDb db = NetCombatCardDb.Instance;
        if (!UndoReflectionUtil.TryGetFieldValue(db, "_idToCard", out System.Collections.IDictionary? idToCard)
            || idToCard == null)
        {
            return;
        }

        foreach (System.Collections.DictionaryEntry entry in idToCard)
        {
            if (entry.Key is uint combatCardId && entry.Value is CardModel card && card.IsMutable)
                _cardsByCombatCardId[combatCardId] = card;
        }
    }

    private void MergeSnapshotCombatCardDbIndex(UndoCombatCardDbState combatCardDbState)
    {
        foreach (UndoCombatCardDbEntryState entry in combatCardDbState.Entries)
        {
            string fingerprint = UndoSerializationUtil.GetPacketFingerprint(entry.Card.Card);
            AddValue(_combatCardIdsByFingerprint, fingerprint, entry.CombatCardId);

            if (TryGetLocation(entry.Card, out CardLocationKey location))
            {
                _combatCardIdsByLocation[location] = entry.CombatCardId;
                if (!_cardsByCombatCardId.ContainsKey(entry.CombatCardId)
                    && _cardsByLocation.TryGetValue(location, out CardModel? liveCard)
                    && CardMatchesFingerprint(liveCard, fingerprint))
                {
                    _cardsByCombatCardId[entry.CombatCardId] = liveCard;
                }
            }
        }
    }

    private bool TryResolveByLocation(CardRef cardRef, string targetFingerprint, out CardModel? resolvedCard)
    {
        if (TryGetLocation(cardRef, out CardLocationKey location)
            && _cardsByLocation.TryGetValue(location, out CardModel? liveCard)
            && CardMatchesFingerprint(liveCard, targetFingerprint))
        {
            resolvedCard = liveCard;
            return true;
        }

        resolvedCard = null;
        return false;
    }

    private bool TryResolveByCombatCardDb(CardRef cardRef, string targetFingerprint, out CardModel? resolvedCard)
    {
        if (TryGetCombatCardId(cardRef, targetFingerprint, out uint combatCardId)
            && _cardsByCombatCardId.TryGetValue(combatCardId, out CardModel? liveCard))
        {
            resolvedCard = liveCard;
            return true;
        }

        resolvedCard = null;
        return false;
    }

    private bool TryGetCombatCardId(CardRef cardRef, string targetFingerprint, out uint combatCardId)
    {
        if (TryGetLocation(cardRef, out CardLocationKey location)
            && _combatCardIdsByLocation.TryGetValue(location, out combatCardId))
        {
            return true;
        }

        if (_combatCardIdsByFingerprint.TryGetValue(targetFingerprint, out List<uint>? combatCardIds))
        {
            foreach (uint candidateId in combatCardIds)
            {
                if (_cardsByCombatCardId.ContainsKey(candidateId))
                {
                    combatCardId = candidateId;
                    return true;
                }
            }
        }

        combatCardId = 0;
        return false;
    }

    private void AddCardFingerprint(CardModel card)
    {
        string fingerprint = UndoSerializationUtil.GetPacketFingerprint(card.ToSerializable());
        AddValue(_cardsByFingerprint, fingerprint, card);
    }

    private static bool CardMatchesFingerprint(CardModel card, string targetFingerprint)
    {
        return string.Equals(
            UndoSerializationUtil.GetPacketFingerprint(card.ToSerializable()),
            targetFingerprint,
            StringComparison.Ordinal);
    }

    private static bool TryGetLocation(CardRef cardRef, out CardLocationKey location)
    {
        if (cardRef.PlayerNetId.HasValue && cardRef.PileType.HasValue && cardRef.PileIndex is int pileIndex && pileIndex >= 0)
        {
            location = new CardLocationKey(cardRef.PlayerNetId.Value, cardRef.PileType.Value, pileIndex);
            return true;
        }

        location = default;
        return false;
    }

    private static void AddValue<TKey, TValue>(Dictionary<TKey, List<TValue>> map, TKey key, TValue value)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out List<TValue>? values))
        {
            values = [];
            map[key] = values;
        }

        values.Add(value);
    }

    private readonly record struct CardLocationKey(ulong PlayerNetId, PileType PileType, int PileIndex);
}
