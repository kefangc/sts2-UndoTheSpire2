// 文件说明：挂接官方战斗历史，补充 undo 所需事件。
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Powers;

namespace UndoTheSpire2;

[HarmonyPatch]
internal static class UndoCombatHistoryPatch
{
    [HarmonyPatch(typeof(CombatHistory), nameof(CombatHistory.CardPlayStarted))]
    [HarmonyPostfix]
    private static void OnCardPlayStarted(CombatState combatState, CardPlay cardPlay)
    {
        if (combatState == null || cardPlay?.Card == null)
            return;

        MainFile.Controller.OnCombatHistoryCardPlayStarted(combatState, cardPlay);
    }

    [HarmonyPatch(typeof(CombatHistory), nameof(CombatHistory.CardPlayFinished))]
    [HarmonyPostfix]
    private static void OnCardPlayFinished(CombatState combatState, CardPlay cardPlay)
    {
        if (combatState == null || cardPlay?.Card == null)
            return;

        MainFile.Controller.OnCombatHistoryCardPlayFinished(combatState, cardPlay);
    }

    [HarmonyPatch(typeof(EchoFormPower), nameof(EchoFormPower.ModifyCardPlayCount))]
    [HarmonyPrefix]
    private static bool TryOverrideEchoForm(
        EchoFormPower __instance,
        CardModel card,
        Creature? target,
        int playCount,
        ref int __result)
    {
        if (!MainFile.Controller.TryOverrideEchoFormModifyCardPlayCount(__instance, card, playCount, out int result))
            return true;

        __result = result;
        return false;
    }
}


