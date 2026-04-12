using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace UndoTheSpire2;

internal static class UndoLiveCardExecutionGuard
{
    internal static bool IsLiveCombatCard(CardModel? card)
    {
        if (card == null || card.HasBeenRemovedFromState || card.Owner == null)
            return false;

        CombatState? combatState = card.CombatState ?? card.Owner.Creature.CombatState;
        return combatState != null && combatState.ContainsCard(card);
    }

    internal static bool ShouldAbortExecution(PlayerChoiceContext? choiceContext, CardModel? card, out string reason)
    {
        if (choiceContext is GameActionPlayerChoiceContext actionContext
            && actionContext.Action.State == GameActionState.Canceled)
        {
            reason = "canceled_action";
            return true;
        }

        if (!IsLiveCombatCard(card))
        {
            reason = "stale_card";
            return true;
        }

        reason = "none";
        return false;
    }
}
