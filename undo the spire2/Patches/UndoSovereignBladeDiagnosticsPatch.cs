using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Runs;

namespace UndoTheSpire2;

[HarmonyPatch]
internal static class UndoSovereignBladeDiagnosticsPatch
{
    [HarmonyPatch(typeof(ForgeCmd), nameof(ForgeCmd.PlayCombatRoomForgeVfx))]
    [HarmonyPrefix]
    private static void ForgeVfxPrefix(CardModel card)
    {
        if (card is not SovereignBlade blade)
            return;

        UndoDebugLog.Write(
            $"sovereign_blade_forge_vfx card={blade.Id.Entry} pile={blade.Pile?.Type.ToString() ?? "null"} createdThroughForge={blade.CreatedThroughForge} restoring={MainFile.Controller.IsRestoring}");
    }

    [HarmonyPatch(typeof(SovereignBlade), nameof(SovereignBlade.AfterCardChangedPiles))]
    [HarmonyPrefix]
    private static void AfterCardChangedPilesPrefix(SovereignBlade __instance, CardModel card, PileType oldPileType, AbstractModel? source)
    {
        if (!ReferenceEquals(__instance, card))
            return;

        UndoDebugLog.Write(
            $"sovereign_blade_after_card_changed_piles card={__instance.Id.Entry} oldPile={oldPileType} newPile={__instance.Pile?.Type.ToString() ?? "null"} createdThroughForge={__instance.CreatedThroughForge} source={source?.GetType().Name ?? "null"} restoring={MainFile.Controller.IsRestoring}");
    }

    [HarmonyPatch(typeof(NSovereignBladeVfx), nameof(NSovereignBladeVfx.Forge))]
    [HarmonyPrefix]
    private static void ForgePrefix(NSovereignBladeVfx __instance, float bladeDamage, bool showFlames)
    {
        if (__instance.Card is not SovereignBlade blade)
            return;

        UndoDebugLog.Write(
            $"sovereign_blade_vfx_forge card={blade.Id.Entry} pile={blade.Pile?.Type.ToString() ?? "null"} damage={bladeDamage} showFlames={showFlames} restoring={MainFile.Controller.IsRestoring}");
    }

    [HarmonyPatch(typeof(NSovereignBladeVfx), nameof(NSovereignBladeVfx.Attack))]
    [HarmonyPostfix]
    private static void AttackPostfix(NSovereignBladeVfx __instance)
    {
        if (__instance.Card is not SovereignBlade blade)
            return;

        string currentAction = RunManager.Instance.ActionExecutor.CurrentlyRunningAction?.GetType().Name ?? "null";
        UndoDebugLog.Write(
            $"sovereign_blade_vfx_attack card={blade.Id.Entry} pile={blade.Pile?.Type.ToString() ?? "null"} currentAction={currentAction} restoring={MainFile.Controller.IsRestoring}");
    }

    [HarmonyPatch(typeof(NSovereignBladeVfx), "CleanupAttack")]
    [HarmonyPostfix]
    private static void CleanupAttackPostfix(NSovereignBladeVfx __instance)
    {
        if (__instance.Card is not SovereignBlade blade)
            return;

        UndoDebugLog.Write(
            $"sovereign_blade_vfx_cleanup_attack card={blade.Id.Entry} pile={blade.Pile?.Type.ToString() ?? "null"} restoring={MainFile.Controller.IsRestoring}");
    }
}
