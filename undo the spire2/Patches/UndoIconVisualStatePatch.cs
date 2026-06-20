using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Relics;

namespace UndoTheSpire2;

[HarmonyPatch]
internal static class UndoIconVisualStatePatch
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(NPower), nameof(NPower._Ready))]
    private static void NPowerReadyPostfix(NPower __instance)
    {
        NormalizePower(__instance, "ready");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NPower), "Reload")]
    private static void NPowerReloadPostfix(NPower __instance)
    {
        NormalizePower(__instance, "reload");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NRelic), nameof(NRelic._Ready))]
    private static void NRelicReadyPostfix(NRelic __instance)
    {
        NormalizeRelic(__instance, "ready");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NRelic), "Reload")]
    private static void NRelicReloadPostfix(NRelic __instance)
    {
        NormalizeRelic(__instance, "reload");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NRelicInventoryHolder), nameof(NRelicInventoryHolder._Ready))]
    private static void NRelicInventoryHolderReadyPostfix(NRelicInventoryHolder __instance)
    {
        NormalizeRelicHolder(__instance, "ready");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NRelicInventoryHolder), "RefreshStatus")]
    private static void NRelicInventoryHolderRefreshStatusPostfix(NRelicInventoryHolder __instance)
    {
        NormalizeRelicHolder(__instance, "refresh_status");
    }

    private static void NormalizePower(NPower power, string reason)
    {
        try
        {
            UndoController.NormalizePowerIconVisualState(power);
        }
        catch (Exception ex)
        {
            UndoDebugLog.Write($"icon_visual_patch_power_failed reason={reason} error={ex.GetType().Name}:{ex.Message}");
        }
    }

    private static void NormalizeRelic(NRelic relic, string reason)
    {
        try
        {
            UndoController.NormalizeRelicIconVisualState(relic);
        }
        catch (Exception ex)
        {
            UndoDebugLog.Write($"icon_visual_patch_relic_failed reason={reason} error={ex.GetType().Name}:{ex.Message}");
        }
    }

    private static void NormalizeRelicHolder(NRelicInventoryHolder holder, string reason)
    {
        try
        {
            UndoController.NormalizeRelicInventoryHolderVisualState(holder);
        }
        catch (Exception ex)
        {
            UndoDebugLog.Write($"icon_visual_patch_relic_holder_failed reason={reason} error={ex.GetType().Name}:{ex.Message}");
        }
    }
}
