using System;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace UndoTheSpire2;

[HarmonyPatch(typeof(Creature), nameof(Creature.HealInternal))]
public static class UndoCreatureReviveUiPatch
{
    [HarmonyPrefix]
    public static void HealInternalPrefix(Creature __instance, ref bool __state)
    {
        __state = __instance.Monster != null && __instance.IsDead;
    }

    [HarmonyPostfix]
    public static void HealInternalPostfix(Creature __instance, bool __state)
    {
        if (!__state || __instance.IsDead || __instance.Monster == null || !CombatManager.Instance.IsInProgress)
            return;

        TaskHelper.RunSafely(RefreshRevivedCreatureUiAsync(__instance));
    }

    private static async Task RefreshRevivedCreatureUiAsync(Creature creature)
    {
        await WaitOneFrameAsync();

        NCombatRoom? combatRoom = NCombatRoom.Instance;
        if (combatRoom == null || creature.CombatState == null || creature.Monster == null || !creature.CombatState.ContainsCreature(creature))
            return;

        UndoSpecialCreatureVisualNormalizer.RefreshSingle(creature, combatRoom, normalizeAnimation: false);
    }

    private static async Task WaitOneFrameAsync()
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
            throw new InvalidOperationException("Main loop is not a SceneTree.");

        await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
    }
}
