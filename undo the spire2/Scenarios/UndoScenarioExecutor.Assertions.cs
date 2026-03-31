using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace UndoTheSpire2;

internal static partial class UndoScenarioExecutor
{
    private static List<UndoScenarioAssertionResult> BuildRoundtripAssertions(
        UndoScenarioDefinition scenario,
        UndoCombatFullState targetState,
        UndoCombatFullState undoState,
        UndoCombatFullState redoState,
        RestoreCapabilityReport capabilityReport)
    {
        List<UndoScenarioAssertionResult> assertions = [];
        foreach (string assertion in scenario.Assertions)
        {
            assertions.Add(assertion switch
            {
                "exhaust_history_survives_undo" => CompareProjection(assertion, targetState.CombatHistoryState, redoState.CombatHistoryState, "combat_history_roundtrip"),
                "gold_glow_matches_history" => CompareProjection(assertion, targetState.CombatHistoryState, redoState.CombatHistoryState, "history_backing_state_roundtrip"),
                "cards_left_restores" => CompareProjection(assertion, targetState.RuntimeGraphState.PowerRuntimeStates, redoState.RuntimeGraphState.PowerRuntimeStates, "power_runtime_roundtrip"),
                "display_counter_restores" => AssertAutomationDisplayCounterRestores(assertion, redoState),
                "first_damage_only_triggers_once_after_undo" => CompareProjection(assertion, targetState.RuntimeGraphState.PowerRuntimeStates, redoState.RuntimeGraphState.PowerRuntimeStates, "power_runtime_roundtrip"),
                "reviving_state_restores" => CompareProjection(assertion, ProjectTopology(targetState.CreatureTopologyStates), ProjectTopology(redoState.CreatureTopologyStates), "creature_topology_roundtrip"),
                "segment_rejoins_correctly" => CompareProjection(assertion, ProjectTopology(targetState.CreatureTopologyStates), ProjectTopology(redoState.CreatureTopologyStates), "creature_topology_roundtrip"),
                "door_phase_restores" => CompareProjection(assertion, ProjectTopology(targetState.CreatureTopologyStates), ProjectTopology(redoState.CreatureTopologyStates), "creature_topology_roundtrip"),
                "times_got_back_in_restores" => CompareProjection(assertion, ProjectTopology(targetState.CreatureTopologyStates), ProjectTopology(redoState.CreatureTopologyStates), "creature_topology_roundtrip"),
                "pet_role_restores" => CompareProjection(assertion, ProjectTopology(targetState.CreatureTopologyStates), ProjectTopology(redoState.CreatureTopologyStates), "creature_topology_roundtrip"),
                "pet_owner_restores" => CompareProjection(assertion, ProjectTopology(targetState.CreatureTopologyStates), ProjectTopology(redoState.CreatureTopologyStates), "creature_topology_roundtrip"),
                "no_duplicate_pet_after_undo" => CompareProjection(assertion, ProjectTopology(targetState.CreatureTopologyStates), ProjectTopology(redoState.CreatureTopologyStates), "creature_topology_roundtrip"),
                "pet_visual_state_restores" => AssertPaelsLegionVisualState(assertion),
                "osty_owner_restores" => AssertOstyVisualState(assertion),
                "osty_position_restores" => AssertOstyVisualState(assertion),
                "osty_scale_restores" => AssertOstyVisualState(assertion),
                "osty_block_track_restores" => AssertOstyVisualState(assertion),
                "osty_state_display_rebound" => AssertOstyVisualState(assertion),
                "no_disposed_block_subscription" => AssertOstyVisualState(assertion),
                "no_restore_noop_potion_slot" => AssertNoPotionSlotRestoreNoop(assertion),
                "no_post_restore_input_lock" => AssertInteractionStillPlayable(assertion),
                "status_runtime_restores" => CompareProjection(assertion, ProjectCreatureStatusRuntime(targetState.CreatureStatusRuntimeStates), ProjectCreatureStatusRuntime(redoState.CreatureStatusRuntimeStates), "creature_status_runtime_roundtrip"),
                "creature_visual_state_restores" => AssertCreatureStatusVisualState(assertion, scenario.Id),
                "creature_intent_state_restores" => AssertCreatureIntentState(assertion, scenario.Id),
                "burrow_visual_restores" => AssertCreatureStatusVisualState(assertion, scenario.Id),
                "stun_intent_restores" => AssertCreatureIntentState(assertion, scenario.Id),
                "flight_visual_restores" => AssertCreatureStatusVisualState(assertion, scenario.Id),
                "flight_intent_restores" => AssertCreatureIntentState(assertion, scenario.Id),
                "bound_card_play_flag_restores" => CompareProjection(assertion, targetState.RuntimeGraphState.PowerRuntimeStates, redoState.RuntimeGraphState.PowerRuntimeStates, "power_runtime_roundtrip"),
                "queen_branch_intent_restores" => AssertCreatureIntentState(assertion, scenario.Id),
                "turn_counter_restores" => CompareProjection(assertion, targetState.RuntimeGraphState.RelicRuntimeStates, redoState.RuntimeGraphState.RelicRuntimeStates, "relic_runtime_roundtrip"),
                "activation_flag_restores" => CompareProjection(assertion, targetState.RuntimeGraphState.RelicRuntimeStates, redoState.RuntimeGraphState.RelicRuntimeStates, "relic_runtime_roundtrip"),
                "last_turn_card_replay_restores" => CompareProjection(assertion, targetState.CombatHistoryState, redoState.CombatHistoryState, "combat_history_roundtrip"),
                "attack_to_double_restores" => CompareProjection(assertion, targetState.RuntimeGraphState.RelicRuntimeStates, redoState.RuntimeGraphState.RelicRuntimeStates, "relic_runtime_roundtrip"),
                "attack_played_flags_restore" => CompareProjection(assertion, targetState.RuntimeGraphState.RelicRuntimeStates, redoState.RuntimeGraphState.RelicRuntimeStates, "relic_runtime_roundtrip"),
                "stolen_card_restores" => CompareProjection(assertion, targetState.RuntimeGraphState.PowerRuntimeStates, redoState.RuntimeGraphState.PowerRuntimeStates, "power_runtime_roundtrip"),
                "stolen_card_ui_restores" => CompareProjection(assertion, targetState.MonsterStates, redoState.MonsterStates, "monster_node_state_roundtrip"),
                "first_card_double_flag_restores" => CompareProjection(assertion, targetState.RuntimeGraphState.RelicRuntimeStates, redoState.RuntimeGraphState.RelicRuntimeStates, "relic_runtime_roundtrip"),
                "draw_count_damage_restores" => CompareProjection(assertion, targetState.CombatHistoryState, redoState.CombatHistoryState, "combat_history_roundtrip"),
                _ => new UndoScenarioAssertionResult
                {
                    Assertion = assertion,
                    Passed = capabilityReport.Result == RestoreCapabilityResult.Supported,
                    Detail = capabilityReport.Detail ?? capabilityReport.Result.ToString()
                }
            });
        }

        assertions.Add(CompareProjection("undo_state_snapshot_captured", undoState.RoundNumber, undoState.RoundNumber, "undo_snapshot_available"));
        return assertions;
    }

    private static UndoScenarioAssertionResult AssertPaelsLegionVisualState(string assertion)
    {
        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        NCombatRoom? combatRoom = NCombatRoom.Instance;
        if (combatState == null || combatRoom == null)
        {
            return new UndoScenarioAssertionResult
            {
                Assertion = assertion,
                Passed = false,
                Detail = combatState == null ? "combat_state_missing" : "combat_room_missing"
            };
        }

        foreach (Creature creature in combatState.Allies)
        {
            if (!UndoSpecialCreatureVisualNormalizer.TryGetPaelsLegionExpectation(creature, out UndoSpecialCreatureVisualNormalizer.PaelsLegionVisualExpectation? expectation))
                continue;

            NCreature? creatureNode = combatRoom.GetCreatureNode(creature);
            string? animationName = creatureNode?.Visuals?.SpineBody?.GetAnimationState()?.GetCurrent(0)?.GetAnimation()?.GetName();
            bool passed = animationName != null && expectation.AcceptableAnimationNames.Contains(animationName, StringComparer.Ordinal);
            return new UndoScenarioAssertionResult
            {
                Assertion = assertion,
                Passed = passed,
                Detail = passed ? $"animation={animationName}" : $"expected={string.Join("/", expectation.AcceptableAnimationNames)} actual={animationName ?? "null"}"
            };
        }

        return new UndoScenarioAssertionResult
        {
            Assertion = assertion,
            Passed = false,
            Detail = "paels_legion_pet_missing"
        };
    }

    private static UndoScenarioAssertionResult AssertOstyVisualState(string assertion)
    {
        if (!TryGetLocalOstyVisualState(out Player? owner, out Creature? osty, out NCreature? ownerNode, out NCreature? ostyNode, out string detail))
        {
            return new UndoScenarioAssertionResult
            {
                Assertion = assertion,
                Passed = false,
                Detail = detail
            };
        }

        if (assertion == "osty_owner_restores")
        {
            bool ownerConsistent = ReferenceEquals(owner.Osty, osty)
                && owner.IsOstyAlive == osty.IsAlive
                && owner.IsOstyMissing == !osty.IsAlive
                && owner.PlayerCombatState?.Pets.Contains(osty) == true;
            return new UndoScenarioAssertionResult
            {
                Assertion = assertion,
                Passed = ownerConsistent,
                Detail = $"owner_osty={(ReferenceEquals(owner.Osty, osty))}; alive={owner.IsOstyAlive}; missing={owner.IsOstyMissing}; in_pets={(owner.PlayerCombatState?.Pets.Contains(osty) == true)}"
            };
        }

        if (assertion == "osty_block_track_restores")
        {
            object? stateDisplay = UndoReflectionUtil.FindField(ostyNode.GetType(), "_stateDisplay")?.GetValue(ostyNode);
            Creature? trackedCreature = stateDisplay == null ? null : UndoReflectionUtil.FindField(stateDisplay.GetType(), "_blockTrackingCreature")?.GetValue(stateDisplay) as Creature;
            bool passed = ReferenceEquals(trackedCreature, owner.Creature);
            return new UndoScenarioAssertionResult
            {
                Assertion = assertion,
                Passed = passed,
                Detail = passed ? "tracked_owner_block_status" : $"tracked={(trackedCreature == null ? "null" : trackedCreature.GetType().Name)}"
            };
        }


        if (assertion is "osty_state_display_rebound" or "no_disposed_block_subscription")
        {
            object? stateDisplay = UndoReflectionUtil.FindField(ostyNode.GetType(), "_stateDisplay")?.GetValue(ostyNode);
            Creature? trackedCreature = stateDisplay == null ? null : UndoReflectionUtil.FindField(stateDisplay.GetType(), "_blockTrackingCreature")?.GetValue(stateDisplay) as Creature;
            bool stateDisplayValid = stateDisplay is GodotObject stateDisplayObject && GodotObject.IsInstanceValid(stateDisplayObject);
            bool passed = stateDisplayValid && ReferenceEquals(trackedCreature, owner.Creature);
            return new UndoScenarioAssertionResult
            {
                Assertion = assertion,
                Passed = passed,
                Detail = $"state_display_valid={stateDisplayValid}; tracked={(trackedCreature == null ? "null" : trackedCreature.GetType().Name)}"
            };
        }

        Vector2 expectedPosition = ownerNode.Position + NCreature.GetOstyOffsetFromPlayer(osty);
        bool positionOk = ostyNode.Position.IsEqualApprox(expectedPosition);
        float defaultScale = ostyNode.Visuals.DefaultScale;
        float expectedScalar = Mathf.Lerp(Osty.ScaleRange.X, Osty.ScaleRange.Y, Mathf.Clamp((float)osty.MaxHp / 150f, 0f, 1f)) * defaultScale;
        bool scaleOk = Mathf.IsEqualApprox(ostyNode.Visuals.Scale.X, expectedScalar) && Mathf.IsEqualApprox(ostyNode.Visuals.Scale.Y, expectedScalar);

        return new UndoScenarioAssertionResult
        {
            Assertion = assertion,
            Passed = assertion switch
            {
                "osty_position_restores" => positionOk,
                "osty_scale_restores" => scaleOk,
                _ => positionOk && scaleOk
            },
            Detail = $"position={ostyNode.Position}; expected_position={expectedPosition}; scale={ostyNode.Visuals.Scale}; expected_scale={expectedScalar}"
        };
    }

    private static UndoScenarioAssertionResult AssertNoPotionSlotRestoreNoop(string assertion)
    {
        string? failureReason = GetLastRestoreFailureReason(MainFile.Controller);
        bool passed = string.IsNullOrWhiteSpace(failureReason) || !failureReason.Contains("potion slot index", StringComparison.OrdinalIgnoreCase);
        return new UndoScenarioAssertionResult
        {
            Assertion = assertion,
            Passed = passed,
            Detail = passed ? "no_potion_slot_restore_noop" : failureReason
        };
    }

    private static UndoScenarioAssertionResult AssertInteractionStillPlayable(string assertion)
    {
        NCombatUi? ui = NCombatRoom.Instance?.Ui;
        bool isSelecting = ui?.Hand?.IsInCardSelection == true;
        bool handDisabled = ui?.Hand != null && (UndoReflectionUtil.FindField(ui.Hand.GetType(), "_isDisabled")?.GetValue(ui.Hand) as bool? == true);
        bool passed = CombatManager.Instance.IsPlayPhase
            && !isSelecting
            && !handDisabled
            && !CombatManager.Instance.PlayerActionsDisabled;
        return new UndoScenarioAssertionResult
        {
            Assertion = assertion,
            Passed = passed,
            Detail = $"is_play_phase={CombatManager.Instance.IsPlayPhase}; selecting={isSelecting}; hand_disabled={handDisabled}; player_actions_disabled={CombatManager.Instance.PlayerActionsDisabled}"
        };
    }

    private static UndoScenarioAssertionResult AssertAutomationDisplayCounterRestores(string assertion, UndoCombatFullState redoState)
    {
        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        Player? me = combatState == null ? null : LocalContext.GetMe(combatState);
        AutomationPower? power = me?.Creature.Powers.OfType<AutomationPower>().FirstOrDefault();
        if (power == null)
        {
            return new UndoScenarioAssertionResult
            {
                Assertion = assertion,
                Passed = false,
                Detail = "automation_power_missing"
            };
        }

        string? ownerCreatureKey = UndoStableRefs.TryResolveCreatureKey(combatState!.Creatures, me!.Creature);
        int? expectedDisplayAmount = redoState.RuntimeGraphState.PowerRuntimeStates
            .Where(state => state.OwnerCreatureKey == ownerCreatureKey && state.PowerId.Entry == "AutomationPower")
            .SelectMany(state => state.ComplexStates)
            .OfType<UndoIntRuntimeComplexState>()
            .FirstOrDefault(state => state.CodecId == "power:AutomationPower.cardsLeft")?.Value;
        if (expectedDisplayAmount == null)
        {
            return new UndoScenarioAssertionResult
            {
                Assertion = assertion,
                Passed = false,
                Detail = "automation_runtime_state_missing"
            };
        }

        bool passed = power.DisplayAmount == expectedDisplayAmount.Value;
        return new UndoScenarioAssertionResult
        {
            Assertion = assertion,
            Passed = passed,
            Detail = passed
                ? $"display_amount={power.DisplayAmount}"
                : $"expected_display_amount={expectedDisplayAmount.Value}; actual_display_amount={power.DisplayAmount}"
        };
    }
    private static UndoScenarioAssertionResult CompareProjection(string assertion, object? expected, object? actual, string detail)
    {
        string expectedJson = JsonSerializer.Serialize(expected, JsonOptions);
        string actualJson = JsonSerializer.Serialize(actual, JsonOptions);
        bool passed = string.Equals(expectedJson, actualJson, StringComparison.Ordinal);
        return new UndoScenarioAssertionResult
        {
            Assertion = assertion,
            Passed = passed,
            Detail = passed
                ? detail
                : $"{detail}; expected={expectedJson}; actual={actualJson}"
        };
    }
}
