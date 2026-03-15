// 文件说明：在战斗 UI 生命周期中挂接 undo HUD。
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace UndoTheSpire2;

[HarmonyPatch]
public static class UndoCombatUiPatch
{
    [HarmonyPatch(typeof(NCombatUi), nameof(NCombatUi._Ready))]
    [HarmonyPostfix]
    public static void CombatUiReadyPostfix(NCombatUi __instance)
    {
        Node hudParent = (Node?)NRun.Instance?.GlobalUi ?? __instance;
        UndoHud? hud = hudParent.GetNodeOrNull<UndoHud>("UndoHud") ?? __instance.GetNodeOrNull<UndoHud>("UndoHud");
        if (hud == null)
        {
            hud = new UndoHud();
            hudParent.AddChildSafely(hud);
        }

        hud.Bind(__instance);
    }

    [HarmonyPatch(typeof(NCombatUi), nameof(NCombatUi.Activate))]
    [HarmonyPostfix]
    public static void CombatUiActivatePostfix(NCombatUi __instance, CombatState state)
    {
        MainFile.Controller.OnCombatUiActivated(__instance, state);
    }

    [HarmonyPatch(typeof(NCombatUi), nameof(NCombatUi.Deactivate))]
    [HarmonyPostfix]
    public static void CombatUiDeactivatePostfix(NCombatUi __instance)
    {
        MainFile.Controller.OnCombatUiDeactivated(__instance);
    }

    [HarmonyPatch(typeof(NGame), nameof(NGame._Input))]
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    public static bool GameInputPrefix(NGame __instance, Godot.InputEvent inputEvent)
    {
        NCombatUi? combatUi = NCombatRoom.Instance?.Ui;
        return combatUi == null || !MainFile.Controller.TryHandleHotkey(combatUi, inputEvent);
    }
}
