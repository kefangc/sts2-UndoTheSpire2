using System.Collections.Concurrent;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;

namespace UndoTheSpire2;

internal static class UndoPlayCardActionTracker
{
    private static readonly ConcurrentDictionary<CardModel, PlayCardAction> CardToAction = new(ReferenceEqualityComparer.Instance);
    private static readonly ConcurrentDictionary<PlayCardAction, CardModel> ActionToCard = new();

    internal static void Track(PlayCardAction action, CardModel? card)
    {
        if (card == null)
            return;

        ActionToCard[action] = card;
        CardToAction[card] = action;
    }

    internal static void Untrack(PlayCardAction action)
    {
        if (!ActionToCard.TryRemove(action, out CardModel? card))
            return;

        CardToAction.TryRemove(card, out _);
    }

    internal static PlayCardAction? GetTrackedAction(CardModel? card)
    {
        if (card == null)
            return null;

        return CardToAction.TryGetValue(card, out PlayCardAction? action) ? action : null;
    }
}
