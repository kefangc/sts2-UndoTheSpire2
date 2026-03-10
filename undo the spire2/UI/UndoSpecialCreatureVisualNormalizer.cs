using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using PaelsLegionMonster = MegaCrit.Sts2.Core.Models.Monsters.PaelsLegion;
using PaelsLegionRelic = MegaCrit.Sts2.Core.Models.Relics.PaelsLegion;

namespace UndoTheSpire2;

// Restores creature presentation that depends on runtime state outside
// NetFullCombatState. This layer owns visuals only, not topology or model state.
internal static class UndoSpecialCreatureVisualNormalizer
{
    internal sealed class PaelsLegionVisualExpectation
    {
        public required string Trigger { get; init; }

        public required IReadOnlyList<string> AcceptableAnimationNames { get; init; }
    }

    public static void Refresh(CombatState combatState, NCombatRoom combatRoom)
    {
        foreach (Creature creature in combatState.Creatures)
        {
            if (ShouldWarmCreatureVisualScene(creature))
                WarmCreatureVisualScene(creature);
        }

        foreach (Creature creature in combatState.Allies)
        {
            if (creature.Monster is PaelsLegionMonster)
                RefreshPaelsLegionVisual(creature, combatRoom);
        }

        foreach (Creature creature in combatState.Creatures)
            RefreshCreatureStatusVisual(creature, combatRoom);
    }

    public static bool TryGetPaelsLegionExpectation(Creature creature, out PaelsLegionVisualExpectation? expectation)
    {
        bool matched = TryGetPaelsLegionExpectation(creature, out PaelsLegionVisualExpectation? resolvedExpectation, out _);
        expectation = resolvedExpectation;
        return matched;
    }

    private static void RefreshPaelsLegionVisual(Creature creature, NCombatRoom combatRoom)
    {
        if (!TryGetPaelsLegionExpectation(creature, out PaelsLegionVisualExpectation? expectation, out _))
            return;

        NCreature? creatureNode = combatRoom.GetCreatureNode(creature);
        if (creatureNode == null)
            return;

        NormalizeCreatureNodeVisibility(creature, creatureNode);
        creatureNode.Visuals.SetUpSkin(creature.Monster);
        creatureNode.SetAnimationTrigger(expectation.Trigger);
    }

    private static void RefreshCreatureStatusVisual(Creature creature, NCombatRoom combatRoom)
    {
        MonsterModel? monster = creature.Monster;
        if (monster == null)
            return;

        NCreature? creatureNode = combatRoom.GetCreatureNode(creature);
        if (creatureNode == null)
            return;

        NormalizeCreatureNodeVisibility(creature, creatureNode);

        switch (monster)
        {
            case SlumberingBeetle slumberingBeetle:
                NormalizeSleepingBeetle(slumberingBeetle, creatureNode);
                break;
            case LagavulinMatriarch lagavulinMatriarch:
                NormalizeLagavulin(lagavulinMatriarch, creatureNode);
                break;
            case BowlbugRock bowlbugRock:
                NormalizeBowlbugRock(bowlbugRock, creatureNode);
                break;
            case ThievingHopper thievingHopper:
                NormalizeThievingHopper(thievingHopper, creatureNode);
                break;
            case FatGremlin fatGremlin:
                NormalizeGremlinAwakeState(creatureNode, ReadBoolMonsterProperty(fatGremlin, "IsAwake"));
                break;
            case SneakyGremlin sneakyGremlin:
                NormalizeGremlinAwakeState(creatureNode, ReadBoolMonsterProperty(sneakyGremlin, "IsAwake"));
                break;
            case CeremonialBeast ceremonialBeast:
                NormalizeCeremonialBeast(ceremonialBeast, creatureNode);
                break;
        }
    }

    private static void NormalizeSleepingBeetle(SlumberingBeetle monster, NCreature creatureNode)
    {
        if (!monster.IsAwake)
        {
            EnsureSleepingVfx(monster, creatureNode);
            EnsureBaseAnimation(creatureNode, "sleep_loop", loop: true);
            return;
        }

        ClearSleepingVfx(monster);
        EnsureIdleState(creatureNode);
    }

    private static void NormalizeLagavulin(LagavulinMatriarch monster, NCreature creatureNode)
    {
        bool asleep = !monster.IsAwake || monster.Creature.HasPower<AsleepPower>();
        if (asleep)
        {
            EnsureSleepingVfx(monster, creatureNode);
            creatureNode.SetAnimationTrigger("Sleep");
            EnsureBaseAnimation(creatureNode, "sleep_loop", loop: true);
            SetLagavulinEyes(creatureNode, "_tracks/eyes_closed_loop", addLoop: false);
            return;
        }

        ClearSleepingVfx(monster);
        EnsureIdleState(creatureNode);
        if (monster.Creature.CurrentHp <= monster.Creature.MaxHp / 2)
            SetLagavulinEyes(creatureNode, "_tracks/eyes_open", addLoop: true);
        else
            SetLagavulinEyes(creatureNode, "_tracks/eyes_closed_loop", addLoop: false);
    }

    private static void NormalizeBowlbugRock(BowlbugRock monster, NCreature creatureNode)
    {
        if (monster.IsOffBalance)
        {
            creatureNode.SetAnimationTrigger("Stun");
            EnsureBaseAnimation(creatureNode, "stunned_loop", loop: true);
            return;
        }

        EnsureIdleState(creatureNode);
    }

    private static void NormalizeThievingHopper(ThievingHopper monster, NCreature creatureNode)
    {
        bool isHovering = ReadBoolMonsterProperty(monster, "IsHovering");
        if (isHovering)
        {
            creatureNode.SetAnimationTrigger("Hover");
            EnsureBaseAnimation(creatureNode, "hover_loop", loop: true);
            return;
        }

        creatureNode.SetAnimationTrigger("StunTrigger");
        EnsureBaseAnimation(creatureNode, "idle_loop", loop: true);
    }

    private static void NormalizeGremlinAwakeState(NCreature creatureNode, bool isAwake)
    {
        if (isAwake)
        {
            creatureNode.SetAnimationTrigger("Idle");
            EnsureBaseAnimation(creatureNode, "awake_loop", loop: true);
            return;
        }

        EnsureBaseAnimation(creatureNode, "stunned_loop", loop: true);
    }

    private static void NormalizeCeremonialBeast(CeremonialBeast monster, NCreature creatureNode)
    {
        bool inMidCharge = ReadBoolMonsterProperty(monster, "InMidCharge");
        bool isStunned = ReadBoolMonsterProperty(monster, "IsStunnedByPlowRemoval");
        if (inMidCharge)
        {
            creatureNode.SetAnimationTrigger("Plow");
            return;
        }

        if (isStunned)
        {
            creatureNode.SetAnimationTrigger("Stun");
            EnsureBaseAnimation(creatureNode, "stun_loop", loop: true);
            return;
        }

        EnsureIdleState(creatureNode);
    }

    private static void NormalizeCreatureNodeVisibility(Creature creature, NCreature creatureNode)
    {
        creatureNode.Visible = true;
        creatureNode.Modulate = Colors.White;
        if (creatureNode.Visuals != null)
        {
            creatureNode.Visuals.Visible = true;
            creatureNode.Visuals.Modulate = Colors.White;
        }

        if (creatureNode.Body != null)
        {
            creatureNode.Body.Visible = true;
            creatureNode.Body.Modulate = Colors.White;
        }

        bool interactable = creature.Monster?.IsHealthBarVisible == true && !creature.IsDead;
        creatureNode.ToggleIsInteractable(interactable);
    }

    private static void EnsureIdleState(NCreature creatureNode)
    {
        if (TryInvokePrivateMethod(creatureNode, "ImmediatelySetIdle"))
            return;

        EnsureBaseAnimation(creatureNode, "idle_loop", loop: true);
    }

    private static bool TryInvokePrivateMethod(object target, string name)
    {
        try
        {
            var method = UndoReflectionUtil.FindMethod(target.GetType(), name);
            if (method == null)
                return false;

            method.Invoke(target, null);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void SetLagavulinEyes(NCreature creatureNode, string animationName, bool addLoop)
    {
        var animationState = creatureNode.Visuals.SpineBody.GetAnimationState();
        animationState.SetAnimation(animationName, addLoop ? false : true, 1);
        if (addLoop)
            animationState.AddAnimation("_tracks/eyes_open_loop", 0f, true, 1);
    }

    private static void EnsureBaseAnimation(NCreature creatureNode, string animationName, bool loop)
    {
        string? currentAnimation = creatureNode.SpineController.GetAnimationState().GetCurrent(0)?.GetAnimation()?.GetName();
        if (string.Equals(currentAnimation, animationName, StringComparison.Ordinal))
            return;

        creatureNode.SpineController.GetAnimationState().SetAnimation(animationName, loop, 0);
    }

    private static void EnsureSleepingVfx(object monster, NCreature creatureNode)
    {
        if (GetSleepingVfx(monster) != null)
            return;

        Marker2D? marker = creatureNode.GetSpecialNode<Marker2D>("%SleepVfxPos");
        if (marker == null)
            return;

        NSleepingVfx sleepingVfx = NSleepingVfx.Create(marker.GlobalPosition, true);
        marker.AddChild(sleepingVfx);
        sleepingVfx.Position = Vector2.Zero;
        if (!UndoReflectionUtil.TrySetPropertyValue(monster, "SleepingVfx", sleepingVfx))
            UndoReflectionUtil.TrySetFieldValue(monster, "_sleepingVfx", sleepingVfx);
    }

    private static void ClearSleepingVfx(object monster)
    {
        NSleepingVfx? sleepingVfx = GetSleepingVfx(monster);
        if (sleepingVfx == null)
            return;

        sleepingVfx.Stop();
        sleepingVfx.QueueFree();
        if (!UndoReflectionUtil.TrySetPropertyValue(monster, "SleepingVfx", null))
            UndoReflectionUtil.TrySetFieldValue(monster, "_sleepingVfx", null);
    }

    private static NSleepingVfx? GetSleepingVfx(object monster)
    {
        return UndoReflectionUtil.FindProperty(monster.GetType(), "SleepingVfx")?.GetValue(monster) as NSleepingVfx
            ?? UndoReflectionUtil.FindField(monster.GetType(), "_sleepingVfx")?.GetValue(monster) as NSleepingVfx;
    }

    private static bool ReadBoolMonsterProperty(object monster, string propertyName)
    {
        if (UndoReflectionUtil.FindProperty(monster.GetType(), propertyName)?.GetValue(monster) is bool propertyValue)
            return propertyValue;

        string fieldName = '_' + char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
        return UndoReflectionUtil.FindField(monster.GetType(), fieldName)?.GetValue(monster) is bool fieldValue && fieldValue;
    }

    private static bool TryGetPaelsLegionExpectation(Creature creature, out PaelsLegionVisualExpectation? expectation, out PaelsLegionRelic? relic)
    {
        expectation = null;
        relic = null;
        if (creature.Monster is not PaelsLegionMonster)
            return false;

        Player? owner = creature.PetOwner;
        if (owner == null)
            return false;

        relic = owner.GetRelic<PaelsLegionRelic>();
        if (relic == null)
            return false;

        string trigger = GetPaelsLegionVisualTrigger(relic);
        expectation = new PaelsLegionVisualExpectation
        {
            Trigger = trigger,
            AcceptableAnimationNames = trigger switch
            {
                "BlockTrigger" => ["block", "block_loop"],
                "SleepTrigger" => ["sleep"],
                "WakeUpTrigger" => ["wake_up", "idle_loop"],
                _ => ["idle_loop"]
            }
        };
        return true;
    }

    private static bool ShouldWarmCreatureVisualScene(Creature creature)
    {
        return creature.Monster is PaelsLegionMonster or SlumberingBeetle or LagavulinMatriarch;
    }

    // Warm the creature visuals scene explicitly so restore does not depend on it
    // already being present in the preload cache.
    private static void WarmCreatureVisualScene(Creature creature)
    {
        if (creature.Monster == null)
            return;

        string? scenePath = creature.Monster.AssetPaths.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(scenePath))
            return;

        _ = PreloadManager.Cache.GetScene(scenePath);
    }

    private static string GetPaelsLegionVisualTrigger(PaelsLegionRelic relic)
    {
        int cooldown = UndoReflectionUtil.FindProperty(relic.GetType(), "Cooldown")?.GetValue(relic) is int value ? value : 0;
        bool triggeredBlockLastTurn = UndoReflectionUtil.FindProperty(relic.GetType(), "TriggeredBlockLastTurn")?.GetValue(relic) is bool triggered && triggered;
        if (cooldown <= 0)
            return "Idle";

        return triggeredBlockLastTurn ? "BlockTrigger" : "SleepTrigger";
    }
}
