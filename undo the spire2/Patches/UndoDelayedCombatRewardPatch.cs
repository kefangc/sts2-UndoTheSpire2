using System;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Rooms;

namespace UndoTheSpire2;

[HarmonyPatch]
public static class UndoDelayedCombatRewardPatch
{
    [HarmonyPatch(typeof(TheHunt), "OnPlay")]
    [HarmonyPrefix]
    public static bool TheHuntOnPlayPrefix(TheHunt __instance, PlayerChoiceContext choiceContext, CardPlay cardPlay, ref Task? __result)
    {
        __result = ExecuteTheHuntWithDelayedRewardAsync(__instance, choiceContext, cardPlay);
        return false;
    }

    [HarmonyPatch(typeof(SwipePower), nameof(SwipePower.BeforeDeath))]
    [HarmonyPrefix]
    public static bool SwipeBeforeDeathPrefix(SwipePower __instance, Creature target, ref Task? __result)
    {
        if (__instance.Owner != target)
        {
            __result = Task.CompletedTask;
            return false;
        }

        Player? player = __instance.Target?.Player;
        CardModel? deckVersion = __instance.StolenCard?.DeckVersion;
        if (deckVersion != null && player != null)
        {
            UndoDelayedCombatRewardService.QueueSwipeReward(player, deckVersion, ModelDb.Encounter<ThievingHopperWeak>().Id);
        }

        __result = Task.CompletedTask;
        return false;
    }

    private static async Task ExecuteTheHuntWithDelayedRewardAsync(TheHunt card, PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        if (card.CombatState?.RunState.CurrentRoom is not CombatRoom combatRoom)
            return;

        Creature target = cardPlay.Target ?? throw new ArgumentNullException(nameof(cardPlay.Target));
        bool shouldTriggerFatal = target.Powers.All(static power => power.ShouldOwnerDeathTriggerFatal());
        AttackCommand attackCommand = await DamageCmd.Attack(card.DynamicVars.Damage.BaseValue)
            .FromCard(card)
            .Targeting(target)
            .WithHitFx("vfx/vfx_attack_slash", null, null)
            .Execute(choiceContext);

        if (!shouldTriggerFatal || !attackCommand.Results.Any(static result => result.WasTargetKilled))
            return;

        UndoDelayedCombatRewardService.QueueTheHuntReward(card.Owner, combatRoom.RoomType);
        await PowerCmd.Apply<TheHuntPower>(card.Owner.Creature, 1m, card.Owner.Creature, card, false);
    }
}
