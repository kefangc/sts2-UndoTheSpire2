// 文件说明：捕获战斗完整快照和运行时补充状态。
// Coordinates undo/redo history and restore transactions.
// Capture/restore details should live in dedicated services; this type is the orchestrator.
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Animation;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Enchantments;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Models;
using MegaCrit.Sts2.Core.Entities.Orbs;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Entities.UI;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Orbs;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Nodes.Vfx.Cards;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace UndoTheSpire2;

// Combat snapshot capture and runtime-state extraction.
public sealed partial class UndoController
{
    private UndoCombatFullState CaptureCurrentCombatFullState(UndoChoiceSpec? choiceSpecOverride = null)
    {
        RunState runState = RunManager.Instance.DebugOnlyGetState()
            ?? throw new InvalidOperationException("Run state was not available while capturing undo snapshot.");
        CombatState combatState = CombatManager.Instance.DebugOnlyGetState()
            ?? throw new InvalidOperationException("Combat state was not available while capturing undo snapshot.");
        UndoSelectionSessionState selectionSessionState = CaptureSelectionSessionState(choiceSpecOverride);
        UndoChoiceSpec? activeChoiceSpec = choiceSpecOverride ?? selectionSessionState.ChoiceSpec;

        return new UndoCombatFullState(
            CloneFullState(NetFullCombatState.FromRun(runState, null)),
            combatState.RoundNumber,
            combatState.CurrentSide,
            RunManager.Instance.ActionQueueSynchronizer.CombatState,
            RunManager.Instance.ActionQueueSet.NextActionId,
            RunManager.Instance.ActionQueueSynchronizer.NextHookId,
            RunManager.Instance.ChecksumTracker.NextId,
            UndoCombatHistoryCodec.Capture(runState, combatState),
            UndoActionKernelService.Capture(runState, activeChoiceSpec),
            CaptureMonsterStates(combatState.Creatures),
            CaptureCardCostStates(runState),
            CaptureCardRuntimeStates(runState, combatState),
            CapturePowerRuntimeStates(runState, combatState),
            CaptureRelicRuntimeStates(runState, combatState),
            selectionSessionState,
            CaptureFirstInSeriesPlayCounts(combatState),
            creatureTopologyStates: UndoCreatureTopologyCodecRegistry.Capture(combatState.Creatures),
            creatureStatusRuntimeStates: UndoCreatureStatusCodecRegistry.Capture(combatState.Creatures),
            creatureVisualStates: CaptureCreatureVisualStates(combatState.Creatures),
            combatCardDbState: CaptureCombatCardDbState(runState),
            playerOrbStates: CapturePlayerOrbStates(runState),
            playerDeckStates: CapturePlayerDeckStates(runState),
            playerPotionStates: CapturePlayerPotionStates(runState),
            audioLoopStates: UndoAudioLoopTracker.CaptureSnapshot(),
            pendingCombatRewardStates: UndoDelayedCombatRewardService.CaptureSnapshot());
    }

    private static IReadOnlyList<UndoPlayerOrbState> CapturePlayerOrbStates(RunState runState)
    {
        return [.. runState.Players.Select(player => new UndoPlayerOrbState
        {
            PlayerNetId = player.NetId,
            BaseOrbSlotCount = player.BaseOrbSlotCount,
            Capacity = player.PlayerCombatState?.OrbQueue.Capacity ?? player.BaseOrbSlotCount,
            Orbs = player.PlayerCombatState == null
                ? []
                : [.. player.PlayerCombatState.OrbQueue.Orbs.Select(CaptureOrbRuntimeState)]
        })];
    }

    private static UndoOrbRuntimeState CaptureOrbRuntimeState(OrbModel orb)
    {
        return new UndoOrbRuntimeState
        {
            OrbId = orb.Id,
            DarkEvokeValue = orb is DarkOrb darkOrb && FindField(darkOrb.GetType(), "_evokeVal")?.GetValue(darkOrb) is decimal darkEvokeValue
                ? darkEvokeValue
                : null,
            GlassPassiveValue = orb is GlassOrb glassOrb && FindField(glassOrb.GetType(), "_passiveVal")?.GetValue(glassOrb) is decimal glassPassiveValue
                ? glassPassiveValue
                : null
        };
    }

    private static IReadOnlyList<UndoPlayerDeckState> CapturePlayerDeckStates(RunState runState)
    {
        return [.. runState.Players.Select(player => new UndoPlayerDeckState
        {
            PlayerNetId = player.NetId,
            Cards = [.. player.Deck.Cards.Select(card => ClonePacketSerializable(card.ToSerializable()))]
        })];
    }

    private static IReadOnlyList<UndoPlayerPotionState> CapturePlayerPotionStates(RunState runState)
    {
        return [.. runState.Players.Select(player => new UndoPlayerPotionState
        {
            PlayerNetId = player.NetId,
            Slots =
            [
                .. Enumerable.Range(0, player.MaxPotionCount).Select(slotIndex => new UndoPotionSlotState
                {
                    SlotIndex = slotIndex,
                    Potion = player.GetPotionAtSlotIndex(slotIndex) is { } potion
                        ? ClonePacketSerializable(potion.ToSerializable(slotIndex))
                        : null
                })
            ]
        })];
    }

    private static IReadOnlyList<UndoMonsterState> CaptureMonsterStates(IReadOnlyList<Creature> creatures)
    {
        List<UndoMonsterState> states = [];
        for (int i = 0; i < creatures.Count; i++)
        {
            Creature creature = creatures[i];
            MonsterModel? monster = creature.Monster;
            if (monster?.MoveStateMachine == null)
                continue;

            MonsterMoveStateMachine moveStateMachine = monster.MoveStateMachine;
            string creatureKey = BuildCreatureKey(creature, i);
            string? currentStateId = GetPrivateFieldValue<MonsterState>(moveStateMachine, "_currentState")?.Id;
            bool performedFirstMove = FindField(moveStateMachine.GetType(), "_performedFirstMove")?.GetValue(moveStateMachine) is true;
            bool nextMovePerformedAtLeastOnce = monster.NextMove != null
                && FindField(monster.NextMove.GetType(), "_performedAtLeastOnce")?.GetValue(monster.NextMove) is true;
            string? transientNextMoveFollowUpId = monster.NextMove?.Id == MonsterModel.stunnedMoveId
                ? monster.NextMove.FollowUpState?.Id ?? monster.NextMove.FollowUpStateId
                : null;
            string? specialNodeStateKey = creature.Powers.OfType<SwipePower>().Any(static power => power.StolenCard != null)
                ? "%StolenCardPos"
                : null;
            NCreatureVisuals? creatureVisuals = NCombatRoom.Instance?.GetCreatureNode(creature)?.Visuals;
            float? visualHue = creatureVisuals == null
                ? null
                : FindField(creatureVisuals.GetType(), "_hue")?.GetValue(creatureVisuals) is float hue
                    ? hue
                    : null;
            states.Add(new UndoMonsterState
            {
                CreatureKey = creatureKey,
                SlotName = string.IsNullOrWhiteSpace(creature.SlotName) ? null : creature.SlotName,
                VisualDefaultScale = creatureVisuals?.DefaultScale,
                VisualHue = visualHue,
                CurrentStateId = currentStateId,
                NextMoveId = monster.NextMove?.Id,
                IsHovering = monster is ThievingHopper thievingHopper && thievingHopper.IsHovering,
                SpawnedThisTurn = monster.SpawnedThisTurn,
                PerformedFirstMove = performedFirstMove,
                NextMovePerformedAtLeastOnce = nextMovePerformedAtLeastOnce,
                TransientNextMoveFollowUpId = transientNextMoveFollowUpId,
                SpecialNodeStateKey = specialNodeStateKey,
                StarterMoveIndex = UndoMonsterMoveStateUtil.TryGetStarterMoveIndex(monster, out int starterMoveIndex)
                    ? starterMoveIndex
                    : null,
                TurnsUntilSummonable = monster is TwoTailedRat
                    && FindField(monster.GetType(), "_turnsUntilSummonable")?.GetValue(monster) is int turnsUntilSummonable
                    ? turnsUntilSummonable
                    : null,
                CallForBackupCount = monster is TwoTailedRat twoTailedRatWithBackup ? twoTailedRatWithBackup.CallForBackupCount : null,
                FabricatorLastSpawnedMonsterId = CaptureFabricatorLastSpawnedMonsterId(monster),
                LivingFogBloatAmount = CaptureLivingFogBloatAmount(monster),
                ToughEggIsHatched = CaptureToughEggIsHatched(monster),
                ToughEggVisualHatched = CaptureToughEggVisualHatched(monster),
                ToughEggAfterHatchedStateId = CaptureToughEggAfterHatchedStateId(monster),
                ToughEggHatchPos = CaptureToughEggHatchPos(monster),
                StateLogIds = [.. moveStateMachine.StateLog.Select(static state => state.Id)]
            });
        }

        return states;
    }

    private static ModelId? CaptureFabricatorLastSpawnedMonsterId(MonsterModel monster)
    {
        return monster is Fabricator fabricator
            && FindField(fabricator.GetType(), "_lastSpawned")?.GetValue(fabricator) is MonsterModel lastSpawned
                ? lastSpawned.Id
                : null;
    }

    private static int? CaptureLivingFogBloatAmount(MonsterModel monster)
    {
        return monster is LivingFog
            && FindProperty(monster.GetType(), "BloatAmount")?.GetValue(monster) is int bloatAmount
                ? bloatAmount
                : null;
    }

    private static bool? CaptureToughEggIsHatched(MonsterModel monster)
    {
        return monster is ToughEgg
            && FindProperty(monster.GetType(), "IsHatched")?.GetValue(monster) is bool isHatched
                ? isHatched
                : null;
    }

    private static bool? CaptureToughEggVisualHatched(MonsterModel monster)
    {
        return monster is ToughEgg
            && FindField(monster.GetType(), "_hatched")?.GetValue(monster) is bool visualHatched
                ? visualHatched
                : null;
    }

    private static string? CaptureToughEggAfterHatchedStateId(MonsterModel monster)
    {
        return monster is ToughEgg
            && FindProperty(monster.GetType(), "AfterHatchedState")?.GetValue(monster) is MonsterState afterHatchedState
                ? afterHatchedState.Id
                : null;
    }

    private static Vector2? CaptureToughEggHatchPos(MonsterModel monster)
    {
        return monster is ToughEgg
            && FindProperty(monster.GetType(), "HatchPos")?.GetValue(monster) is Vector2 hatchPos
                ? hatchPos
                : null;
    }

    private static IReadOnlyList<UndoCreatureVisualState> CaptureCreatureVisualStates(IReadOnlyList<Creature> creatures)
    {
        NCombatRoom? combatRoom = NCombatRoom.Instance;
        if (combatRoom == null)
            return [];

        List<UndoCreatureVisualState> states = [];
        for (int i = 0; i < creatures.Count; i++)
        {
            Creature creature = creatures[i];
            NCreature? creatureNode = combatRoom.GetCreatureNode(creature);
            NCreatureVisuals? creatureVisuals = creatureNode?.Visuals;
            if (creatureNode == null || creatureVisuals == null)
                continue;

            float? visualHue = FindField(creatureVisuals.GetType(), "_hue")?.GetValue(creatureVisuals) is float hue
                ? hue
                : null;
            float? tempScale = FindField(creatureNode.GetType(), "_tempScale")?.GetValue(creatureNode) is float scale
                ? scale
                : null;
            states.Add(new UndoCreatureVisualState
            {
                CreatureKey = BuildCreatureKey(creature, i),
                NodePosition = creatureNode.Position,
                VisualDefaultScale = creatureVisuals.DefaultScale,
                VisualHue = visualHue,
                TempScale = tempScale,
                TrackStates = CaptureCreatureTrackStates(creatureVisuals),
                CanvasStates = CaptureCreatureCanvasStates(creatureVisuals),
                ParticleStates = CaptureCreatureParticleStates(creatureVisuals),
                ShaderParamStates = CaptureCreatureShaderParamStates(creatureVisuals),
                StateDisplayState = CaptureCreatureStateDisplayState(creatureNode),
                AnimatorState = CaptureCreatureAnimatorState(creatureNode)
            });
        }

        return states;
    }

    private static UndoCreatureStateDisplayState? CaptureCreatureStateDisplayState(NCreature creatureNode)
    {
        NCreatureStateDisplay? stateDisplay = GetPrivateFieldValue<NCreatureStateDisplay>(creatureNode, "_stateDisplay");
        if (stateDisplay == null)
            return null;

        NHealthBar? healthBar = GetPrivateFieldValue<NHealthBar>(stateDisplay, "_healthBar");
        Control? blockContainer = healthBar == null ? null : GetPrivateFieldValue<Control>(healthBar, "_blockContainer");
        Control? blockOutline = healthBar == null ? null : GetPrivateFieldValue<Control>(healthBar, "_blockOutline");
        return new UndoCreatureStateDisplayState
        {
            OriginalPosition = UndoReflectionUtil.FindField(stateDisplay.GetType(), "_originalPosition")?.GetValue(stateDisplay) is Vector2 originalPosition
                ? originalPosition
                : null,
            CurrentPosition = stateDisplay.Position,
            Visible = stateDisplay.Visible,
            Modulate = stateDisplay.Modulate,
            HealthBarState = healthBar == null
                ? null
                : new UndoCreatureHealthBarState
                {
                    OriginalBlockPosition = UndoReflectionUtil.FindField(healthBar.GetType(), "_originalBlockPosition")?.GetValue(healthBar) is Vector2 originalBlockPosition
                        ? originalBlockPosition
                        : null,
                    CurrentBlockPosition = blockContainer?.Position,
                    BlockVisible = blockContainer?.Visible,
                    BlockOutlineVisible = blockOutline?.Visible,
                    BlockModulate = blockContainer?.Modulate
                }
        };
    }

    private static IReadOnlyList<UndoCreatureTrackState> CaptureCreatureTrackStates(Node root)
    {
        List<UndoCreatureTrackState> states = [];
        foreach (Node node in EnumerateCreatureVisualNodes(root))
        {
            if (node is not Node2D node2D || !string.Equals(node2D.GetClass(), "SpineSprite", StringComparison.Ordinal))
                continue;

            MegaAnimationState animationState = new MegaSprite(node2D).GetAnimationState();
            string relativePath = BuildCreatureVisualRelativePath(root, node);
            for (int trackIndex = 0; trackIndex < 4; trackIndex++)
            {
                UndoCreatureTrackState? trackState = TryCaptureCreatureTrackState(animationState, relativePath, trackIndex);
                if (trackState != null)
                    states.Add(trackState);
            }
        }

        return states;
    }

    private static UndoCreatureAnimatorState? CaptureCreatureAnimatorState(NCreature creatureNode)
    {
        if (GetPrivateFieldValue<CreatureAnimator>(creatureNode, "_spineAnimator") is not CreatureAnimator animator)
            return null;

        if (FindField(animator.GetType(), "_currentState")?.GetValue(animator) is not AnimState currentState
            || string.IsNullOrWhiteSpace(currentState.Id))
        {
            return null;
        }

        return new UndoCreatureAnimatorState
        {
            StateId = currentState.Id,
            NextStateId = currentState.NextState?.Id,
            HasLooped = currentState.HasLooped
        };
    }

    private static UndoCreatureTrackState? TryCaptureCreatureTrackState(MegaAnimationState animationState, string relativePath, int trackIndex)
    {
        try
        {
            MegaTrackEntry? trackEntry = UndoReflectionUtil.FindMethod(animationState.GetType(), "GetCurrent")?.Invoke(animationState, [trackIndex]) as MegaTrackEntry;
            if (trackEntry == null)
                return null;

            MegaAnimation? animation = UndoReflectionUtil.FindMethod(trackEntry.GetType(), "GetAnimation")?.Invoke(trackEntry, null) as MegaAnimation;
            string? animationName = animation?.GetName();
            if (string.IsNullOrWhiteSpace(animationName))
                return null;

            bool? loop = TryReadTrackEntryLoop(trackEntry);
            float? trackTime = UndoReflectionUtil.FindMethod(trackEntry.GetType(), "GetTrackTime")?.Invoke(trackEntry, null) is float resolvedTrackTime
                ? resolvedTrackTime
                : null;
            return new UndoCreatureTrackState
            {
                RelativePath = relativePath,
                TrackIndex = trackIndex,
                AnimationName = animationName,
                Loop = loop,
                TrackTime = trackTime
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool? TryReadTrackEntryLoop(MegaTrackEntry trackEntry)
    {
        try
        {
            if (UndoReflectionUtil.FindProperty(trackEntry.GetType(), "BoundObject")?.GetValue(trackEntry) is not GodotObject boundObject)
                return null;

            Variant loopValue = boundObject.Get("loop");
            return loopValue.VariantType == Variant.Type.Bool ? loopValue.AsBool() : null;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<UndoCreatureCanvasState> CaptureCreatureCanvasStates(Node root)
    {
        return EnumerateCreatureVisualNodes(root)
            .OfType<CanvasItem>()
            .Select(node => new UndoCreatureCanvasState
            {
                RelativePath = BuildCreatureVisualRelativePath(root, node),
                Visible = node.Visible
            })
            .ToList();
    }

    private static IReadOnlyList<UndoCreatureParticleState> CaptureCreatureParticleStates(Node root)
    {
        List<UndoCreatureParticleState> states = [];
        foreach (Node node in EnumerateCreatureVisualNodes(root))
        {
            switch (node)
            {
                case GpuParticles2D gpuParticles:
                    states.Add(new UndoCreatureParticleState
                    {
                        RelativePath = BuildCreatureVisualRelativePath(root, gpuParticles),
                        Emitting = gpuParticles.Emitting
                    });
                    break;
                case CpuParticles2D cpuParticles:
                    states.Add(new UndoCreatureParticleState
                    {
                        RelativePath = BuildCreatureVisualRelativePath(root, cpuParticles),
                        Emitting = cpuParticles.Emitting
                    });
                    break;
            }
        }

        return states;
    }

    private static IReadOnlyList<UndoCreatureShaderParamState> CaptureCreatureShaderParamStates(Node root)
    {
        List<UndoCreatureShaderParamState> states = [];
        foreach (Node node in EnumerateCreatureVisualNodes(root))
        {
            string relativePath = BuildCreatureVisualRelativePath(root, node);

            if (node is CanvasItem canvasItem
                && canvasItem.Material is ShaderMaterial canvasShaderMaterial
                && !string.Equals(node.GetClass(), "SpineSprite", StringComparison.Ordinal))
            {
                CaptureShaderMaterialStates(states, relativePath, UndoCreatureShaderMaterialBindingKind.CanvasItemMaterial, canvasShaderMaterial);
            }

            if (node is Node2D node2D && string.Equals(node2D.GetClass(), "SpineSprite", StringComparison.Ordinal))
            {
                if (new MegaSprite(node2D).GetNormalMaterial() is ShaderMaterial spineShaderMaterial)
                {
                    CaptureShaderMaterialStates(states, relativePath, UndoCreatureShaderMaterialBindingKind.SpineNormalMaterial, spineShaderMaterial);
                }
            }

            if (string.Equals(node.GetClass(), "SpineSlotNode", StringComparison.Ordinal)
                && new MegaSlotNode(node).GetNormalMaterial() is ShaderMaterial slotShaderMaterial)
            {
                CaptureShaderMaterialStates(states, relativePath, UndoCreatureShaderMaterialBindingKind.SpineSlotNormalMaterial, slotShaderMaterial);
            }
        }

        return states;
    }

    private static void CaptureShaderMaterialStates(
        ICollection<UndoCreatureShaderParamState> states,
        string relativePath,
        UndoCreatureShaderMaterialBindingKind bindingKind,
        ShaderMaterial material)
    {
        foreach (Godot.Collections.Dictionary property in material.GetPropertyList())
        {
            string? propertyName = property.ContainsKey("name") ? property["name"].ToString() : null;
            if (string.IsNullOrWhiteSpace(propertyName)
                || !propertyName.StartsWith("shader_parameter/", StringComparison.Ordinal))
            {
                continue;
            }

            string paramName = propertyName["shader_parameter/".Length..];
            Variant value = material.Get(propertyName);
            switch (value.VariantType)
            {
                case Variant.Type.Bool:
                    states.Add(new UndoCreatureShaderParamState
                    {
                        RelativePath = relativePath,
                        ParamName = paramName,
                        BindingKind = bindingKind,
                        ValueKind = UndoCreatureShaderParamValueKind.Bool,
                        BoolValue = value.AsBool()
                    });
                    break;
                case Variant.Type.Int:
                    states.Add(new UndoCreatureShaderParamState
                    {
                        RelativePath = relativePath,
                        ParamName = paramName,
                        BindingKind = bindingKind,
                        ValueKind = UndoCreatureShaderParamValueKind.Int,
                        IntValue = value.AsInt32()
                    });
                    break;
                case Variant.Type.Float:
                    states.Add(new UndoCreatureShaderParamState
                    {
                        RelativePath = relativePath,
                        ParamName = paramName,
                        BindingKind = bindingKind,
                        ValueKind = UndoCreatureShaderParamValueKind.Float,
                        FloatValue = value.AsSingle()
                    });
                    break;
                case Variant.Type.Vector2:
                    states.Add(new UndoCreatureShaderParamState
                    {
                        RelativePath = relativePath,
                        ParamName = paramName,
                        BindingKind = bindingKind,
                        ValueKind = UndoCreatureShaderParamValueKind.Vector2,
                        Vector2Value = value.AsVector2()
                    });
                    break;
                case Variant.Type.Color:
                    states.Add(new UndoCreatureShaderParamState
                    {
                        RelativePath = relativePath,
                        ParamName = paramName,
                        BindingKind = bindingKind,
                        ValueKind = UndoCreatureShaderParamValueKind.Color,
                        ColorValue = value.AsColor()
                    });
                    break;
            }
        }
    }

    private static IEnumerable<Node> EnumerateCreatureVisualNodes(Node root)
    {
        yield return root;
        foreach (Node child in root.GetChildren())
        {
            foreach (Node descendant in EnumerateCreatureVisualNodes(child))
                yield return descendant;
        }
    }

    private static string BuildCreatureVisualRelativePath(Node root, Node node)
    {
        return ReferenceEquals(root, node) ? "." : root.GetPathTo(node).ToString();
    }

    private static string BuildCreatureKey(Creature creature, int index)
    {
        if (creature.Player != null)
            return $"player:{index}:{creature.Player.NetId}";

        if (creature.PetOwner != null && creature.Monster != null)
            return $"pet:{index}:{creature.PetOwner.NetId}:{creature.Monster.Id.Entry}";

        if (creature.Monster != null)
            return $"monster:{index}:{creature.Monster.Id.Entry}";

        return $"creature:{index}";
    }

    private static string? TryResolveCreatureKey(IReadOnlyList<Creature> creatures, Creature? target)
    {
        if (target == null)
            return null;

        for (int i = 0; i < creatures.Count; i++)
        {
            if (ReferenceEquals(creatures[i], target))
                return BuildCreatureKey(target, i);
        }

        if (target.PetOwner != null && target.Monster != null)
        {
            for (int i = 0; i < creatures.Count; i++)
            {
                Creature candidate = creatures[i];
                if (candidate.PetOwner?.NetId != target.PetOwner.NetId)
                    continue;

                if (candidate.Monster?.Id != target.Monster.Id)
                    continue;

                return BuildCreatureKey(candidate, i);
            }
        }

        if (target.Player != null)
        {
            for (int i = 0; i < creatures.Count; i++)
            {
                if (creatures[i].Player?.NetId == target.Player.NetId)
                    return BuildCreatureKey(creatures[i], i);
            }
        }

        if (target.Monster != null)
        {
            for (int i = 0; i < creatures.Count; i++)
            {
                Creature candidate = creatures[i];
                if (candidate.Monster?.Id == target.Monster.Id)
                    return BuildCreatureKey(candidate, i);
            }
        }

        return null;
    }

    private static IReadOnlyList<UndoPlayerPileCardCostState> CaptureCardCostStates(RunState runState)
    {
        List<UndoPlayerPileCardCostState> states = [];
        foreach (Player player in runState.Players)
        {
            foreach (PileType pileType in CombatPileOrder)
            {
                CardPile? pile = CardPile.Get(pileType, player);
                if (pile == null)
                    continue;

                states.Add(new UndoPlayerPileCardCostState
                {
                    PlayerNetId = player.NetId,
                    PileType = pileType,
                    Cards = [.. pile.Cards.Select(CaptureCardCostState)]
                });
            }
        }

        return states;
    }

    private static IReadOnlyList<UndoPlayerPileCardRuntimeState> CaptureCardRuntimeStates(RunState runState, CombatState combatState)
    {
        UndoRuntimeCaptureContext context = new()
        {
            RunState = runState,
            CombatState = combatState
        };

        List<UndoPlayerPileCardRuntimeState> states = [];
        foreach (Player player in runState.Players)
        {
            foreach (PileType pileType in CombatPileOrder)
            {
                CardPile? pile = CardPile.Get(pileType, player);
                if (pile == null)
                    continue;

                states.Add(new UndoPlayerPileCardRuntimeState
                {
                    PlayerNetId = player.NetId,
                    PileType = pileType,
                    Cards = [.. pile.Cards.Select(card => CaptureCardRuntimeState(card, context))]
                });
            }
        }

        return states;
    }

    private static UndoCardRuntimeState CaptureCardRuntimeState(CardModel card, UndoRuntimeCaptureContext context)
    {
        return CreateCardRuntimeState(
            card,
            CaptureEnchantmentRuntimeState(card.Enchantment),
            CaptureAfflictionRuntimeState(card.Affliction),
            UndoRuntimeStateCodecRegistry.CaptureCardStates(card, context));
    }

    private static UndoCardRuntimeState CaptureDefaultCardRuntimeState(CardModel card)
    {
        return CreateCardRuntimeState(card, CaptureEnchantmentRuntimeState(card.Enchantment), CaptureAfflictionRuntimeState(card.Affliction), []);
    }

    internal static UndoCardCostState CaptureChoiceOptionCostState(CardModel card)
    {
        return CaptureCardCostState(card);
    }

    internal static UndoCardRuntimeState CaptureChoiceOptionRuntimeState(CardModel card)
    {
        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        CombatState? combatState = card.Owner?.Creature?.CombatState ?? CombatManager.Instance.DebugOnlyGetState();
        if (runState != null && combatState != null)
        {
            UndoRuntimeCaptureContext context = new()
            {
                RunState = runState,
                CombatState = combatState
            };
            return CaptureCardRuntimeState(card, context);
        }

        return CaptureDefaultCardRuntimeState(card);
    }

    private static UndoCardRuntimeState CreateCardRuntimeState(
        CardModel card,
        UndoEnchantmentRuntimeState? enchantmentState,
        UndoAfflictionRuntimeState? afflictionState,
        IReadOnlyList<UndoComplexRuntimeState> complexStates)
    {
        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        return new UndoCardRuntimeState
        {
            BaseReplayCount = card.BaseReplayCount,
            HasSingleTurnRetain = FindProperty(card.GetType(), "HasSingleTurnRetain")?.GetValue(card) is bool retain && retain,
            HasSingleTurnSly = FindProperty(card.GetType(), "HasSingleTurnSly")?.GetValue(card) is bool sly && sly,
            ExhaustOnNextPlay = card.ExhaustOnNextPlay,
            DeckVersionRef = runState != null && card.DeckVersion != null
                ? UndoStableRefs.CaptureCardRef(runState, card.DeckVersion)
                : null,
            EnchantmentState = enchantmentState,
            AfflictionState = afflictionState,
            ComplexStates = complexStates
        };
    }
    private static UndoEnchantmentRuntimeState? CaptureEnchantmentRuntimeState(EnchantmentModel? enchantment)
    {
        if (enchantment == null)
            return null;

        return new UndoEnchantmentRuntimeState
        {
            Serializable = ClonePacketSerializable(enchantment.ToSerializable()),
            Status = enchantment.Status,
            BoolProperties = CaptureRuntimeBoolProperties(enchantment),
            IntProperties = CaptureRuntimeIntProperties(enchantment, "Amount"),
            EnumProperties = CaptureRuntimeEnumProperties(enchantment)
        };
    }

    private static UndoAfflictionRuntimeState? CaptureAfflictionRuntimeState(AfflictionModel? affliction)
    {
        if (affliction == null)
            return null;

        return new UndoAfflictionRuntimeState
        {
            BoolProperties = CaptureRuntimeBoolProperties(affliction, "Card"),
            IntProperties = CaptureRuntimeIntProperties(affliction, "Amount"),
            EnumProperties = CaptureRuntimeEnumProperties(affliction)
        };
    }

    private static IReadOnlyList<UndoPowerRuntimeState> CapturePowerRuntimeStates(RunState runState, CombatState combatState)
    {
        UndoRuntimeCaptureContext context = new()
        {
            RunState = runState,
            CombatState = combatState
        };

        List<UndoPowerRuntimeState> states = [];
        IReadOnlyList<Creature> creatures = combatState.Creatures;
        for (int i = 0; i < creatures.Count; i++)
        {
            Creature creature = creatures[i];
            string ownerCreatureKey = BuildCreatureKey(creature, i);
            Dictionary<ModelId, int> ordinalsByPowerId = [];
            foreach (PowerModel power in creature.Powers)
            {
                int ordinal = ordinalsByPowerId.TryGetValue(power.Id, out int existingOrdinal) ? existingOrdinal : 0;
                ordinalsByPowerId[power.Id] = ordinal + 1;
                states.Add(new UndoPowerRuntimeState
                {
                    OwnerCreatureKey = ownerCreatureKey,
                    PowerId = power.Id,
                    Ordinal = ordinal,
                    TargetCreatureKey = TryResolveCreatureKey(creatures, power.Target),
                    ApplierCreatureKey = TryResolveCreatureKey(creatures, power.Applier),
                    StolenCard = power is SwipePower swipe && swipe.StolenCard != null
                        ? ClonePacketSerializable(swipe.StolenCard.ToSerializable())
                        : null,
                    StolenCardDeckVersion = power is SwipePower swipeWithDeckVersion
                        && swipeWithDeckVersion.StolenCard?.DeckVersion != null
                            ? ClonePacketSerializable(swipeWithDeckVersion.StolenCard.DeckVersion.ToSerializable())
                            : null,
                    TriggeredPlayerNetIds = CaptureTriggeredPlayerNetIds(power),
                    BoolProperties = CapturePowerRuntimeBoolProperties(power),
                    IntProperties = CaptureRuntimeIntProperties(power, "Amount"),
                    EnumProperties = CaptureRuntimeEnumProperties(power),
                    ComplexStates = UndoRuntimeStateCodecRegistry.CapturePowerStates(power, context)
                });
            }
        }

        return states;
    }
    private static IReadOnlyList<UndoRelicRuntimeState> CaptureRelicRuntimeStates(RunState runState, CombatState combatState)
    {
        UndoRuntimeCaptureContext context = new()
        {
            RunState = runState,
            CombatState = combatState
        };

        List<UndoRelicRuntimeState> states = [];
        foreach (Player player in runState.Players)
        {
            Dictionary<ModelId, int> ordinalsByRelicId = [];
            foreach (RelicModel relic in player.Relics)
            {
                int ordinal = ordinalsByRelicId.TryGetValue(relic.Id, out int existingOrdinal) ? existingOrdinal : 0;
                ordinalsByRelicId[relic.Id] = ordinal + 1;
                states.Add(new UndoRelicRuntimeState
                {
                    PlayerNetId = player.NetId,
                    RelicId = relic.Id,
                    Ordinal = ordinal,
                    Status = relic.Status,
                    IsActivating = UndoRuntimeStateCodecRegistry.CaptureRelicIsActivatingForSavestate(relic),
                    BoolProperties = CaptureRuntimeBoolProperties(relic, "IsActivating"),
                    IntProperties = CaptureRuntimeIntProperties(relic),
                    EnumProperties = CaptureRuntimeEnumProperties(relic),
                    ComplexStates = UndoRuntimeStateCodecRegistry.CaptureRelicStates(relic, context)
                });
            }
        }

        return states;
    }
    private static UndoSelectionSessionState CaptureSelectionSessionState(UndoChoiceSpec? choiceSpecOverride = null)
    {
        NCombatUi? combatUi = NCombatRoom.Instance?.Ui;
        object? overlay = NOverlayStack.Instance?.Peek();
        return new UndoSelectionSessionState
        {
            HandSelectionActive = combatUi?.Hand?.IsInCardSelection == true,
            OverlaySelectionActive = overlay is NChooseACardSelectionScreen or NCardGridSelectionScreen,
            SupportedChoiceUiActive = combatUi != null && IsSupportedChoiceUiActive(combatUi),
            OverlayScreenType = overlay?.GetType().Name,
            ChoiceSpec = choiceSpecOverride ?? TryCaptureCurrentChoiceSpecFromUi()
        };
    }
    private IReadOnlyList<UndoFirstInSeriesPlayCountState> CaptureFirstInSeriesPlayCounts(CombatState combatState)
    {
        if (_hasFirstInSeriesPlayCountOverride
            && combatState.RoundNumber == _firstInSeriesPlayCountOverrideRound
            && combatState.CurrentSide == _firstInSeriesPlayCountOverrideSide)
        {
            return _firstInSeriesPlayCountOverrides
                .Select(static pair => new UndoFirstInSeriesPlayCountState
                {
                    CreatureKey = pair.Key,
                    Count = pair.Value
                })
                .ToList();
        }

        return CaptureFirstInSeriesPlayCountsFromHistory(combatState);
    }

    private static ActionKernelState CaptureActionKernelState()
    {
        RunState runState = RunManager.Instance.DebugOnlyGetState()
            ?? throw new InvalidOperationException("Run state was not available while capturing action kernel state.");
        return UndoActionKernelService.Capture(runState, TryCaptureCurrentChoiceSpecFromUi());
    }

    private static IReadOnlyList<UndoFirstInSeriesPlayCountState> CaptureFirstInSeriesPlayCountsFromHistory(CombatState combatState)
    {
        List<UndoFirstInSeriesPlayCountState> states = [];
        IReadOnlyList<Creature> creatures = combatState.Creatures;
        foreach (CardPlayStartedEntry entry in CombatManager.Instance.History.CardPlaysStarted)
        {
            if (!entry.HappenedThisTurn(combatState) || !entry.CardPlay.IsFirstInSeries)
                continue;

            string? creatureKey = TryResolveCreatureKey(creatures, entry.Actor);
            if (string.IsNullOrWhiteSpace(creatureKey))
                continue;

            for (int i = 0; i < states.Count; i++)
            {
                if (states[i].CreatureKey != creatureKey)
                    continue;

                states[i] = new UndoFirstInSeriesPlayCountState
                {
                    CreatureKey = creatureKey,
                    Count = states[i].Count + 1
                };
                creatureKey = null;
                break;
            }

            if (!string.IsNullOrWhiteSpace(creatureKey))
            {
                states.Add(new UndoFirstInSeriesPlayCountState
                {
                    CreatureKey = creatureKey,
                    Count = 1
                });
            }
        }

        return states;
    }

    private static IReadOnlyList<ulong> CaptureTriggeredPlayerNetIds(PowerModel power)
    {
        if (power is not VitalSparkPower)
            return [];

        if (FindField(typeof(PowerModel), "_internalData")?.GetValue(power) is not { } internalData)
            return [];

        if (FindField(internalData.GetType(), "playersTriggeredThisTurn")?.GetValue(internalData) is not System.Collections.IEnumerable triggeredPlayers)
            return [];

        List<ulong> playerNetIds = [];
        foreach (object? entry in triggeredPlayers)
        {
            if (entry is Player player)
                playerNetIds.Add(player.NetId);
        }

        return playerNetIds;
    }

    private static IReadOnlyList<UndoNamedBoolState> CapturePowerRuntimeBoolProperties(PowerModel power)
    {
        List<UndoNamedBoolState> states = [.. CaptureRuntimeBoolProperties(power, "Target", "Applier")];
        PropertyInfo? isRevivingProperty = FindProperty(power.GetType(), "IsReviving");
        if (isRevivingProperty?.PropertyType == typeof(bool)
            && states.All(static state => state.Name != "IsReviving"))
        {
            states.Add(new UndoNamedBoolState
            {
                Name = "IsReviving",
                Value = isRevivingProperty.GetValue(power) is bool isReviving && isReviving
            });
        }

        return states;
    }

    private static IReadOnlyList<UndoNamedBoolState> CaptureRuntimeBoolProperties(object model, params string[] excludedNames)
    {
        HashSet<string> excluded = [.. excludedNames];
        return GetRuntimeStateProperties(model.GetType())
            .Where(property => property.PropertyType == typeof(bool) && !ShouldSkipRuntimeStateProperty(model.GetType(), property.Name, excluded))
            .Select(property => new UndoNamedBoolState
            {
                Name = property.Name,
                Value = property.GetValue(model) is bool value && value
            })
            .ToList();
    }

    private static IReadOnlyList<UndoNamedIntState> CaptureRuntimeIntProperties(object model, params string[] excludedNames)
    {
        HashSet<string> excluded = [.. excludedNames];
        return GetRuntimeStateProperties(model.GetType())
            .Where(property => property.PropertyType == typeof(int) && !ShouldSkipRuntimeStateProperty(model.GetType(), property.Name, excluded))
            .Select(property => new UndoNamedIntState
            {
                Name = property.Name,
                Value = property.GetValue(model) is int value ? value : 0
            })
            .ToList();
    }

    private static IReadOnlyList<UndoNamedEnumState> CaptureRuntimeEnumProperties(object model, params string[] excludedNames)
    {
        HashSet<string> excluded = [.. excludedNames];
        return GetRuntimeStateProperties(model.GetType())
            .Where(property => property.PropertyType.IsEnum && !ShouldSkipRuntimeStateProperty(model.GetType(), property.Name, excluded))
            .Select(property => new UndoNamedEnumState
            {
                Name = property.Name,
                EnumTypeName = property.PropertyType.AssemblyQualifiedName ?? property.PropertyType.FullName ?? property.PropertyType.Name,
                Value = Convert.ToInt32(property.GetValue(model))
            })
            .ToList();
    }

    private static bool ShouldSkipRuntimeStateProperty(Type type, string propertyName, IReadOnlySet<string>? excludedNames = null)
    {
        if (excludedNames?.Contains(propertyName) == true)
            return true;

        if (propertyName.StartsWith("Test", StringComparison.Ordinal))
            return true;

        return type == typeof(ConfusedPower) && propertyName == "TestEnergyCostOverride";
    }

    private static IEnumerable<PropertyInfo> GetRuntimeStateProperties(Type type)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        return type
            .GetProperties(Flags)
            .Where(static property => property.GetIndexParameters().Length == 0)
            .Where(static property => property.CanRead)
            .Where(property => property.GetMethod != null)
            .Where(property => property.SetMethod != null || FindField(type, $"<{property.Name}>k__BackingField") != null)
            .Where(static property => property.Name != "Status");
    }
    private static UndoCardCostState CaptureCardCostState(CardModel card)
    {
        CardEnergyCost energyCost = card.EnergyCost;
        List<LocalCostModifier> localModifiers = GetPrivateFieldValue<List<LocalCostModifier>>(energyCost, "_localModifiers") ?? [];
        List<TemporaryCardCost> temporaryStarCosts = GetPrivateFieldValue<List<TemporaryCardCost>>(card, "_temporaryStarCosts") ?? [];

        return new UndoCardCostState
        {
            EnergyBaseCost = FindField(energyCost.GetType(), "_base")?.GetValue(energyCost) as int? ?? energyCost.Canonical,
            CapturedXValue = FindField(energyCost.GetType(), "_capturedXValue")?.GetValue(energyCost) as int? ?? 0,
            EnergyWasJustUpgraded = FindProperty(energyCost.GetType(), "WasJustUpgraded")?.GetValue(energyCost) as bool? ?? false,
            EnergyLocalModifiers =
            [
                .. localModifiers.Select(static modifier => new UndoLocalCostModifierState
                {
                    Amount = modifier.Amount,
                    Type = modifier.Type,
                    Expiration = modifier.Expiration,
                    IsReduceOnly = modifier.IsReduceOnly
                })
            ],
            StarCostSet = FindField(card.GetType(), "_starCostSet")?.GetValue(card) as bool? ?? false,
            BaseStarCost = FindField(card.GetType(), "_baseStarCost")?.GetValue(card) as int? ?? 0,
            StarWasJustUpgraded = FindField(card.GetType(), "_wasStarCostJustUpgraded")?.GetValue(card) as bool? ?? false,
            TemporaryStarCosts =
            [
                .. temporaryStarCosts.Select(static cost => new UndoTemporaryStarCostState
                {
                    Cost = cost.Cost,
                    ClearsWhenTurnEnds = cost.ClearsWhenTurnEnds,
                    ClearsWhenCardIsPlayed = cost.ClearsWhenCardIsPlayed
                })
            ]
        };
    }
}

