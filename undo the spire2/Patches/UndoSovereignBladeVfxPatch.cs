using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Nodes.Vfx;

namespace UndoTheSpire2;

[HarmonyPatch(typeof(NSovereignBladeVfx), nameof(NSovereignBladeVfx.Attack))]
internal static class UndoSovereignBladeVfxPatch
{
    [HarmonyPrefix]
    private static bool OnAttack(NSovereignBladeVfx __instance)
    {
        if (__instance.Card is not SovereignBlade blade)
            return true;

        if (MainFile.Controller.IsRestoring)
        {
            UndoDebugLog.Write($"stale_sovereign_blade_attack_skipped card={blade.Id.Entry} reason=controller_restoring");
            return false;
        }

        if (UndoPlayCardActionTracker.GetTrackedAction(blade) is { } trackedAction
            && trackedAction.State == MegaCrit.Sts2.Core.Entities.Actions.GameActionState.Canceled)
        {
            UndoDebugLog.Write(
                $"stale_sovereign_blade_attack_skipped card={blade.Id.Entry} reason=tracked_action_canceled actionId={trackedAction.Id?.ToString() ?? "null"}");
            return false;
        }

        if (UndoLiveCardExecutionGuard.IsLiveCombatCard(blade))
            return true;

        UndoDebugLog.Write($"stale_sovereign_blade_attack_skipped card={blade.Id.Entry} reason=stale_card");
        return false;
    }
}
