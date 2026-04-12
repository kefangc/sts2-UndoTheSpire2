// 文件说明：描述一次可恢复 choice 的选项来源与结果映射规则。
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
        IReadOnlyList<UndoCardCostState> optionCardCostStates,
        IReadOnlyList<UndoCardRuntimeState> optionCardRuntimeStates,
        PileType? sourcePileType,
        IReadOnlyList<int> sourcePileOptionIndexes,
        IReadOnlyList<NetCombatCard> sourcePileCombatCards,
        NetCombatCard? sourceCombatCard,
        bool canSkip,
        string? sourceModelTypeName,
        string? sourceModelId)
    {
        Kind = kind;
        SelectionPrefs = selectionPrefs;
        OptionCards = optionCards;
        OptionCardCostStates = optionCardCostStates;
        OptionCardRuntimeStates = optionCardRuntimeStates;
        SourcePileType = sourcePileType;
        SourcePileOptionIndexes = sourcePileOptionIndexes;
        SourcePileCombatCards = sourcePileCombatCards;
        SourceCombatCard = sourceCombatCard;
        CanSkip = canSkip;
        SourceModelTypeName = sourceModelTypeName;
        SourceModelId = sourceModelId;
    }

    public UndoChoiceKind Kind { get; }

    public CardSelectorPrefs SelectionPrefs { get; }

    public IReadOnlyList<SerializableCard> OptionCards { get; }

    public IReadOnlyList<UndoCardCostState> OptionCardCostStates { get; }

    public IReadOnlyList<UndoCardRuntimeState> OptionCardRuntimeStates { get; }

    public PileType? SourcePileType { get; }

    public IReadOnlyList<int> SourcePileOptionIndexes { get; }

    public IReadOnlyList<NetCombatCard> SourcePileCombatCards { get; }

    public NetCombatCard? SourceCombatCard { get; }

    public bool CanSkip { get; }

    public string? SourceModelTypeName { get; }

    public string? SourceModelId { get; }

    public static UndoChoiceSpec CreateChooseACard(IReadOnlyList<CardModel> cards, bool canSkip, AbstractModel? source = null)
    {
        return new UndoChoiceSpec(
            UndoChoiceKind.ChooseACard,
            default,
            [.. cards.Select(static card => card.ToSerializable())],
            [.. cards.Select(UndoController.CaptureChoiceOptionCostState)],
            [.. cards.Select(UndoController.CaptureChoiceOptionRuntimeState)],
            null,
            [],
            [],
            source is CardModel sourceCard ? NetCombatCard.FromModel(sourceCard) : null,
            canSkip,
            source?.GetType().FullName,
            source?.Id.Entry);
    }

    public static UndoChoiceSpec CreateHandSelection(Player player, CardSelectorPrefs prefs, Func<CardModel, bool>? filter, AbstractModel? source = null)
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
            [],
            [],
            PileType.Hand,
            eligibleIndexes,
            eligibleCombatCards,
            source is CardModel sourceCard ? NetCombatCard.FromModel(sourceCard) : null,
            prefs.MinSelect == 0,
            source?.GetType().FullName,
            source?.Id.Entry);
    }

    public static UndoChoiceSpec CreateSimpleGridSelection(Player player, IReadOnlyList<CardModel> cards, CardSelectorPrefs prefs, AbstractModel? source = null)
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
            [.. cards.Select(UndoController.CaptureChoiceOptionCostState)],
            [.. cards.Select(UndoController.CaptureChoiceOptionRuntimeState)],
            sourcePileType,
            sourcePileIndexes,
            sourcePileCombatCards,
            source is CardModel sourceCard ? NetCombatCard.FromModel(sourceCard) : null,
            prefs.MinSelect == 0,
            source?.GetType().FullName,
            source?.Id.Entry);
    }

    public bool SupportsSyntheticRestore => Kind is UndoChoiceKind.ChooseACard or UndoChoiceKind.HandSelection or UndoChoiceKind.SimpleGridSelection;

    public Func<CardModel, bool> BuildHandFilter(Player player)
    {
        HashSet<NetCombatCard> eligibleCombatCards = [.. SourcePileCombatCards];
        if (eligibleCombatCards.Count > 0)
        {
            // 手牌在多次 undo 后位置可能变化，优先按 NetCombatCard 身份匹配，避免 index 漂移。
            return card => eligibleCombatCards.Contains(NetCombatCard.FromModel(card));
        }

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
        if (Kind == UndoChoiceKind.SimpleGridSelection
            && SourcePileType is PileType sourcePileType
            && TryBuildLiveSourcePileOptionCards(player, sourcePileType, out List<CardModel>? liveOptions))
        {
            return liveOptions;
        }

        List<CardModel> options = [];
        for (int i = 0; i < OptionCards.Count; i++)
        {
            SerializableCard optionCard = OptionCards[i];
            CardModel card = CardModel.FromSerializable(optionCard);
            if (card.Owner == null)
                card.Owner = player;

            UndoCardCostState? optionCostState = i < OptionCardCostStates.Count ? OptionCardCostStates[i] : null;
            UndoCardRuntimeState? optionRuntimeState = i < OptionCardRuntimeStates.Count ? OptionCardRuntimeStates[i] : null;
            UndoController.RestoreChoiceOptionState(player, card, optionCostState, optionRuntimeState);
            options.Add(card);
        }

        return options;
    }

    public IReadOnlyList<CardModel> BuildDisplayedOptionCards(Player player)
    {
        return BuildOptionCards(player);
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

    public UndoChoiceResultKey? TryMapDisplayedOptionSelection(IReadOnlyList<CardModel> displayedOptions, IEnumerable<CardModel> selectedCards)
    {
        List<CardModel> cards = [.. selectedCards];
        if (cards.Count == 0)
        {
            if (!CanSkip)
                return null;

            return Kind == UndoChoiceKind.ChooseACard ? new UndoChoiceResultKey([-1]) : new UndoChoiceResultKey([]);
        }

        // 对重新打开的 UI，分支键必须按“当前屏幕展示顺序”计算，不能回退到旧 pile index。
        List<int> mappedIndexes = [];
        foreach (CardModel card in cards)
        {
            int mappedIndex = IndexOfReference(displayedOptions, card);
            if (mappedIndex < 0)
                return null;

            mappedIndexes.Add(mappedIndex);
        }

        return new UndoChoiceResultKey(mappedIndexes);
    }

    public UndoChoiceResultKey? TryMapDisplayedSimpleGridSelection(IReadOnlyList<CardModel> displayedOptions, IEnumerable<CardModel> selectedCards)
    {
        List<CardModel> cards = [.. selectedCards];
        if (cards.Count == 0)
        {
            if (!CanSkip)
                return null;

            return new UndoChoiceResultKey([]);
        }

        if (SourcePileCombatCards.Count > 0)
        {
            List<int> mappedIndexes = [];
            foreach (CardModel card in cards)
            {
                int mappedIndex = IndexOfValue(SourcePileCombatCards, NetCombatCard.FromModel(card));
                if (mappedIndex < 0)
                    return null;

                mappedIndexes.Add(mappedIndex);
            }

            return new UndoChoiceResultKey(mappedIndexes);
        }

        UndoChoiceResultKey? optionCardKey = TryMapOptionCardSelection(cards, useNegativeSkipIndex: false);
        if (optionCardKey != null)
            return optionCardKey;

        return TryMapDisplayedOptionSelection(displayedOptions, cards);
    }

    private UndoChoiceResultKey? TryMapIndexResult(List<int>? indexes)
    {
        if (indexes == null)
            return null;

        return new UndoChoiceResultKey(indexes);
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

        return new UndoChoiceResultKey(mappedIndexes);
    }

    private UndoChoiceResultKey? TryMapHandSelection(Player player, IEnumerable<CardModel> selectedCards)
    {
        List<CardModel> cards = [.. selectedCards];
        if (cards.Count == 0)
            return CanSkip ? new UndoChoiceResultKey([]) : null;

        List<int> mappedIndexes = [];
        if (SourcePileCombatCards.Count > 0)
        {
            foreach (CardModel card in cards)
            {
                int optionIndex = IndexOfValue(SourcePileCombatCards, NetCombatCard.FromModel(card));
                if (optionIndex < 0)
                    return null;

                mappedIndexes.Add(optionIndex);
            }

            return new UndoChoiceResultKey(mappedIndexes);
        }

        IReadOnlyList<CardModel> handCards = PileType.Hand.GetPile(player).Cards;
        foreach (CardModel card in cards)
        {
            int handIndex = IndexOfReference(handCards, card);
            if (handIndex < 0)
                return null;

            int optionIndex = IndexOfValue(SourcePileOptionIndexes, handIndex);
            if (optionIndex < 0)
                return null;

            mappedIndexes.Add(optionIndex);
        }

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

    private bool TryBuildLiveSourcePileOptionCards(Player player, PileType sourcePileType, out List<CardModel>? options)
    {
        options = null;
        IReadOnlyList<CardModel> sourceCards = sourcePileType.GetPile(player).Cards;
        if (SourcePileCombatCards.Count > 0)
        {
            List<CardModel> liveOptions = [];
            bool[] usedSourceIndexes = new bool[sourceCards.Count];
            foreach (NetCombatCard combatCard in SourcePileCombatCards)
            {
                int matchedIndex = -1;
                for (int i = 0; i < sourceCards.Count; i++)
                {
                    if (usedSourceIndexes[i] || !combatCard.Equals(NetCombatCard.FromModel(sourceCards[i])))
                        continue;

                    matchedIndex = i;
                    usedSourceIndexes[i] = true;
                    break;
                }

                if (matchedIndex < 0)
                    return false;

                liveOptions.Add(sourceCards[matchedIndex]);
            }

            options = liveOptions;
            return true;
        }

        if (SourcePileOptionIndexes.Count == 0)
            return false;

        List<CardModel> indexedOptions = [];
        foreach (int sourceIndex in SourcePileOptionIndexes)
        {
            if (sourceIndex < 0 || sourceIndex >= sourceCards.Count)
                return false;

            indexedOptions.Add(sourceCards[sourceIndex]);
        }

        options = indexedOptions;
        return true;
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

