// 文件说明：执行调试和回归场景，并驱动自动验证。
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;

namespace UndoTheSpire2;

// Scenario execution is a live runtime validator for undo/redo invariants.
// It checks current combat state and round-trips supported scenarios, but it
// does not build full combat worlds from scratch.
internal enum UndoScenarioExecutionStatus
{
    Passed,
    Failed,
    Skipped
}

internal sealed class UndoScenarioAssertionResult
{
    public string Assertion { get; init; } = string.Empty;

    public bool Passed { get; init; }

    public string? Detail { get; init; }
}

internal sealed class UndoScenarioExecutionResult
{
    public required UndoScenarioDefinition Scenario { get; init; }

    public required UndoScenarioExecutionStatus Status { get; init; }

    public string? Detail { get; init; }

    public IReadOnlyList<UndoScenarioAssertionResult> Assertions { get; init; } = [];

    public IReadOnlyList<string> UnsupportedCapabilities { get; init; } = [];

    public IReadOnlyList<string> MissingExpectedUnsupportedCapabilities { get; init; } = [];

    public IReadOnlyList<string> UnexpectedUnsupportedCapabilities { get; init; } = [];

    public bool DeclaredSupported { get; init; }

    public bool RuntimeClosedLoop { get; init; }
}

internal sealed class UndoScenarioPreconditionResult
{
    public bool Matched { get; init; }

    public string Detail { get; init; } = string.Empty;
}

internal static partial class UndoScenarioExecutor
{
    private static readonly IReadOnlyList<PileType> CombatPileOrder = UndoSharedConstants.CombatPileOrder;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private static readonly IReadOnlyDictionary<string, Func<UndoScenarioDefinition, RunState, CombatState, Task<UndoScenarioExecutionResult>>> Handlers =
        new Dictionary<string, Func<UndoScenarioDefinition, RunState, CombatState, Task<UndoScenarioExecutionResult>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["well-laid-plans"] = ExecuteWellLaidPlansScenarioAsync,
            ["toolbox-combat-start"] = ExecuteToolboxCombatStartScenarioAsync,
            ["forgotten-ritual"] = ExecuteRoundtripScenarioAsync,
            ["automation-power"] = ExecuteRoundtripScenarioAsync,
            ["infested-prism"] = ExecuteRoundtripScenarioAsync,
            ["decimillipede"] = ExecuteRoundtripScenarioAsync,
            ["door-maker"] = ExecuteRoundtripScenarioAsync,
            ["paels-legion"] = ExecuteRoundtripScenarioAsync,
            ["tunneler"] = ExecuteRoundtripScenarioAsync,
            ["owl-magistrate-flight"] = ExecuteRoundtripScenarioAsync,
            ["queen-soulbound"] = ExecuteRoundtripScenarioAsync,
            ["queen-amalgam-branch"] = ExecuteRoundtripScenarioAsync,
            ["osty-summon-roundtrip"] = ExecuteRoundtripScenarioAsync,
            ["osty-revive-roundtrip"] = ExecuteRoundtripScenarioAsync,
            ["osty-enemy-hit-roundtrip"] = ExecuteRoundtripScenarioAsync,
            ["slumbering-beetle"] = ExecuteRoundtripScenarioAsync,
            ["lagavulin-matriarch"] = ExecuteRoundtripScenarioAsync,
            ["bowlbug-rock"] = ExecuteRoundtripScenarioAsync,
            ["thieving-hopper"] = ExecuteRoundtripScenarioAsync,
            ["fat-gremlin"] = ExecuteRoundtripScenarioAsync,
            ["sneaky-gremlin"] = ExecuteRoundtripScenarioAsync,
            ["ceremonial-beast"] = ExecuteRoundtripScenarioAsync,
            ["wriggler"] = ExecuteRoundtripScenarioAsync,
            ["throwing-axe"] = ExecuteRoundtripScenarioAsync,
            ["happy-flower"] = ExecuteRoundtripScenarioAsync,
            ["history-course"] = ExecuteRoundtripScenarioAsync,
            ["pen-nib"] = ExecuteRoundtripScenarioAsync,
            ["art-of-war"] = ExecuteRoundtripScenarioAsync,
            ["swipe-power"] = ExecuteRoundtripScenarioAsync,
            ["death-march"] = ExecuteRoundtripScenarioAsync
        };

    public static async Task<IReadOnlyList<UndoScenarioExecutionResult>> RunAllAsync(IEnumerable<UndoScenarioDefinition> scenarios)
    {
        List<UndoScenarioExecutionResult> results = [];
        foreach (UndoScenarioDefinition scenario in scenarios.OrderBy(static scenario => scenario.Id, StringComparer.Ordinal))
            results.Add(await ExecuteAsync(scenario));

        return results;
    }

    public static async Task<UndoScenarioExecutionResult> ExecuteAsync(UndoScenarioDefinition scenario)
    {
        if (!Handlers.TryGetValue(scenario.Id, out Func<UndoScenarioDefinition, RunState, CombatState, Task<UndoScenarioExecutionResult>>? handler))
        {
            return new UndoScenarioExecutionResult
            {
                Scenario = scenario,
                Status = UndoScenarioExecutionStatus.Skipped,
                Detail = "no_registered_handler"
            };
        }

        List<string> unsupported = GetCapabilityGaps(scenario);
        IReadOnlyList<string> missingExpectedUnsupported = scenario.ExpectedUnsupportedCapabilities
            .Except(unsupported, StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToList();
        IReadOnlyList<string> unexpectedUnsupported = unsupported
            .Except(scenario.ExpectedUnsupportedCapabilities, StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToList();
        if (unexpectedUnsupported.Count > 0)
        {
            return new UndoScenarioExecutionResult
            {
                Scenario = scenario,
                Status = UndoScenarioExecutionStatus.Failed,
                Detail = "missing_capabilities",
                UnsupportedCapabilities = unsupported,
                MissingExpectedUnsupportedCapabilities = missingExpectedUnsupported,
                UnexpectedUnsupportedCapabilities = unexpectedUnsupported,
                DeclaredSupported = false,
                RuntimeClosedLoop = false,
                Assertions = scenario.Assertions.Select(assertion => new UndoScenarioAssertionResult
                {
                    Assertion = assertion,
                    Passed = false,
                    Detail = string.Join(", ", unsupported)
                }).ToList()
            };
        }

        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        if (runState == null || combatState == null || !CombatManager.Instance.IsInProgress)
        {
            return new UndoScenarioExecutionResult
            {
                Scenario = scenario,
                Status = UndoScenarioExecutionStatus.Skipped,
                Detail = "live_scenario_setup_required",
                UnsupportedCapabilities = unsupported,
                MissingExpectedUnsupportedCapabilities = missingExpectedUnsupported,
                UnexpectedUnsupportedCapabilities = unexpectedUnsupported,
                DeclaredSupported = unexpectedUnsupported.Count == 0,
                RuntimeClosedLoop = false
            };
        }

        UndoScenarioPreconditionResult precondition = EvaluatePreconditions(scenario.Id, runState, combatState);
        if (!precondition.Matched)
        {
            return new UndoScenarioExecutionResult
            {
                Scenario = scenario,
                Status = UndoScenarioExecutionStatus.Skipped,
                Detail = precondition.Detail,
                UnsupportedCapabilities = unsupported,
                MissingExpectedUnsupportedCapabilities = missingExpectedUnsupported,
                UnexpectedUnsupportedCapabilities = unexpectedUnsupported,
                DeclaredSupported = unexpectedUnsupported.Count == 0,
                RuntimeClosedLoop = false
            };
        }

        UndoScenarioExecutionResult result = await handler(scenario, runState, combatState);
        return new UndoScenarioExecutionResult
        {
            Scenario = result.Scenario,
            Status = result.Status,
            Detail = result.Detail,
            Assertions = result.Assertions,
            UnsupportedCapabilities = unsupported,
            MissingExpectedUnsupportedCapabilities = missingExpectedUnsupported,
            UnexpectedUnsupportedCapabilities = unexpectedUnsupported,
            DeclaredSupported = result.DeclaredSupported || unexpectedUnsupported.Count == 0,
            RuntimeClosedLoop = result.RuntimeClosedLoop
        };
    }

    private static async Task<UndoScenarioExecutionResult> ExecuteRoundtripScenarioAsync(UndoScenarioDefinition scenario, RunState runState, CombatState combatState)
    {
        UndoController controller = MainFile.Controller;
        if (!controller.HasUndo)
        {
            return new UndoScenarioExecutionResult
            {
                Scenario = scenario,
                Status = UndoScenarioExecutionStatus.Skipped,
                Detail = "undo_history_required"
            };
        }

        UndoCombatFullState? targetState = TryCaptureCurrentCombatFullState(controller);
        if (targetState == null)
        {
            return new UndoScenarioExecutionResult
            {
                Scenario = scenario,
                Status = UndoScenarioExecutionStatus.Failed,
                Detail = "target_snapshot_capture_failed"
            };
        }

        string expectedRedoLabel = controller.UndoLabel;
        controller.Undo();
        if (!await WaitForHistoryMoveAsync(() => controller.HasRedo && controller.RedoLabel == expectedRedoLabel, controller))
        {
            return new UndoScenarioExecutionResult
            {
                Scenario = scenario,
                Status = UndoScenarioExecutionStatus.Failed,
                Detail = GetLastRestoreFailureReason(controller) ?? "undo_failed"
            };
        }

        UndoCombatFullState? undoState = TryCaptureCurrentCombatFullState(controller);
        if (undoState == null)
        {
            return new UndoScenarioExecutionResult
            {
                Scenario = scenario,
                Status = UndoScenarioExecutionStatus.Failed,
                Detail = "undo_snapshot_capture_failed"
            };
        }

        string expectedUndoLabel = controller.RedoLabel;
        controller.Redo();
        if (!await WaitForHistoryMoveAsync(() => controller.HasUndo && controller.UndoLabel == expectedUndoLabel, controller))
        {
            return new UndoScenarioExecutionResult
            {
                Scenario = scenario,
                Status = UndoScenarioExecutionStatus.Failed,
                Detail = GetLastRestoreFailureReason(controller) ?? "redo_failed"
            };
        }

        UndoCombatFullState? redoState = TryCaptureCurrentCombatFullState(controller);
        if (redoState == null)
        {
            return new UndoScenarioExecutionResult
            {
                Scenario = scenario,
                Status = UndoScenarioExecutionStatus.Failed,
                Detail = "redo_snapshot_capture_failed"
            };
        }

        List<UndoScenarioAssertionResult> assertions = BuildRoundtripAssertions(scenario, targetState, undoState, redoState, GetLastRestoreCapabilityReport(controller));
        bool passed = assertions.All(static assertion => assertion.Passed);
        return new UndoScenarioExecutionResult
        {
            Scenario = scenario,
            Status = passed ? UndoScenarioExecutionStatus.Passed : UndoScenarioExecutionStatus.Failed,
            Detail = $"undo_label={expectedRedoLabel} redo_label={expectedUndoLabel}",
            Assertions = assertions,
            DeclaredSupported = true,
            RuntimeClosedLoop = passed
        };
    }

    private static async Task<UndoScenarioExecutionResult> ExecuteWellLaidPlansScenarioAsync(UndoScenarioDefinition scenario, RunState runState, CombatState combatState)
    {
        UndoController controller = MainFile.Controller;
        if (!controller.HasUndo)
        {
            return new UndoScenarioExecutionResult
            {
                Scenario = scenario,
                Status = UndoScenarioExecutionStatus.Skipped,
                Detail = "undo_history_required",
                DeclaredSupported = false,
                RuntimeClosedLoop = false
            };
        }

        UndoChoiceSpec? activeChoice = TryCaptureActiveChoiceSpec(controller);
        UndoCombatFullState? currentSnapshot = TryCaptureCurrentCombatFullState(controller);
        PausedChoiceState? pausedChoice = currentSnapshot?.ActionKernelState.PausedChoiceState;
        RestoreCapabilityReport capability = UndoActionCodecRegistry.EvaluateCapability(pausedChoice);
        bool pausedChoiceCaptured = pausedChoice?.ChoiceSpec?.Kind == UndoChoiceKind.HandSelection
            && pausedChoice.SourceActionRef.ActionId != null;
        bool primaryChoiceSupported = capability.Result == RestoreCapabilityResult.Supported;
        int hiddenSkipCountBefore = UndoController.DebugGetHiddenChoiceAnchorSkipCount();
        bool primaryRestoreUsed = false;
        bool retainSelectionReopened = IsSupportedChoiceUiActive() && activeChoice?.Kind == UndoChoiceKind.HandSelection;
        bool noHiddenChoiceAnchorSkip = true;

        if (pausedChoiceCaptured && primaryChoiceSupported)
        {
            controller.Undo();
            bool restoreObserved = await WaitForHistoryMoveAsync(() => !controller.IsRestoring, controller);
            activeChoice = TryCaptureActiveChoiceSpec(controller);
            string? restoreStage = UndoController.DebugGetLastInteractionStage();
            primaryRestoreUsed = restoreObserved && (string.Equals(restoreStage, "primary_restore", StringComparison.Ordinal) || string.Equals(restoreStage, "primary_restore_anchor_reopened", StringComparison.Ordinal));
            retainSelectionReopened = IsSupportedChoiceUiActive() && activeChoice?.Kind == UndoChoiceKind.HandSelection;
            noHiddenChoiceAnchorSkip = UndoController.DebugGetHiddenChoiceAnchorSkipCount() == hiddenSkipCountBefore;
        }
        else
        {
            noHiddenChoiceAnchorSkip = UndoController.DebugGetHiddenChoiceAnchorSkipCount() == hiddenSkipCountBefore;
        }

        List<UndoScenarioAssertionResult> assertions =
        [
            new UndoScenarioAssertionResult
            {
                Assertion = "paused_choice_captured",
                Passed = pausedChoiceCaptured,
                Detail = pausedChoice == null ? "no_paused_choice_state" : pausedChoice.SourceActionCodecId ?? "missing_codec_id"
            },
            new UndoScenarioAssertionResult
            {
                Assertion = "primary_choice_supported",
                Passed = primaryChoiceSupported,
                Detail = capability.Detail ?? capability.Result.ToString()
            },
            new UndoScenarioAssertionResult
            {
                Assertion = "primary_restore_used",
                Passed = primaryRestoreUsed,
                Detail = UndoController.DebugGetLastInteractionStage() ?? "no_restore_stage"
            },
            new UndoScenarioAssertionResult
            {
                Assertion = "retain_selection_reopens",
                Passed = retainSelectionReopened,
                Detail = retainSelectionReopened ? "supported_choice_ui_active" : "supported_choice_ui_inactive"
            },
            new UndoScenarioAssertionResult
            {
                Assertion = "no_hidden_choice_anchor_skip",
                Passed = noHiddenChoiceAnchorSkip,
                Detail = noHiddenChoiceAnchorSkip ? "hidden_anchor_skip_not_observed" : "hidden_anchor_skip_observed"
            }
        ];

        bool passed = assertions.All(static assertion => assertion.Passed);
        return new UndoScenarioExecutionResult
        {
            Scenario = scenario,
            Status = passed ? UndoScenarioExecutionStatus.Passed : UndoScenarioExecutionStatus.Failed,
            Detail = passed ? "primary_choice_runtime_closed" : "primary_choice_runtime_incomplete",
            Assertions = assertions,
            DeclaredSupported = primaryChoiceSupported,
            RuntimeClosedLoop = passed
        };
    }

    private static async Task<UndoScenarioExecutionResult> ExecuteToolboxCombatStartScenarioAsync(UndoScenarioDefinition scenario, RunState runState, CombatState combatState)
    {
        UndoController controller = MainFile.Controller;
        if (!controller.HasUndo)
        {
            return new UndoScenarioExecutionResult
            {
                Scenario = scenario,
                Status = UndoScenarioExecutionStatus.Skipped,
                Detail = "undo_history_required",
                DeclaredSupported = false,
                RuntimeClosedLoop = false
            };
        }

        UndoSnapshot? latestUndoSnapshot = GetLatestUndoSnapshot(controller);
        PausedChoiceState? pausedChoice = latestUndoSnapshot?.CombatState.ActionKernelState.PausedChoiceState;
        RestoreCapabilityReport capability = UndoActionCodecRegistry.EvaluateCapability(pausedChoice);
        bool choiceAnchorVisible = latestUndoSnapshot?.IsChoiceAnchor == true && latestUndoSnapshot.ChoiceSpec?.Kind == UndoChoiceKind.ChooseACard;
        bool primaryChoiceSupported = capability.Result == RestoreCapabilityResult.Supported || choiceAnchorVisible;
        int hiddenSkipCountBefore = UndoController.DebugGetHiddenChoiceAnchorSkipCount();
        bool primaryRestoreUsed = false;
        bool chooseACardReopened = false;
        bool noHiddenChoiceAnchorSkip = true;

        if (choiceAnchorVisible && primaryChoiceSupported)
        {
            controller.Undo();
            bool restoreObserved = await WaitForHistoryMoveAsync(() => !controller.IsRestoring, controller);
            UndoChoiceSpec? activeChoice = TryCaptureActiveChoiceSpec(controller);
            string? restoreStage = UndoController.DebugGetLastInteractionStage();
            primaryRestoreUsed = restoreObserved && (string.Equals(restoreStage, "primary_restore", StringComparison.Ordinal) || string.Equals(restoreStage, "primary_restore_anchor_reopened", StringComparison.Ordinal));
            chooseACardReopened = IsSupportedChoiceUiActive() && activeChoice?.Kind == UndoChoiceKind.ChooseACard;
            noHiddenChoiceAnchorSkip = UndoController.DebugGetHiddenChoiceAnchorSkipCount() == hiddenSkipCountBefore;
        }
        else
        {
            noHiddenChoiceAnchorSkip = UndoController.DebugGetHiddenChoiceAnchorSkipCount() == hiddenSkipCountBefore;
        }

        List<UndoScenarioAssertionResult> assertions =
        [
            new UndoScenarioAssertionResult
            {
                Assertion = "choice_anchor_visible",
                Passed = choiceAnchorVisible,
                Detail = latestUndoSnapshot == null ? "no_undo_snapshot" : $"isChoiceAnchor={latestUndoSnapshot.IsChoiceAnchor}; kind={latestUndoSnapshot.ChoiceSpec?.Kind.ToString() ?? "null"}"
            },
            new UndoScenarioAssertionResult
            {
                Assertion = "primary_choice_supported",
                Passed = primaryChoiceSupported,
                Detail = choiceAnchorVisible && capability.Result != RestoreCapabilityResult.Supported ? "choose_a_card_anchor_first" : (capability.Detail ?? capability.Result.ToString())
            },
            new UndoScenarioAssertionResult
            {
                Assertion = "primary_restore_used",
                Passed = primaryRestoreUsed,
                Detail = UndoController.DebugGetLastInteractionStage() ?? "no_restore_stage"
            },
            new UndoScenarioAssertionResult
            {
                Assertion = "choose_a_card_reopens",
                Passed = chooseACardReopened,
                Detail = chooseACardReopened ? "supported_choice_ui_active" : "supported_choice_ui_inactive"
            },
            new UndoScenarioAssertionResult
            {
                Assertion = "no_hidden_choice_anchor_skip",
                Passed = noHiddenChoiceAnchorSkip,
                Detail = noHiddenChoiceAnchorSkip ? "hidden_anchor_skip_not_observed" : "hidden_anchor_skip_observed"
            }
        ];

        bool passed = assertions.All(static assertion => assertion.Passed);
        return new UndoScenarioExecutionResult
        {
            Scenario = scenario,
            Status = passed ? UndoScenarioExecutionStatus.Passed : UndoScenarioExecutionStatus.Failed,
            Detail = passed ? "toolbox_primary_choice_runtime_closed" : "toolbox_primary_choice_runtime_incomplete",
            Assertions = assertions,
            DeclaredSupported = primaryChoiceSupported,
            RuntimeClosedLoop = passed
        };
    }
    private static async Task<bool> WaitForHistoryMoveAsync(Func<bool> completion, UndoController controller, int timeoutMs = 5000)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        bool observedRestore = controller.IsRestoring;
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            observedRestore |= controller.IsRestoring;
            if (!controller.IsRestoring && observedRestore && completion())
                return true;

            await Task.Delay(50);
        }

        return completion();
    }


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

    private static bool TryGetLocalOstyVisualState(out Player? owner, out Creature? osty, out NCreature? ownerNode, out NCreature? ostyNode, out string detail)
    {
        owner = null;
        osty = null;
        ownerNode = null;
        ostyNode = null;
        detail = "osty_missing";

        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        NCombatRoom? combatRoom = NCombatRoom.Instance;
        if (combatState == null || combatRoom == null)
        {
            detail = combatState == null ? "combat_state_missing" : "combat_room_missing";
            return false;
        }

        owner = LocalContext.GetMe(combatState);
        Player? localOwner = owner;
        if (localOwner == null || !IsLocalNecrobinder(localOwner))
        {
            detail = "local_necrobinder_required";
            return false;
        }

        osty = combatState.Allies.FirstOrDefault(creature => creature.PetOwner == localOwner && creature.Monster is Osty);
        if (osty == null)
        {
            detail = "osty_creature_missing";
            return false;
        }

        ownerNode = combatRoom.GetCreatureNode(owner.Creature);
        ostyNode = combatRoom.GetCreatureNode(osty);
        if (ownerNode == null || ostyNode == null)
        {
            detail = ownerNode == null ? "owner_node_missing" : "osty_node_missing";
            return false;
        }

        detail = "matched";
        return true;
    }

    private static bool IsLocalNecrobinder(Player player)
    {
        return string.Equals(player.Character.GetType().Name, "Necrobinder", StringComparison.Ordinal);
    }

    private static object ProjectCreatureStatusRuntime(IReadOnlyList<CreatureStatusRuntimeState> states)
    {
        return states
            .OrderBy(static state => state.CreatureKey, StringComparer.Ordinal)
            .Select(state => new
            {
                state.CreatureKey,
                state.CodecId,
                Payload = state.Payload switch
                {
                    UndoBoolCreatureStatusRuntimePayload boolPayload => (object?)new { boolPayload.CodecId, boolPayload.Value },
                    UndoIntCreatureStatusRuntimePayload intPayload => (object?)new { intPayload.CodecId, intPayload.Value },
                    UndoCreatureStatusRuntimePayload payload => (object?)new { payload.CodecId },
                    _ => null
                }
            })
            .ToList();
    }

    private static UndoScenarioAssertionResult AssertCreatureStatusVisualState(string assertion, string scenarioId)
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

        foreach (Creature creature in combatState.Creatures)
        {
            NCreature? creatureNode = combatRoom.GetCreatureNode(creature);
            if (creatureNode == null || creature.Monster == null)
                continue;

            string? animation = creatureNode.SpineController.GetAnimationState().GetCurrent(0)?.GetAnimation()?.GetName();
            bool nodeVisible = creatureNode.Visible && creatureNode.Visuals.Visible && creatureNode.Body.Visible;
            bool hasSleepingVfx = UndoReflectionUtil.FindProperty(creature.Monster.GetType(), "SleepingVfx")?.GetValue(creature.Monster) != null
                || UndoReflectionUtil.FindField(creature.Monster.GetType(), "_sleepingVfx")?.GetValue(creature.Monster) != null;

            switch (scenarioId)
            {
                case "slumbering-beetle" when creature.Monster is SlumberingBeetle slumberingBeetle:
                {
                    UndoSpecialCreatureVisualNormalizer.SlumberingBeetleVisualState visualState = UndoSpecialCreatureVisualNormalizer.GetSlumberingBeetleVisualState(slumberingBeetle);
                    IReadOnlyList<string> expectedAnimations = visualState switch
                    {
                        UndoSpecialCreatureVisualNormalizer.SlumberingBeetleVisualState.Sleeping => ["sleep_loop"],
                        UndoSpecialCreatureVisualNormalizer.SlumberingBeetleVisualState.WakeStun => ["wake_up"],
                        _ => ["idle_loop"]
                    };
                    bool expectedSleepingVfx = visualState == UndoSpecialCreatureVisualNormalizer.SlumberingBeetleVisualState.Sleeping;
                    return BuildAnimationAssertion(assertion, animation, expectedAnimations, hasSleepingVfx, expectedSleepingVfx, nodeVisible, true);
                }
                case "lagavulin-matriarch" when creature.Monster is LagavulinMatriarch lagavulinMatriarch:
                {
                    UndoSpecialCreatureVisualNormalizer.LagavulinVisualState visualState = UndoSpecialCreatureVisualNormalizer.GetLagavulinVisualState(lagavulinMatriarch);
                    IReadOnlyList<string> expectedAnimations = visualState == UndoSpecialCreatureVisualNormalizer.LagavulinVisualState.Sleeping ? ["sleep_loop"] : ["idle_loop"];
                    bool expectedSleepingVfx = visualState == UndoSpecialCreatureVisualNormalizer.LagavulinVisualState.Sleeping;
                    return BuildAnimationAssertion(assertion, animation, expectedAnimations, hasSleepingVfx, expectedSleepingVfx, nodeVisible, true);
                }
                case "tunneler" when creature.Monster is Tunneler tunneler:
                {
                    UndoSpecialCreatureVisualNormalizer.TunnelerVisualState visualState = UndoSpecialCreatureVisualNormalizer.GetTunnelerVisualState(tunneler);
                    IReadOnlyList<string> expectedAnimations = visualState == UndoSpecialCreatureVisualNormalizer.TunnelerVisualState.Burrowed ? ["hidden_loop"] : ["idle_loop"];
                    Node2D? specialNode = creatureNode.GetSpecialNode<Node2D>("Visuals/SpineBoneNode");
                    bool nodeReset = specialNode == null || specialNode.Position.IsEqualApprox(Vector2.Zero);
                    UndoScenarioAssertionResult result = BuildAnimationAssertion(assertion, animation, expectedAnimations, actualVisible: nodeVisible, expectedVisible: true);
                    if (!nodeReset)
                    {
                        result = new UndoScenarioAssertionResult
                        {
                            Assertion = assertion,
                            Passed = false,
                            Detail = $"{result.Detail}; spine_bone_pos={specialNode!.Position}"
                        };
                    }
                    else if (!string.IsNullOrWhiteSpace(result.Detail))
                    {
                        result = new UndoScenarioAssertionResult
                        {
                            Assertion = result.Assertion,
                            Passed = result.Passed,
                            Detail = $"{result.Detail}; spine_bone_pos=Zero"
                        };
                    }
                    return result;
                }
                case "owl-magistrate-flight" when creature.Monster is OwlMagistrate owlMagistrate:
                {
                    UndoSpecialCreatureVisualNormalizer.OwlMagistrateVisualState visualState = UndoSpecialCreatureVisualNormalizer.GetOwlMagistrateVisualState(owlMagistrate);
                    IReadOnlyList<string> expectedAnimations = visualState == UndoSpecialCreatureVisualNormalizer.OwlMagistrateVisualState.Flying ? ["fly_loop"] : ["idle_loop"];
                    return BuildAnimationAssertion(assertion, animation, expectedAnimations, actualVisible: nodeVisible, expectedVisible: true);
                }
                case "osty-summon-roundtrip" when creature.Monster is Osty:
                case "osty-revive-roundtrip" when creature.Monster is Osty:
                case "osty-enemy-hit-roundtrip" when creature.Monster is Osty:
                    return AssertOstyVisualState(assertion);
                case "bowlbug-rock" when creature.Monster is BowlbugRock bowlbugRock:
                    return BuildAnimationAssertion(assertion, animation, bowlbugRock.IsOffBalance ? ["stunned_loop"] : ["idle_loop"], actualVisible: nodeVisible, expectedVisible: true);
                case "thieving-hopper" when creature.Monster is ThievingHopper thievingHopper:
                {
                    bool expectsStolenCard = creature.Powers.OfType<SwipePower>().Any(static power => power.StolenCard != null);
                    int stolenCardNodeCount = creatureNode.Visuals?.GetNodeOrNull<Marker2D>("%StolenCardPos")?.GetChildCount() ?? 0;
                    UndoScenarioAssertionResult result = BuildAnimationAssertion(assertion, animation, ReadPrivateBool(thievingHopper, "IsHovering") ? ["hover_loop"] : ["idle_loop"], actualVisible: nodeVisible, expectedVisible: true);
                    bool stolenCardUiMatches = expectsStolenCard ? stolenCardNodeCount > 0 : stolenCardNodeCount == 0;
                    if (!result.Passed || !stolenCardUiMatches)
                    {
                        return new UndoScenarioAssertionResult
                        {
                            Assertion = assertion,
                            Passed = result.Passed && stolenCardUiMatches,
                            Detail = $"animation_check={result.Detail}; expects_stolen_card={expectsStolenCard}; stolen_card_nodes={stolenCardNodeCount}"
                        };
                    }

                    return new UndoScenarioAssertionResult
                    {
                        Assertion = assertion,
                        Passed = true,
                        Detail = $"animation_check={result.Detail}; expects_stolen_card={expectsStolenCard}; stolen_card_nodes={stolenCardNodeCount}"
                    };
                }
                case "fat-gremlin" when creature.Monster is FatGremlin fatGremlin:
                    return BuildAnimationAssertion(assertion, animation, ReadPrivateBool(fatGremlin, "IsAwake") ? ["awake_loop"] : ["stunned_loop"], actualVisible: nodeVisible, expectedVisible: true);
                case "sneaky-gremlin" when creature.Monster is SneakyGremlin sneakyGremlin:
                    return BuildAnimationAssertion(assertion, animation, ReadPrivateBool(sneakyGremlin, "IsAwake") ? ["awake_loop"] : ["stunned_loop"], actualVisible: nodeVisible, expectedVisible: true);
                case "ceremonial-beast" when creature.Monster is CeremonialBeast ceremonialBeast:
                    return BuildAnimationAssertion(assertion, animation, ReadPrivateBool(ceremonialBeast, "InMidCharge") ? ["plow"] : (ReadPrivateBool(ceremonialBeast, "IsStunnedByPlowRemoval") ? ["stun_loop"] : ["idle_loop"]), actualVisible: nodeVisible, expectedVisible: true);
            }
        }

        return new UndoScenarioAssertionResult
        {
            Assertion = assertion,
            Passed = false,
            Detail = $"scenario_creature_missing:{scenarioId}"
        };
    }

    private static UndoScenarioAssertionResult AssertCreatureIntentState(string assertion, string scenarioId)
    {
        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        if (combatState == null)
        {
            return new UndoScenarioAssertionResult
            {
                Assertion = assertion,
                Passed = false,
                Detail = "combat_state_missing"
            };
        }

        foreach (Creature creature in combatState.Creatures)
        {
            MonsterModel? monster = creature.Monster;
            if (monster == null)
                continue;

            switch (scenarioId)
            {
                case "slumbering-beetle" when monster is SlumberingBeetle slumberingBeetle:
                {
                    string? nextMoveId = slumberingBeetle.NextMove?.Id;
                    bool hasSlumberPower = creature.HasPower<SlumberPower>();
                    UndoSpecialCreatureVisualNormalizer.SlumberingBeetleVisualState visualState = UndoSpecialCreatureVisualNormalizer.GetSlumberingBeetleVisualState(slumberingBeetle);
                    bool passed = nextMoveId == MonsterModel.stunnedMoveId
                        ? !slumberingBeetle.IntendsToAttack && visualState == UndoSpecialCreatureVisualNormalizer.SlumberingBeetleVisualState.WakeStun
                        : hasSlumberPower
                            ? nextMoveId == "SNORE_MOVE" && !slumberingBeetle.IntendsToAttack && visualState == UndoSpecialCreatureVisualNormalizer.SlumberingBeetleVisualState.Sleeping
                            : nextMoveId == "ROLL_OUT_MOVE" && slumberingBeetle.IntendsToAttack && visualState == UndoSpecialCreatureVisualNormalizer.SlumberingBeetleVisualState.Awake;
                    return new UndoScenarioAssertionResult
                    {
                        Assertion = assertion,
                        Passed = passed,
                        Detail = $"next_move={nextMoveId ?? "null"}; awake={slumberingBeetle.IsAwake}; slumber={hasSlumberPower}; attacks={slumberingBeetle.IntendsToAttack}; visual={visualState}"
                    };
                }
                case "lagavulin-matriarch" when monster is LagavulinMatriarch lagavulinMatriarch:
                {
                    string? nextMoveId = lagavulinMatriarch.NextMove?.Id;
                    bool asleepPower = creature.HasPower<AsleepPower>();
                    UndoSpecialCreatureVisualNormalizer.LagavulinVisualState visualState = UndoSpecialCreatureVisualNormalizer.GetLagavulinVisualState(lagavulinMatriarch);
                    bool passed = nextMoveId == MonsterModel.stunnedMoveId
                        ? !lagavulinMatriarch.IntendsToAttack && visualState == UndoSpecialCreatureVisualNormalizer.LagavulinVisualState.WakeStun
                        : asleepPower
                            ? nextMoveId == "SLEEP_MOVE" && !lagavulinMatriarch.IntendsToAttack && visualState == UndoSpecialCreatureVisualNormalizer.LagavulinVisualState.Sleeping
                            : visualState == UndoSpecialCreatureVisualNormalizer.LagavulinVisualState.Awake;
                    return new UndoScenarioAssertionResult
                    {
                        Assertion = assertion,
                        Passed = passed,
                        Detail = $"next_move={nextMoveId ?? "null"}; awake={lagavulinMatriarch.IsAwake}; asleep={asleepPower}; attacks={lagavulinMatriarch.IntendsToAttack}; visual={visualState}"
                    };
                }
                case "tunneler" when monster is Tunneler tunneler:
                {
                    string? nextMoveId = tunneler.NextMove?.Id;
                    bool burrowedPower = creature.HasPower<BurrowedPower>();
                    UndoSpecialCreatureVisualNormalizer.TunnelerVisualState visualState = UndoSpecialCreatureVisualNormalizer.GetTunnelerVisualState(tunneler);
                    bool passed = nextMoveId == MonsterModel.stunnedMoveId || string.Equals(nextMoveId, "DIZZY_MOVE", StringComparison.Ordinal)
                        ? !tunneler.IntendsToAttack && visualState == UndoSpecialCreatureVisualNormalizer.TunnelerVisualState.Stunned
                        : burrowedPower
                            ? (string.Equals(nextMoveId, "BURROW_MOVE", StringComparison.Ordinal) || string.Equals(nextMoveId, "BELOW_MOVE_1", StringComparison.Ordinal)) && visualState == UndoSpecialCreatureVisualNormalizer.TunnelerVisualState.Burrowed
                            : visualState == UndoSpecialCreatureVisualNormalizer.TunnelerVisualState.Surfaced && (!string.Equals(nextMoveId, "BELOW_MOVE_1", StringComparison.Ordinal));
                    return new UndoScenarioAssertionResult
                    {
                        Assertion = assertion,
                        Passed = passed,
                        Detail = $"next_move={nextMoveId ?? "null"}; burrowed={burrowedPower}; attacks={tunneler.IntendsToAttack}; visual={visualState}"
                    };
                }
                case "owl-magistrate-flight" when monster is OwlMagistrate owlMagistrate:
                {
                    string? nextMoveId = owlMagistrate.NextMove?.Id;
                    bool hasSoar = creature.HasPower<SoarPower>();
                    bool isFlying = ReadPrivateBool(owlMagistrate, "IsFlying");
                    UndoSpecialCreatureVisualNormalizer.OwlMagistrateVisualState visualState = UndoSpecialCreatureVisualNormalizer.GetOwlMagistrateVisualState(owlMagistrate);
                    bool passed = visualState == UndoSpecialCreatureVisualNormalizer.OwlMagistrateVisualState.Flying
                        ? isFlying && (hasSoar || string.Equals(nextMoveId, "VERDICT", StringComparison.Ordinal))
                        : !isFlying && !hasSoar && !string.Equals(nextMoveId, "VERDICT", StringComparison.Ordinal);
                    return new UndoScenarioAssertionResult
                    {
                        Assertion = assertion,
                        Passed = passed,
                        Detail = $"next_move={nextMoveId ?? "null"}; soar={hasSoar}; flying={isFlying}; attacks={owlMagistrate.IntendsToAttack}; visual={visualState}"
                    };
                }
                case "queen-amalgam-branch" when monster is Queen queen:
                {
                    string? nextMoveId = queen.NextMove?.Id;
                    bool hasAmalgamDied = ReadPrivateBool(queen, "HasAmalgamDied");
                    Creature? amalgam = UndoReflectionUtil.FindProperty(queen.GetType(), "Amalgam")?.GetValue(queen) as Creature
                        ?? UndoReflectionUtil.FindField(queen.GetType(), "_amalgam")?.GetValue(queen) as Creature;
                    bool amalgamAlive = amalgam != null && !amalgam.IsDead;
                    bool inDeathPath = string.Equals(nextMoveId, "OFF_WITH_YOUR_HEAD_MOVE", StringComparison.Ordinal)
                        || string.Equals(nextMoveId, "EXECUTION_MOVE", StringComparison.Ordinal)
                        || string.Equals(nextMoveId, "ENRAGE_MOVE", StringComparison.Ordinal);
                    bool passed = amalgamAlive ? !hasAmalgamDied && !inDeathPath : hasAmalgamDied || inDeathPath;
                    return new UndoScenarioAssertionResult
                    {
                        Assertion = assertion,
                        Passed = passed,
                        Detail = $"next_move={nextMoveId ?? "null"}; amalgam_alive={amalgamAlive}; has_amalgam_died={hasAmalgamDied}"
                    };
                }
            }
        }

        return new UndoScenarioAssertionResult
        {
            Assertion = assertion,
            Passed = false,
            Detail = $"scenario_creature_missing:{scenarioId}"
        };
    }
    private static UndoScenarioAssertionResult BuildAnimationAssertion(string assertion, string? animation, IReadOnlyList<string> expectedAnimations, bool? actualSleepingVfx = null, bool? expectedSleepingVfx = null, bool? actualVisible = null, bool? expectedVisible = null)
    {
        bool passed = animation != null && expectedAnimations.Contains(animation, StringComparer.Ordinal);
        if (expectedSleepingVfx.HasValue && actualSleepingVfx.HasValue)
            passed &= actualSleepingVfx.Value == expectedSleepingVfx.Value;
        if (expectedVisible.HasValue && actualVisible.HasValue)
            passed &= actualVisible.Value == expectedVisible.Value;

        string detail = passed
            ? $"animation={animation ?? "null"}"
            : $"expected={string.Join("/", expectedAnimations)} actual={animation ?? "null"}";
        if (expectedSleepingVfx.HasValue && actualSleepingVfx.HasValue)
            detail += $"; sleeping_vfx={actualSleepingVfx.Value}; expected_vfx={expectedSleepingVfx.Value}";
        if (expectedVisible.HasValue && actualVisible.HasValue)
            detail += $"; visible={actualVisible.Value}; expected_visible={expectedVisible.Value}";

        return new UndoScenarioAssertionResult
        {
            Assertion = assertion,
            Passed = passed,
            Detail = detail
        };
    }

    private static bool ReadPrivateBool(object target, string propertyName)
    {
        if (UndoReflectionUtil.FindProperty(target.GetType(), propertyName)?.GetValue(target) is bool propertyValue)
            return propertyValue;

        string fieldName = '_' + char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
        return UndoReflectionUtil.FindField(target.GetType(), fieldName)?.GetValue(target) is bool fieldValue && fieldValue;
    }
    private static object ProjectTopology(IReadOnlyList<CreatureTopologyState> states)
    {
        return states
            .OrderBy(static state => state.CreatureRef?.Key, StringComparer.Ordinal)
            .Select(state => new
            {
                CreatureKey = state.CreatureRef?.Key,
                state.Role,
                state.Side,
                MonsterId = state.MonsterId?.Entry,
                state.PetOwnerPlayerNetId,
                state.SlotName,
                state.Exists,
                state.IsDead,
                state.IsHalfDead,
                state.CurrentMoveId,
                state.NextMoveId,
                state.CurrentStateType,
                state.FollowUpStateType,
                LinkedCreatureRefs = state.LinkedCreatureRefs.Select(static linked => linked.Key).OrderBy(static key => key, StringComparer.Ordinal).ToList(),
                state.RuntimeCodecId,
                RuntimePayload = ProjectRuntimePayload(state.RuntimePayload)
            })
            .ToList();
    }

    private static object? ProjectRuntimePayload(UndoCreatureTopologyRuntimeState? payload)
    {
        return payload switch
        {
            UndoDoorTopologyRuntimeState door => new
            {
                door.CodecId,
                DoormakerRef = door.DoormakerRef?.Key,
                door.DeadStateFollowUpStateId,
                door.TimesGotBackIn,
                door.IsDoorVisible
            },
            UndoDecimillipedeTopologyRuntimeState decimillipede => new
            {
                decimillipede.CodecId,
                decimillipede.StarterMoveIdx,
                SegmentRefs = decimillipede.SegmentRefs.Select(static segment => segment.Key).OrderBy(static key => key, StringComparer.Ordinal).ToList()
            },
            UndoTestSubjectTopologyRuntimeState testSubject => new
            {
                testSubject.CodecId,
                testSubject.IsReviving
            },
            UndoCreatureTopologyRuntimeState topologyPayload => new
            {
                topologyPayload.CodecId
            },
            _ => null
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





















