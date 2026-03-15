// 文件说明：承载 choice restore / synthetic choice 共用的底层状态对齐辅助方法。
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace UndoTheSpire2;

public sealed partial class UndoController
{
    private static bool TryGetComparablePlayerStates(
        NetFullCombatState anchorState,
        NetFullCombatState branchState,
        out int branchPlayerIndex,
        out NetFullCombatState.PlayerState anchorPlayerState,
        out NetFullCombatState.PlayerState branchPlayerState)
    {
        branchPlayerIndex = -1;
        anchorPlayerState = default;
        branchPlayerState = default;
        if (branchState.Players.Count == 0)
            return false;

        ulong playerId = LocalContext.NetId ?? branchState.Players[0].playerId;
        if (!TryGetPlayerState(anchorState, playerId, out _, out anchorPlayerState))
            return false;
        if (!TryGetPlayerState(branchState, playerId, out branchPlayerIndex, out branchPlayerState))
            return false;

        return true;
    }

    private static bool TryGetPlayerState(
        NetFullCombatState fullState,
        ulong playerId,
        out int playerIndex,
        out NetFullCombatState.PlayerState playerState)
    {
        for (int i = 0; i < fullState.Players.Count; i++)
        {
            if (fullState.Players[i].playerId != playerId)
                continue;

            playerIndex = i;
            playerState = fullState.Players[i];
            return true;
        }

        playerIndex = -1;
        playerState = default;
        return false;
    }

    private static bool TryFindGeneratedCombatCard(
        NetFullCombatState.PlayerState anchorPlayerState,
        NetFullCombatState.PlayerState branchPlayerState,
        out int pileIndex,
        out int cardIndex,
        out NetFullCombatState.CardState generatedCardState)
    {
        pileIndex = -1;
        cardIndex = -1;
        generatedCardState = default;
        bool foundGeneratedCard = false;

        for (int i = 0; i < branchPlayerState.piles.Count; i++)
        {
            NetFullCombatState.CombatPileState branchPileState = branchPlayerState.piles[i];
            int anchorPileIndex = FindPileIndex(anchorPlayerState.piles, branchPileState.pileType);
            if (anchorPileIndex < 0)
                return false;

            NetFullCombatState.CombatPileState anchorPileState = anchorPlayerState.piles[anchorPileIndex];
            if (!TryFindExtraCardIndexes(anchorPileState.cards, branchPileState.cards, out List<int> extraCardIndexes))
                return false;

            if (extraCardIndexes.Count == 0)
                continue;

            if (extraCardIndexes.Count != 1 || foundGeneratedCard)
                return false;

            foundGeneratedCard = true;
            pileIndex = i;
            cardIndex = extraCardIndexes[0];
            generatedCardState = branchPileState.cards[cardIndex];
        }

        return foundGeneratedCard;
    }

    private static bool TryFindChooseACardTemplateSlot(
        NetFullCombatState.PlayerState anchorPlayerState,
        NetFullCombatState.PlayerState templatePlayerState,
        SerializableCard templateOptionCard,
        out int pileIndex,
        out int cardIndex,
        out NetFullCombatState.CardState generatedCardState)
    {
        pileIndex = -1;
        cardIndex = -1;
        generatedCardState = default;
        bool foundGeneratedCard = false;

        for (int i = 0; i < templatePlayerState.piles.Count; i++)
        {
            NetFullCombatState.CombatPileState templatePileState = templatePlayerState.piles[i];
            int anchorPileIndex = FindPileIndex(anchorPlayerState.piles, templatePileState.pileType);
            if (anchorPileIndex < 0)
                return false;

            NetFullCombatState.CombatPileState anchorPileState = anchorPlayerState.piles[anchorPileIndex];
            List<int> unmatchedIndexes = FindUnmatchedCardIndexes(anchorPileState.cards, templatePileState.cards);
            foreach (int unmatchedIndex in unmatchedIndexes)
            {
                SerializableCard templateCard = templatePileState.cards[unmatchedIndex].card;
                if (!templateCard.Equals(templateOptionCard))
                    continue;

                if (foundGeneratedCard)
                    return false;

                foundGeneratedCard = true;
                pileIndex = i;
                cardIndex = unmatchedIndex;
                generatedCardState = templatePileState.cards[unmatchedIndex];
            }
        }

        return foundGeneratedCard;
    }

    private static int FindPileIndex(IReadOnlyList<NetFullCombatState.CombatPileState> pileStates, PileType pileType)
    {
        for (int i = 0; i < pileStates.Count; i++)
        {
            if (pileStates[i].pileType == pileType)
                return i;
        }

        return -1;
    }

    private static bool TryFindExtraCardIndexes(
        IReadOnlyList<NetFullCombatState.CardState> baseCards,
        IReadOnlyList<NetFullCombatState.CardState> cardsWithExtra,
        out List<int> extraCardIndexes)
    {
        extraCardIndexes = [];
        if (cardsWithExtra.Count < baseCards.Count)
            return false;

        bool[] used = new bool[cardsWithExtra.Count];
        foreach (NetFullCombatState.CardState baseCard in baseCards)
        {
            int matchIndex = FindMatchingCardStateIndex(cardsWithExtra, baseCard, used);
            if (matchIndex < 0)
                return false;

            used[matchIndex] = true;
        }

        for (int i = 0; i < used.Length; i++)
        {
            if (!used[i])
                extraCardIndexes.Add(i);
        }

        return true;
    }

    private static int FindMatchingCardStateIndex(
        IReadOnlyList<NetFullCombatState.CardState> cards,
        NetFullCombatState.CardState targetCard,
        bool[] used)
    {
        for (int i = 0; i < cards.Count; i++)
        {
            if (used[i] || !PacketDataEquals(cards[i], targetCard))
                continue;

            return i;
        }

        return -1;
    }

    private static NetFullCombatState.CardState CreateChooseACardCardState(
        SerializableCard selectedOptionCard,
        NetFullCombatState.CardState templateCardState)
    {
        CardModel replacementCard = CardModel.FromSerializable(ClonePacketSerializable(selectedOptionCard));
        NetFullCombatState.CardState cardState = NetFullCombatState.CardState.From(replacementCard);
        cardState.card.Props = templateCardState.card.Props == null ? null : ClonePacketSerializable(templateCardState.card.Props);
        cardState.card.FloorAddedToDeck = templateCardState.card.FloorAddedToDeck;
        return cardState;
    }
}
