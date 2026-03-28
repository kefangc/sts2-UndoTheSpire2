using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace UndoTheSpire2;

[HarmonyPatch]
public static class UndoPlayerHandPatch
{
    [HarmonyPatch(typeof(NPlayerHand), "OnSelectModeSourceFinished")]
    [HarmonyPrefix]
    public static void SelectModeSourceFinishedPrefix(NPlayerHand __instance, AbstractModel? source)
    {
        MainFile.Controller.OnOfficialHandChoiceSourceFinishing(__instance, source);
    }

    [HarmonyPatch(typeof(NPlayerHand), "OnSelectModeSourceFinished")]
    [HarmonyPostfix]
    public static void SelectModeSourceFinishedPostfix(NPlayerHand __instance, AbstractModel? source)
    {
        MainFile.Controller.OnOfficialHandChoiceSourceFinished(__instance, source);
    }

    [HarmonyPatch(typeof(NPlayerHand), nameof(NPlayerHand._ExitTree))]
    [HarmonyPrefix]
    public static void ExitTreePrefix(NPlayerHand __instance)
    {
        if (MainFile.Controller.IsRestoring)
        {
            UndoReflectionUtil.TrySetFieldValue(__instance, "_selectionCompletionSource", null);
            UndoDebugLog.Write("hand_exit_tree_selection_source_cleared");
        }
    }

    [HarmonyPatch(typeof(NPlayerHand), "AfterCardsSelected")]
    [HarmonyPrefix]
    public static bool AfterCardsSelectedPrefix(NPlayerHand __instance)
    {
        if (!GodotObject.IsInstanceValid(__instance))
            return false;

        if (!__instance.IsInsideTree())
        {
            UndoDebugLog.Write("hand_after_cards_selected_suppressed reason=not_in_tree");
            return false;
        }

        if (__instance.CurrentMode == NPlayerHand.Mode.Play && !__instance.IsInCardSelection)
        {
            UndoDebugLog.Write("hand_after_cards_selected_suppressed reason=mode_play");
            return false;
        }

        return true;
    }

    [HarmonyPatch(typeof(NPlayerHand), nameof(NPlayerHand.SelectCards))]
    [HarmonyPrefix]
    public static void SelectCardsPrefix(NPlayerHand __instance, CardSelectorPrefs prefs)
    {
        MainFile.Controller.PrepareHandSelectionUiForOpen(__instance);
    }
}
