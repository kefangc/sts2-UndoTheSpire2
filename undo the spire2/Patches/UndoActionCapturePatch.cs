// 文件说明：在官方 action 流程里挂接 choice 与快照捕获。
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;

namespace UndoTheSpire2;

[HarmonyPatch]
public static class UndoActionCapturePatch
{
    [HarmonyPatch(typeof(PlayCardAction), "ExecuteAction")]
    [HarmonyPrefix]
    public static void PlayCardPrefix(PlayCardAction __instance)
    {
        UndoPlayCardActionTracker.Track(__instance, ResolveCard(__instance));
        MainFile.Controller.TryCaptureAction(UndoActionKind.PlayCard, __instance);
    }

    [HarmonyPatch(typeof(PlayCardAction), "ExecuteAction")]
    [HarmonyPostfix]
    public static void PlayCardPostfix(PlayCardAction __instance, Task __result)
    {
        if (__result.IsCompleted)
        {
            UndoPlayCardActionTracker.Untrack(__instance);
            return;
        }

        _ = __result.ContinueWith(
            static (_, state) =>
            {
                if (state is PlayCardAction action)
                    UndoPlayCardActionTracker.Untrack(action);
            },
            __instance,
            TaskScheduler.Default);
    }

    [HarmonyPatch(typeof(UsePotionAction), "ExecuteAction")]
    [HarmonyPrefix]
    public static void UsePotionPrefix(UsePotionAction __instance)
    {
        MainFile.Controller.TryCaptureAction(UndoActionKind.UsePotion, __instance);
    }

    [HarmonyPatch(typeof(DiscardPotionGameAction), "ExecuteAction")]
    [HarmonyPrefix]
    public static void DiscardPotionPrefix(DiscardPotionGameAction __instance)
    {
        MainFile.Controller.TryCaptureAction(UndoActionKind.DiscardPotion, __instance);
    }

    [HarmonyPatch(typeof(EndPlayerTurnAction), "ExecuteAction")]
    [HarmonyPrefix]
    public static void EndTurnPrefix(EndPlayerTurnAction __instance)
    {
        MainFile.Controller.TryCaptureAction(UndoActionKind.EndTurn, __instance);
    }

    [HarmonyPatch(typeof(GameAction), nameof(GameAction.PauseForPlayerChoice))]
    [HarmonyPostfix]
    public static void PauseForPlayerChoicePostfix(GameAction __instance)
    {
        MainFile.Controller.TryCapturePlayerChoice(__instance);
    }

    private static CardModel? ResolveCard(PlayCardAction action)
    {
        if (UndoReflectionUtil.TryGetFieldValue(action, "_card", out CardModel? card) && card != null)
            return card;

        return action.NetCombatCard.ToCardModelOrNull();
    }
}

