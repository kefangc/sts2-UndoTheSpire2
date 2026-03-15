// 文件说明：在官方 action 流程里挂接 choice 与快照捕获。
using HarmonyLib;
using MegaCrit.Sts2.Core.GameActions;

namespace UndoTheSpire2;

[HarmonyPatch]
public static class UndoActionCapturePatch
{
    [HarmonyPatch(typeof(PlayCardAction), "ExecuteAction")]
    [HarmonyPrefix]
    public static void PlayCardPrefix(PlayCardAction __instance)
    {
        MainFile.Controller.TryCaptureAction(UndoActionKind.PlayCard, __instance);
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
}

