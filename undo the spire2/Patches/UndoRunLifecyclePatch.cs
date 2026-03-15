// 文件说明：在战斗开始和结束时初始化或清理 undo 状态。
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Runs;

namespace UndoTheSpire2;

[HarmonyPatch]
public static class UndoRunLifecyclePatch
{
    [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.EndCombatInternal))]
    [HarmonyPrefix]
    public static void EndCombatPrefix()
    {
        MainFile.Controller.ClearHistory("combat ended");
    }

    [HarmonyPatch(typeof(RunManager), nameof(RunManager.CleanUp))]
    [HarmonyPostfix]
    public static void RunCleanupPostfix()
    {
        MainFile.Controller.ClearHistory("run cleaned up");
    }
}
