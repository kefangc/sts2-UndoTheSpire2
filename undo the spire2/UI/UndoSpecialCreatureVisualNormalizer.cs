// 文件说明：修正特殊 creature 在恢复后的视觉表现。
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Commands;
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

    public static void RefreshSingle(Creature creature, NCombatRoom combatRoom, bool normalizeAnimation = true)
    {
        if (ShouldWarmCreatureVisualScene(creature))
            WarmCreatureVisualScene(creature);

        if (creature.Monster is PaelsLegionMonster)
            RefreshPaelsLegionVisual(creature, combatRoom);

        RefreshCreatureStatusVisual(creature, combatRoom, normalizeAnimation);
    }

    public static void DetachStateDisplayTracking(NCombatRoom combatRoom)
    {
        foreach (NCreature creatureNode in combatRoom.CreatureNodes)
            RebindStateDisplayTracking(creatureNode, null);
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

    private static void RefreshCreatureStatusVisual(Creature creature, NCombatRoom combatRoom, bool normalizeAnimation = true)
    {
        MonsterModel? monster = creature.Monster;
        if (monster == null)
            return;

        NCreature? creatureNode = combatRoom.GetCreatureNode(creature);
        if (creatureNode == null)
            return;

        NormalizeCreatureNodeVisibility(creature, creatureNode);
        if (!normalizeAnimation)
            return;

        switch (monster)
        {
            case TestSubject testSubject:
                NormalizeTestSubject(testSubject, creatureNode);
                break;
            case DecimillipedeSegment decimillipedeSegment:
                NormalizeDecimillipedeSegment(decimillipedeSegment, creatureNode);
                break;
            case Parafright parafright:
                NormalizeParafright(parafright, creatureNode);
                break;
            case SlumberingBeetle slumberingBeetle:
                NormalizeSleepingBeetle(slumberingBeetle, creatureNode);
                break;
            case LagavulinMatriarch lagavulinMatriarch:
                NormalizeLagavulin(lagavulinMatriarch, creatureNode);
                break;
            case Tunneler tunneler:
                NormalizeTunneler(tunneler, creatureNode);
                break;
            case OwlMagistrate owlMagistrate:
                NormalizeOwlMagistrate(owlMagistrate, creatureNode);
                break;
            case Osty osty:
                NormalizeOsty(osty, creatureNode);
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
            case WaterfallGiant waterfallGiant:
                NormalizeWaterfallGiant(waterfallGiant, creatureNode);
                break;
        }
    }

    private static void NormalizeSleepingBeetle(SlumberingBeetle monster, NCreature creatureNode)
    {
        SlumberingBeetleVisualState visualState = GetSlumberingBeetleVisualState(monster);
        if (visualState == SlumberingBeetleVisualState.Sleeping)
        {
            EnsureSleepingVfx(monster, creatureNode);
            EnsureSleepLoopSfx("event:/sfx/enemy/enemy_attacks/slumbering_beetle/slumbering_beetle_sleep_loop");
            EnsureBaseAnimation(creatureNode, "sleep_loop", loop: true);
            return;
        }

        ClearSleepingVfx(monster);
        StopLoopSfx("event:/sfx/enemy/enemy_attacks/slumbering_beetle/slumbering_beetle_sleep_loop");
        if (visualState == SlumberingBeetleVisualState.WakeStun)
        {
            creatureNode.SetAnimationTrigger("WakeUp");
            EnsureBaseAnimation(creatureNode, "wake_up", loop: false);
            return;
        }

        creatureNode.SetAnimationTrigger("WakeUp");
        EnsureBaseAnimation(creatureNode, "idle_loop", loop: true);
    }

    private static void NormalizeLagavulin(LagavulinMatriarch monster, NCreature creatureNode)
    {
        LagavulinVisualState visualState = GetLagavulinVisualState(monster);
        if (visualState == LagavulinVisualState.Sleeping)
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

    private static void NormalizeTunneler(Tunneler monster, NCreature creatureNode)
    {
        TunnelerVisualState visualState = GetTunnelerVisualState(monster);
        ResetSpecialNodePosition(creatureNode.GetSpecialNode<Node2D>("Visuals/SpineBoneNode"));
        switch (visualState)
        {
            case TunnelerVisualState.Burrowed:
                EnsureBaseAnimation(creatureNode, "hidden_loop", loop: true);
                return;
            case TunnelerVisualState.Stunned:
                EnsureBaseAnimation(creatureNode, "idle_loop", loop: true);
                return;
            default:
                EnsureBaseAnimation(creatureNode, "idle_loop", loop: true);
                return;
        }
    }

    private static void NormalizeOwlMagistrate(OwlMagistrate monster, NCreature creatureNode)
    {
        OwlMagistrateVisualState visualState = GetOwlMagistrateVisualState(monster);
        if (visualState == OwlMagistrateVisualState.Flying)
        {
            SfxCmd.PlayLoop("event:/sfx/enemy/enemy_attacks/owl_magistrate/owl_magistrate_fly_loop", true);
            creatureNode.SetAnimationTrigger("TakeOff");
            EnsureBaseAnimation(creatureNode, "fly_loop", loop: true);
            ApplyBoundsContainer(creatureNode, "FlyingBounds");
            return;
        }

        StopLoopSfx("event:/sfx/enemy/enemy_attacks/owl_magistrate/owl_magistrate_fly_loop");
        EnsureBaseAnimation(creatureNode, "idle_loop", loop: true);
        ApplyBoundsContainer(creatureNode, "IdleBounds");
    }

    private static void NormalizeOsty(Osty monster, NCreature creatureNode)
    {
        Player? owner = monster.Creature.PetOwner;
        if (owner == null)
        {
            RebindStateDisplayTracking(creatureNode, null);
            return;
        }

        NCombatRoom? combatRoom = NCombatRoom.Instance;
        NCreature? ownerNode = combatRoom?.GetCreatureNode(owner.Creature);
        bool localNecrobinderOwner = LocalContext.IsMe(owner)
            && string.Equals(owner.Character.GetType().Name, "Necrobinder", StringComparison.Ordinal);

        if (monster.Creature.IsAlive)
        {
            RebindStateDisplayTracking(creatureNode, owner.Creature);
            creatureNode.OstyScaleToSize((float)monster.Creature.MaxHp, 0f);
            if (localNecrobinderOwner && ownerNode != null)
                creatureNode.Position = ownerNode.Position + NCreature.GetOstyOffsetFromPlayer(monster.Creature);
            EnsureIdleState(creatureNode);
            return;
        }

        RebindStateDisplayTracking(creatureNode, null);
        creatureNode.OstyScaleToSize((float)monster.Creature.MaxHp, 0f);
        creatureNode.StartDeathAnim(false);
        EnsureBaseAnimation(creatureNode, "dead_loop", loop: true);
    }

    private static void RebindStateDisplayTracking(NCreature creatureNode, Creature? creatureToTrack)
    {
        object? stateDisplay = UndoReflectionUtil.FindField(creatureNode.GetType(), "_stateDisplay")?.GetValue(creatureNode);
        if (stateDisplay is not GodotObject stateDisplayObject || !GodotObject.IsInstanceValid(stateDisplayObject))
            return;

        Creature? previousTrackedCreature = UndoReflectionUtil.FindField(stateDisplay.GetType(), "_blockTrackingCreature")?.GetValue(stateDisplay) as Creature;
        if (previousTrackedCreature != null)
            TryUnsubscribeBlockTracking(stateDisplay, previousTrackedCreature);

        UndoReflectionUtil.TrySetFieldValue(stateDisplay, "_blockTrackingCreature", null);
        object? healthBar = UndoReflectionUtil.FindField(stateDisplay.GetType(), "_healthBar")?.GetValue(stateDisplay);
        if (healthBar != null)
            UndoReflectionUtil.TrySetFieldValue(healthBar, "_blockTrackingCreature", null);

        if (creatureToTrack != null)
            creatureNode.TrackBlockStatus(creatureToTrack);
    }

    private static void TryUnsubscribeBlockTracking(object stateDisplay, Creature trackedCreature)
    {
        try
        {
            System.Reflection.MethodInfo? handlerMethod = UndoReflectionUtil.FindMethod(stateDisplay.GetType(), "OnBlockTrackingCreatureBlockChanged");
            if (handlerMethod == null)
                return;

            if (Delegate.CreateDelegate(typeof(Action<int, int>), stateDisplay, handlerMethod, false) is not Action<int, int> handler)
                return;

            trackedCreature.BlockChanged -= handler;
        }
        catch
        {
            // Best-effort cleanup. Restore should proceed even if an old display was already detached.
        }
    }

    internal enum SlumberingBeetleVisualState
    {
        Sleeping,
        WakeStun,
        Awake
    }

    internal enum LagavulinVisualState
    {
        Sleeping,
        WakeStun,
        Awake
    }

    internal enum TunnelerVisualState
    {
        Burrowed,
        Stunned,
        Surfaced
    }

    internal enum OwlMagistrateVisualState
    {
        Grounded,
        Flying
    }

    internal static SlumberingBeetleVisualState GetSlumberingBeetleVisualState(SlumberingBeetle monster)
    {
        bool hasSlumberPower = monster.Creature.HasPower<SlumberPower>();
        string? nextMoveId = monster.NextMove?.Id;
        if (hasSlumberPower && nextMoveId != MonsterModel.stunnedMoveId && !monster.IntendsToAttack)
            return SlumberingBeetleVisualState.Sleeping;

        if (nextMoveId == MonsterModel.stunnedMoveId)
            return SlumberingBeetleVisualState.WakeStun;

        return SlumberingBeetleVisualState.Awake;
    }

    internal static LagavulinVisualState GetLagavulinVisualState(LagavulinMatriarch monster)
    {
        string? nextMoveId = monster.NextMove?.Id;
        if (monster.Creature.HasPower<AsleepPower>() && nextMoveId != MonsterModel.stunnedMoveId && !monster.IntendsToAttack)
            return LagavulinVisualState.Sleeping;

        if (nextMoveId == MonsterModel.stunnedMoveId)
            return LagavulinVisualState.WakeStun;

        return LagavulinVisualState.Awake;
    }

    internal static TunnelerVisualState GetTunnelerVisualState(Tunneler monster)
    {
        string? nextMoveId = monster.NextMove?.Id;
        if (nextMoveId == MonsterModel.stunnedMoveId || string.Equals(nextMoveId, "DIZZY_MOVE", StringComparison.Ordinal))
            return TunnelerVisualState.Stunned;

        if (monster.Creature.HasPower<BurrowedPower>())
            return TunnelerVisualState.Burrowed;

        return TunnelerVisualState.Surfaced;
    }

    internal static OwlMagistrateVisualState GetOwlMagistrateVisualState(OwlMagistrate monster)
    {
        bool isFlying = ReadBoolMonsterProperty(monster, "IsFlying");
        return isFlying
            ? OwlMagistrateVisualState.Flying
            : OwlMagistrateVisualState.Grounded;
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
        Node2D? spineBoneNode = creatureNode.GetSpecialNode<Node2D>("Visuals/SpineBoneNode");
        if (spineBoneNode != null)
            spineBoneNode.Position = Vector2.Zero;

        Marker2D? stolenCardPos = creatureNode.Visuals?.GetNodeOrNull<Marker2D>("%StolenCardPos");
        bool isHoldingCard = monster.Creature.Powers.OfType<SwipePower>().Any(static power => power.StolenCard != null);
        if (!isHoldingCard && stolenCardPos != null)
            ClearNodeChildren(stolenCardPos);

        bool isHovering = ReadBoolMonsterProperty(monster, "IsHovering");
        if (isHovering)
        {
            creatureNode.SetAnimationTrigger("Hover");
            EnsureBaseAnimation(creatureNode, "hover_loop", loop: true);
            return;
        }

        creatureNode.SetAnimationTrigger("Idle");
        EnsureBaseAnimation(creatureNode, "idle_loop", loop: true);
    }

    private static void ClearNodeChildren(Node parent)
    {
        foreach (Node child in parent.GetChildren().OfType<Node>().ToList())
        {
            child.GetParent()?.RemoveChild(child);
            child.QueueFree();
        }
    }

    private static void NormalizeTestSubject(TestSubject monster, NCreature creatureNode)
    {
        int respawns = ReadIntMonsterProperty(monster, "Respawns");
        int phaseIndex = Math.Clamp(respawns + 1, 1, 3);
        creatureNode.SetDefaultScaleTo(1f + respawns * 0.1f, 0f);
        if (UndoMonsterMoveStateUtil.IsPendingRevive(monster.Creature))
        {
            creatureNode.SetAnimationTrigger("DeadTrigger");
            EnsureBaseAnimation(creatureNode, $"knocked_out_loop{phaseIndex}", loop: true);
            return;
        }

        if (monster.Creature.IsDead)
        {
            creatureNode.SetAnimationTrigger("Dead");
            EnsureBaseAnimation(creatureNode, "die", loop: false);
            return;
        }

        EnsureBaseAnimation(creatureNode, $"idle_loop{phaseIndex}", loop: true);
    }

    private static void NormalizeDecimillipedeSegment(DecimillipedeSegment monster, NCreature creatureNode)
    {
        if (UndoMonsterMoveStateUtil.IsPendingRevive(monster.Creature) || monster.Creature.IsDead)
        {
            creatureNode.SetAnimationTrigger("Dead");
            EnsureBaseAnimation(creatureNode, "dead_loop", loop: true);
            return;
        }

        EnsureIdleState(creatureNode);
    }

    private static void NormalizeParafright(Parafright monster, NCreature creatureNode)
    {
        if (UndoMonsterMoveStateUtil.IsPendingRevive(monster.Creature))
        {
            creatureNode.SetAnimationTrigger("StunTrigger");
            EnsureBaseAnimation(creatureNode, "stunned_loop", loop: true);
            return;
        }

        if (monster.Creature.IsDead)
        {
            creatureNode.SetAnimationTrigger("Dead");
            EnsureBaseAnimation(creatureNode, "die", loop: false);
            return;
        }

        EnsureIdleState(creatureNode);
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

    private static void NormalizeWaterfallGiant(WaterfallGiant monster, NCreature creatureNode)
    {
        SyncWaterfallGiantBuildUpTrack(monster, creatureNode);
        ReconcileWaterfallGiantVfx(monster, creatureNode);

        bool aboutToBlow = ReadBoolMonsterProperty(monster, "IsAboutToBlow")
            || monster.Creature.ShowsInfiniteHp
            || string.Equals(monster.NextMove?.Id, "ABOUT_TO_BLOW_MOVE", StringComparison.Ordinal);

        if (aboutToBlow)
        {
            EnsureBaseAnimation(creatureNode, "die_loop", loop: true);
            return;
        }

        if (monster.Creature.IsDead)
        {
            creatureNode.SetAnimationTrigger("Dead");
            EnsureBaseAnimation(creatureNode, "die_loop", loop: true);
            return;
        }

        EnsureBaseAnimation(creatureNode, "idle_loop", loop: true);
    }

    private static void NormalizeCreatureNodeVisibility(Creature creature, NCreature creatureNode)
    {
        bool nodeVisible = ShouldShowCreatureNode(creature);
        creatureNode.Visible = nodeVisible;
        creatureNode.Modulate = Colors.White;
        if (creatureNode.Visuals != null)
        {
            creatureNode.Visuals.Visible = nodeVisible;
            creatureNode.Visuals.Modulate = Colors.White;
        }

        if (creatureNode.Body != null)
        {
            creatureNode.Body.Visible = nodeVisible;
            creatureNode.Body.Modulate = Colors.White;
        }

        bool canShowMonsterUi = nodeVisible && creature.Monster?.IsHealthBarVisible == true;
        bool isPendingRevive = UndoMonsterMoveStateUtil.IsPendingRevive(creature);
        bool interactable = canShowMonsterUi && creature.IsAlive && !isPendingRevive;
        bool showStateDisplay = canShowMonsterUi && creature.IsAlive && !isPendingRevive;
        creatureNode.ToggleIsInteractable(interactable);
        NormalizeStateDisplayVisibility(creatureNode, showStateDisplay);
    }

    private static bool ShouldShowCreatureNode(Creature creature)
    {
        if (creature.Monster is not Door door)
            return creature.GetPower<DoorRevivalPower>()?.IsHalfDead != true;

        bool isHalfDead = creature.GetPower<DoorRevivalPower>()?.IsHalfDead == true;
        if (!isHalfDead)
            return true;

        Creature doormaker = door.Doormaker;
        CombatState? combatState = creature.CombatState;
        bool doormakerInCombat = combatState != null
            && doormaker.CombatState == combatState
            && combatState.ContainsCreature(doormaker);
        return !doormakerInCombat;
    }

    private static void NormalizeStateDisplayVisibility(NCreature creatureNode, bool showStateDisplay)
    {
        object? stateDisplay = UndoReflectionUtil.FindField(creatureNode.GetType(), "_stateDisplay")?.GetValue(creatureNode);
        if (stateDisplay is not Control stateDisplayControl || !GodotObject.IsInstanceValid(stateDisplayControl))
            return;

        bool visible = showStateDisplay && !NCombatUi.IsDebugHidingHpBar;
        stateDisplayControl.Visible = visible;
        Color modulate = stateDisplayControl.Modulate;
        modulate.A = visible ? 1f : 0f;
        stateDisplayControl.Modulate = modulate;
        if (visible)
            TryInvokePrivateMethod(stateDisplay, "RefreshValues");

        if (creatureNode.Entity is Creature creature && !creature.ShowsInfiniteHp)
            HideInfinityHealthIndicator(stateDisplay);
    }

    private static void SyncWaterfallGiantBuildUpTrack(WaterfallGiant monster, NCreature creatureNode)
    {
        var animationState = creatureNode.Visuals?.SpineBody?.GetAnimationState();
        if (animationState == null)
            return;

        int pressureBuildupIdx = ReadIntMonsterProperty(monster, "PressureBuildupIdx");
        string? expectedTrack = pressureBuildupIdx <= 0
            ? null
            : $"_tracks/buildup{Math.Clamp((int)MathF.Floor(pressureBuildupIdx * 0.5f), 1, 3)}";
        string? currentTrack = animationState.GetCurrent(1)?.GetAnimation()?.GetName();
        if (string.Equals(currentTrack, expectedTrack, StringComparison.Ordinal))
            return;

        if (expectedTrack == null)
        {
            animationState.AddEmptyAnimation(1);
            return;
        }

        animationState.SetAnimation(expectedTrack, true, 1);
    }

    private static void HideInfinityHealthIndicator(object stateDisplay)
    {
        object? healthBar = UndoReflectionUtil.FindField(stateDisplay.GetType(), "_healthBar")?.GetValue(stateDisplay);
        if (healthBar == null)
            return;

        if (UndoReflectionUtil.FindField(healthBar.GetType(), "_infinityTex")?.GetValue(healthBar) is TextureRect infinityTex)
            infinityTex.Visible = false;

        if (UndoReflectionUtil.FindField(healthBar.GetType(), "_hpLabel")?.GetValue(healthBar) is Control hpLabel)
            hpLabel.Visible = true;
    }

    private static void ReconcileWaterfallGiantVfx(WaterfallGiant monster, NCreature creatureNode)
    {
        Node? vfxNode = FindDescendantByTypeName(creatureNode.Visuals, "NWaterfallGiantVfx");
        if (vfxNode == null)
            return;

        bool aboutToBlow = ReadBoolMonsterProperty(monster, "IsAboutToBlow")
            || monster.Creature.ShowsInfiniteHp
            || string.Equals(monster.NextMove?.Id, "ABOUT_TO_BLOW_MOVE", StringComparison.Ordinal);
        int pressureBuildupIdx = ReadIntMonsterProperty(monster, "PressureBuildupIdx");
        int buildupLevel = pressureBuildupIdx <= 0 ? 0 : Math.Clamp((int)MathF.Floor(pressureBuildupIdx * 0.5f), 1, 3);

        SetWaterfallEmitter(vfxNode, "_mistParticles", emitting: true, visible: true);
        SetWaterfallEmitter(vfxNode, "_dropletParticles", emitting: true, visible: true);
        SetWaterfallEmitter(vfxNode, "_mouthParticles", emitting: true, visible: true);

        bool leakActive = aboutToBlow || buildupLevel > 0;
        bool steam1Active = aboutToBlow;
        bool steam2Active = aboutToBlow;
        bool steam34Active = aboutToBlow;
        bool steam56Active = aboutToBlow;

        SetWaterfallEmitter(vfxNode, "_steam1Particles", steam1Active, steam1Active);
        SetWaterfallEmitter(vfxNode, "_steam2Particles", steam2Active, steam2Active);
        SetWaterfallEmitter(vfxNode, "_steam3Particles", steam34Active, steam34Active);
        SetWaterfallEmitter(vfxNode, "_steam4Particles", steam34Active, steam34Active);
        SetWaterfallEmitter(vfxNode, "_steam5Particles", steam56Active, steam56Active);
        SetWaterfallEmitter(vfxNode, "_steam6Particles", steam56Active, steam56Active);
        SetWaterfallEmitter(vfxNode, "_steamLeakParticles1", leakActive, leakActive);
        SetWaterfallEmitter(vfxNode, "_steamLeakParticles2", leakActive, leakActive);
        SetWaterfallEmitter(vfxNode, "_steamLeakParticles3", leakActive, leakActive);
        ApplyWaterfallLeakIntensity(vfxNode, buildupLevel);

        UndoReflectionUtil.TrySetFieldValue(vfxNode, "_isDead", false);
    }

    private static void SetWaterfallEmitter(Node vfxNode, string fieldName, bool emitting, bool visible)
    {
        if (UndoReflectionUtil.FindField(vfxNode.GetType(), fieldName)?.GetValue(vfxNode) is not GpuParticles2D emitter)
            return;

        bool wasVisible = emitter.Visible;
        bool wasEmitting = emitter.Emitting;
        emitter.Visible = visible;
        emitter.Emitting = emitting;
        if (!visible)
        {
            emitter.Restart();
            return;
        }

        if (emitting && (!wasVisible || !wasEmitting))
            emitter.Restart();
    }

    private static void ApplyWaterfallLeakIntensity(Node vfxNode, int buildupLevel)
    {
        (int amount, float lifetime) = buildupLevel switch
        {
            1 => (8, 0.37f),
            2 => (15, 0.45f),
            3 => (20, 0.6f),
            _ => (0, 0f)
        };

        foreach (string fieldName in new[] { "_steamLeakParticles1", "_steamLeakParticles2", "_steamLeakParticles3" })
        {
            if (UndoReflectionUtil.FindField(vfxNode.GetType(), fieldName)?.GetValue(vfxNode) is not GpuParticles2D emitter)
                continue;

            if (buildupLevel <= 0)
            {
                emitter.Amount = 0;
                continue;
            }

            emitter.Amount = amount;
            emitter.Lifetime = lifetime;
        }
    }

    private static Node? FindDescendantByTypeName(Node? root, string typeName)
    {
        if (root == null)
            return null;

        foreach (Node child in root.GetChildren().OfType<Node>())
        {
            if (string.Equals(child.GetType().Name, typeName, StringComparison.Ordinal))
                return child;

            Node? nested = FindDescendantByTypeName(child, typeName);
            if (nested != null)
                return nested;
        }

        return null;
    }

    private static void ApplyBoundsContainer(NCreature creatureNode, string boundsContainerName)
    {
        Node? boundsContainer = creatureNode.Visuals?.GetNodeOrNull(boundsContainerName);
        if (boundsContainer == null)
            return;

        TryInvokePrivateMethod(creatureNode, "UpdateBounds", boundsContainer);
    }

    private static void EnsureIdleState(NCreature creatureNode)
    {
        if (TryInvokePrivateMethod(creatureNode, "ImmediatelySetIdle"))
        {
            string? currentAnimation = creatureNode.SpineController.GetAnimationState().GetCurrent(0)?.GetAnimation()?.GetName();
            if (string.Equals(currentAnimation, "idle_loop", StringComparison.Ordinal))
                return;
        }

        if (string.Equals(creatureNode.SpineController.GetAnimationState().GetCurrent(0)?.GetAnimation()?.GetName(), "idle_loop", StringComparison.Ordinal))
            return;
        EnsureBaseAnimation(creatureNode, "idle_loop", loop: true);
    }

    private static bool TryInvokePrivateMethod(object target, string name, params object?[]? args)
    {
        try
        {
            var method = UndoReflectionUtil.FindMethod(target.GetType(), name);
            if (method == null)
                return false;

            method.Invoke(target, args);
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

    private static void ResetSpecialNodePosition(Node2D? specialNode)
    {
        if (specialNode == null)
            return;

        specialNode.Position = Vector2.Zero;
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

    private static void EnsureSleepLoopSfx(string eventName)
    {
        SfxCmd.PlayLoop(eventName, true);
    }

    private static void StopLoopSfx(string eventName)
    {
        SfxCmd.StopLoop(eventName);
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

    private static int ReadIntMonsterProperty(object monster, string propertyName)
    {
        if (UndoReflectionUtil.FindProperty(monster.GetType(), propertyName)?.GetValue(monster) is int propertyValue)
            return propertyValue;

        string fieldName = '_' + char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
        return UndoReflectionUtil.FindField(monster.GetType(), fieldName)?.GetValue(monster) as int? ?? 0;
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
        return creature.Monster is PaelsLegionMonster or SlumberingBeetle or LagavulinMatriarch or Tunneler or OwlMagistrate or Osty;
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



