using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace UndoTheSpire2;

internal sealed class UndoChoiceSpec
{
    private UndoChoiceSpec(
        UndoChoiceKind kind,
        CardSelectorPrefs selectionPrefs,
        IReadOnlyList<SerializableCard> optionCards,
        PileType? sourcePileType,
        IReadOnlyList<int> sourcePileOptionIndexes,
        IReadOnlyList<NetCombatCard> sourcePileCombatCards,
        bool canSkip)
    {
        Kind = kind;
        SelectionPrefs = selectionPrefs;
        OptionCards = optionCards;
        SourcePileType = sourcePileType;
        SourcePileOptionIndexes = sourcePileOptionIndexes;
        SourcePileCombatCards = sourcePileCombatCards;
        CanSkip = canSkip;
    }

    public UndoChoiceKind Kind { get; }

    public CardSelectorPrefs SelectionPrefs { get; }

    public IReadOnlyList<SerializableCard> OptionCards { get; }

    public PileType? SourcePileType { get; }

    public IReadOnlyList<int> SourcePileOptionIndexes { get; }

    public IReadOnlyList<NetCombatCard> SourcePileCombatCards { get; }

    public bool CanSkip { get; }

    public static UndoChoiceSpec CreateChooseACard(IReadOnlyList<CardModel> cards, bool canSkip)
    {
        return new UndoChoiceSpec(
            UndoChoiceKind.ChooseACard,
            default,
            [.. cards.Select(static card => card.ToSerializable())],
            null,
            [],
            [],
            canSkip);
    }

    public static UndoChoiceSpec CreateHandSelection(Player player, CardSelectorPrefs prefs, Func<CardModel, bool>? filter)
    {
        IReadOnlyList<CardModel> handCards = PileType.Hand.GetPile(player).Cards;
        List<int> eligibleIndexes = [];
        List<NetCombatCard> eligibleCombatCards = [];
        for (int i = 0; i < handCards.Count; i++)
        {
            CardModel card = handCards[i];
            if (filter != null && !filter(card))
                continue;

            eligibleIndexes.Add(i);
            eligibleCombatCards.Add(NetCombatCard.FromModel(card));
        }

        return new UndoChoiceSpec(
            UndoChoiceKind.HandSelection,
            prefs,
            [],
            PileType.Hand,
            eligibleIndexes,
            eligibleCombatCards,
            false);
    }

    public static UndoChoiceSpec CreateSimpleGridSelection(Player player, IReadOnlyList<CardModel> cards, CardSelectorPrefs prefs)
    {
        PileType? sourcePileType = TryGetCommonCombatPileType(cards);
        List<int> sourcePileIndexes = [];
        List<NetCombatCard> sourcePileCombatCards = [];
        if (sourcePileType != null)
        {
            IReadOnlyList<CardModel> sourceCards = sourcePileType.Value.GetPile(player).Cards;
            foreach (CardModel card in cards)
            {
                int sourcePileIndex = IndexOfReference(sourceCards, card);
                if (sourcePileIndex < 0)
                {
                    sourcePileType = null;
                    sourcePileIndexes.Clear();
                    sourcePileCombatCards.Clear();
                    break;
                }

                sourcePileIndexes.Add(sourcePileIndex);
                sourcePileCombatCards.Add(NetCombatCard.FromModel(card));
            }
        }

        return new UndoChoiceSpec(
            UndoChoiceKind.SimpleGridSelection,
            prefs,
            [.. cards.Select(static card => card.ToSerializable())],
            sourcePileType,
            sourcePileIndexes,
            sourcePileCombatCards,
            prefs.MinSelect == 0);
    }

    public bool SupportsSyntheticRestore => Kind is UndoChoiceKind.ChooseACard or UndoChoiceKind.HandSelection or UndoChoiceKind.SimpleGridSelection;

    public Func<CardModel, bool> BuildHandFilter(Player player)
    {
        HashSet<int> eligibleIndexes = [.. SourcePileOptionIndexes];
        return card =>
        {
            IReadOnlyList<CardModel> handCards = PileType.Hand.GetPile(player).Cards;
            int index = IndexOfReference(handCards, card);
            return index >= 0 && eligibleIndexes.Contains(index);
        };
    }

    public IReadOnlyList<CardModel> BuildOptionCards(Player player)
    {
        List<CardModel> options = [];
        foreach (SerializableCard optionCard in OptionCards)
        {
            CardModel card = CardModel.FromSerializable(optionCard);
            if (card.Owner == null)
                card.Owner = player;

            options.Add(card);
        }

        return options;
    }

    public UndoChoiceResultKey? TryMapReplayResult(NetPlayerChoiceResult result)
    {
        return Kind switch
        {
            UndoChoiceKind.ChooseACard => TryMapIndexResult(result.indexes),
            UndoChoiceKind.SimpleGridSelection => TryMapIndexResult(result.indexes),
            UndoChoiceKind.HandSelection => TryMapCombatCardResult(result.combatCards),
            _ => null
        };
    }

    public UndoChoiceResultKey? TryMapSyntheticSelection(Player player, IEnumerable<CardModel> selectedCards)
    {
        return Kind switch
        {
            UndoChoiceKind.ChooseACard => TryMapOptionCardSelection(selectedCards, true),
            UndoChoiceKind.SimpleGridSelection => TryMapOptionCardSelection(selectedCards, false),
            UndoChoiceKind.HandSelection => TryMapHandSelection(player, selectedCards),
            _ => null
        };
    }

    private UndoChoiceResultKey? TryMapIndexResult(List<int>? indexes)
    {
        if (indexes == null)
            return null;

        return new UndoChoiceResultKey(indexes.OrderBy(static index => index));
    }

    private UndoChoiceResultKey? TryMapCombatCardResult(List<NetCombatCard>? combatCards)
    {
        if (combatCards == null)
            return null;

        List<int> mappedIndexes = [];
        foreach (NetCombatCard combatCard in combatCards)
        {
            int mappedIndex = IndexOfValue(SourcePileCombatCards, combatCard);
            if (mappedIndex < 0)
                return null;

            mappedIndexes.Add(mappedIndex);
        }

        mappedIndexes.Sort();
        return new UndoChoiceResultKey(mappedIndexes);
    }

    private UndoChoiceResultKey? TryMapOptionCardSelection(IEnumerable<CardModel> selectedCards, bool useNegativeSkipIndex)
    {
        List<CardModel> cards = [.. selectedCards];
        if (cards.Count == 0)
        {
            if (!CanSkip)
                return null;

            return useNegativeSkipIndex ? new UndoChoiceResultKey([-1]) : new UndoChoiceResultKey([]);
        }

        bool[] used = new bool[OptionCards.Count];
        List<int> mappedIndexes = [];
        foreach (CardModel card in cards)
        {
            SerializableCard serializableCard = card.ToSerializable();
            int mappedIndex = -1;
            for (int i = 0; i < OptionCards.Count; i++)
            {
                if (used[i] || !PacketDataEquals(OptionCards[i], serializableCard))
                    continue;

                used[i] = true;
                mappedIndex = i;
                break;
            }

            if (mappedIndex < 0)
                return null;

            mappedIndexes.Add(mappedIndex);
        }

        mappedIndexes.Sort();
        return new UndoChoiceResultKey(mappedIndexes);
    }

    private UndoChoiceResultKey? TryMapHandSelection(Player player, IEnumerable<CardModel> selectedCards)
    {
        IReadOnlyList<CardModel> handCards = PileType.Hand.GetPile(player).Cards;
        List<int> mappedIndexes = [];
        foreach (CardModel card in selectedCards)
        {
            int handIndex = IndexOfReference(handCards, card);
            if (handIndex < 0)
                return null;

            int optionIndex = IndexOfValue(SourcePileOptionIndexes, handIndex);
            if (optionIndex < 0)
                return null;

            mappedIndexes.Add(optionIndex);
        }

        mappedIndexes.Sort();
        return new UndoChoiceResultKey(mappedIndexes);
    }

    private static PileType? TryGetCommonCombatPileType(IReadOnlyList<CardModel> cards)
    {
        PileType? sourcePileType = null;
        foreach (CardModel card in cards)
        {
            CardPile? pile = card.Pile;
            if (pile == null || !pile.IsCombatPile)
                return null;

            if (sourcePileType == null)
            {
                sourcePileType = pile.Type;
                continue;
            }

            if (sourcePileType.Value != pile.Type)
                return null;
        }

        return sourcePileType;
    }

    private static int IndexOfReference(IReadOnlyList<CardModel> cards, CardModel target)
    {
        for (int i = 0; i < cards.Count; i++)
        {
            if (ReferenceEquals(cards[i], target))
                return i;
        }

        return -1;
    }

    private static int IndexOfValue<T>(IReadOnlyList<T> values, T target) where T : IEquatable<T>
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i].Equals(target))
                return i;
        }

        return -1;
    }

    private static bool PacketDataEquals<T>(T left, T right) where T : IPacketSerializable
    {
        return Serialize(left).AsSpan().SequenceEqual(Serialize(right));
    }

    private static byte[] Serialize<T>(T value) where T : IPacketSerializable
    {
        PacketWriter writer = new() { WarnOnGrow = false };
        writer.Write(value);
        writer.ZeroByteRemainder();

        byte[] buffer = new byte[writer.BytePosition];
        Array.Copy(writer.Buffer, buffer, writer.BytePosition);
        return buffer;
    }
}
