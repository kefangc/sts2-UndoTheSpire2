using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace UndoTheSpire2;

internal sealed class UndoChoiceSpec
{
    private UndoChoiceSpec(
        UndoChoiceKind kind,
        CardSelectorPrefs handPrefs,
        IReadOnlyList<SerializableCard> optionCards,
        IReadOnlyList<int> handOptionIndexes,
        IReadOnlyList<NetCombatCard> handOptionCombatCards,
        bool canSkip)
    {
        Kind = kind;
        HandPrefs = handPrefs;
        OptionCards = optionCards;
        HandOptionIndexes = handOptionIndexes;
        HandOptionCombatCards = handOptionCombatCards;
        CanSkip = canSkip;
    }

    public UndoChoiceKind Kind { get; }

    public CardSelectorPrefs HandPrefs { get; }

    public IReadOnlyList<SerializableCard> OptionCards { get; }

    public IReadOnlyList<int> HandOptionIndexes { get; }

    public IReadOnlyList<NetCombatCard> HandOptionCombatCards { get; }

    public bool CanSkip { get; }

    public static UndoChoiceSpec CreateChooseACard(IReadOnlyList<CardModel> cards, bool canSkip)
    {
        return new UndoChoiceSpec(
            UndoChoiceKind.ChooseACard,
            default,
            [.. cards.Select(static card => card.ToSerializable())],
            [],
            [],
            canSkip);
    }

    public static UndoChoiceSpec CreateHandSelection(Player player, CardSelectorPrefs prefs, Func<CardModel, bool>? filter)
    {
        IReadOnlyList<CardModel> handCards = PileType.Hand.GetPile(player).Cards;
        List<int> eligibleHandIndexes = [];
        List<NetCombatCard> eligibleCombatCards = [];
        for (int i = 0; i < handCards.Count; i++)
        {
            CardModel card = handCards[i];
            if (filter != null && !filter(card))
                continue;

            eligibleHandIndexes.Add(i);
            eligibleCombatCards.Add(NetCombatCard.FromModel(card));
        }

        return new UndoChoiceSpec(
            UndoChoiceKind.HandSelection,
            prefs,
            [],
            eligibleHandIndexes,
            eligibleCombatCards,
            false);
    }

    public bool SupportsSyntheticRestore => Kind is UndoChoiceKind.ChooseACard or UndoChoiceKind.HandSelection;

    public Func<CardModel, bool> BuildHandFilter(Player player)
    {
        HashSet<int> eligibleIndexes = [.. HandOptionIndexes];
        return card =>
        {
            IReadOnlyList<CardModel> handCards = PileType.Hand.GetPile(player).Cards;
            int index = IndexOfReference(handCards, card);
            return index >= 0 && eligibleIndexes.Contains(index);
        };
    }

    public IReadOnlyList<CardModel> BuildChooseACardOptions(Player player)
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
            UndoChoiceKind.HandSelection => TryMapCombatCardResult(result.combatCards),
            _ => null
        };
    }

    public UndoChoiceResultKey? TryMapSyntheticSelection(Player player, IEnumerable<CardModel> selectedCards)
    {
        return Kind switch
        {
            UndoChoiceKind.ChooseACard => TryMapChooseACardSelection(selectedCards),
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
            int mappedIndex = IndexOfValue(HandOptionCombatCards, combatCard);
            if (mappedIndex < 0)
                return null;

            mappedIndexes.Add(mappedIndex);
        }

        mappedIndexes.Sort();
        return new UndoChoiceResultKey(mappedIndexes);
    }

    private UndoChoiceResultKey? TryMapChooseACardSelection(IEnumerable<CardModel> selectedCards)
    {
        List<CardModel> cards = [.. selectedCards];
        if (cards.Count == 0)
            return CanSkip ? new UndoChoiceResultKey([-1]) : null;

        bool[] used = new bool[OptionCards.Count];
        List<int> mappedIndexes = [];
        foreach (CardModel card in cards)
        {
            SerializableCard serializableCard = card.ToSerializable();
            int mappedIndex = -1;
            for (int i = 0; i < OptionCards.Count; i++)
            {
                if (used[i] || !OptionCards[i].Equals(serializableCard))
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

            int optionIndex = IndexOfValue(HandOptionIndexes, handIndex);
            if (optionIndex < 0)
                return null;

            mappedIndexes.Add(optionIndex);
        }

        mappedIndexes.Sort();
        return new UndoChoiceResultKey(mappedIndexes);
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
}

