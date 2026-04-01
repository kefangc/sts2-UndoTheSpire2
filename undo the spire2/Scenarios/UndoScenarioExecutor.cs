// 文件说明：执行调试和回归场景，并驱动自动验证�?
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


}





















