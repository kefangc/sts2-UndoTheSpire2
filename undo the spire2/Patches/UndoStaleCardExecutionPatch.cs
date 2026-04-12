using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Commands.Builders;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

namespace UndoTheSpire2;

[HarmonyPatch]
internal static class UndoStaleCardExecutionPatch
{
    [HarmonyPatch(typeof(AttackCommand), nameof(AttackCommand.Execute))]
    [HarmonyPrefix]
    private static void WrapBeforeDamage(AttackCommand __instance, PlayerChoiceContext? choiceContext)
    {
        if (__instance.ModelSource is not CardModel sourceCard)
            return;

        if (!UndoReflectionUtil.TryGetFieldValue(__instance, "_beforeDamage", out Func<Task>? beforeDamage) || beforeDamage == null)
            return;

        UndoReflectionUtil.TrySetFieldValue(
            __instance,
            "_beforeDamage",
            () =>
            {
                if (UndoLiveCardExecutionGuard.ShouldAbortExecution(choiceContext, sourceCard, out string reason))
                {
                    UndoDebugLog.Write($"stale_card_before_damage_skipped card={sourceCard.Id.Entry} reason={reason}");
                    return Task.CompletedTask;
                }

                return beforeDamage();
            });
    }

    [HarmonyPatch(
        typeof(CreatureCmd),
        nameof(CreatureCmd.Damage),
        [
            typeof(PlayerChoiceContext),
            typeof(IEnumerable<Creature>),
            typeof(decimal),
            typeof(ValueProp),
            typeof(Creature),
            typeof(CardModel)
        ])]
    [HarmonyPrefix]
    private static bool GuardStaleCardDamage(
        PlayerChoiceContext? choiceContext,
        CardModel? cardSource,
        ref Task<IEnumerable<DamageResult>> __result)
    {
        if (!UndoLiveCardExecutionGuard.ShouldAbortExecution(choiceContext, cardSource, out string reason))
            return true;

        if (cardSource != null)
            UndoDebugLog.Write($"stale_card_damage_skipped card={cardSource.Id.Entry} reason={reason}");

        __result = Task.FromResult<IEnumerable<DamageResult>>(Enumerable.Empty<DamageResult>());
        return false;
    }
}
