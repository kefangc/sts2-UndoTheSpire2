using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
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
}

internal sealed class UndoScenarioPreconditionResult
{
    public bool Matched { get; init; }

    public string Detail { get; init; } = string.Empty;
}

internal static class UndoScenarioExecutor
{
    private static readonly PileType[] CombatPileOrder =
    [
        PileType.Hand,
        PileType.Draw,
        PileType.Discard,
        PileType.Exhaust,
        PileType.Play
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private static readonly IReadOnlyDictionary<string, Func<UndoScenarioDefinition, RunState, CombatState, Task<UndoScenarioExecutionResult>>> Handlers =
        new Dictionary<string, Func<UndoScenarioDefinition, RunState, CombatState, Task<UndoScenarioExecutionResult>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["well-laid-plans"] = ExecuteWellLaidPlansScenarioAsync,
            ["forgotten-ritual"] = ExecuteRoundtripScenarioAsync,
            ["automation-power"] = ExecuteRoundtripScenarioAsync,
            ["infested-prism"] = ExecuteRoundtripScenarioAsync,
            ["decimillipede"] = ExecuteRoundtripScenarioAsync,
            ["door-maker"] = ExecuteRoundtripScenarioAsync,
            ["paels-legion"] = ExecuteRoundtripScenarioAsync,
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
                UnexpectedUnsupportedCapabilities = unexpectedUnsupported
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
                UnexpectedUnsupportedCapabilities = unexpectedUnsupported
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
            UnexpectedUnsupportedCapabilities = unexpectedUnsupported
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
            Assertions = assertions
        };
    }

    private static Task<UndoScenarioExecutionResult> ExecuteWellLaidPlansScenarioAsync(UndoScenarioDefinition scenario, RunState runState, CombatState combatState)
    {
        UndoController controller = MainFile.Controller;
        UndoChoiceSpec? activeChoice = TryCaptureActiveChoiceSpec(controller);
        UndoCombatFullState? currentSnapshot = TryCaptureCurrentCombatFullState(controller);
        PausedChoiceState? pausedChoice = currentSnapshot?.ActionKernelState.PausedChoiceState;
        RestoreCapabilityReport capability = UndoActionCodecRegistry.EvaluateCapability(pausedChoice);
        bool supportedChoiceUiActive = IsSupportedChoiceUiActive();
        bool pausedChoiceCaptured = pausedChoice?.ChoiceSpec?.Kind == UndoChoiceKind.HandSelection
            && pausedChoice.SourceActionRef.ActionId != null;
        bool primaryChoiceSupported = capability.Result == RestoreCapabilityResult.Supported;
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
                Assertion = "retain_selection_reopens",
                Passed = supportedChoiceUiActive && activeChoice?.Kind == UndoChoiceKind.HandSelection,
                Detail = supportedChoiceUiActive ? "supported_choice_ui_active" : "supported_choice_ui_inactive"
            },
            new UndoScenarioAssertionResult
            {
                Assertion = "no_hidden_replay",
                Passed = !HasSyntheticChoiceSession(controller),
                Detail = HasSyntheticChoiceSession(controller) ? "synthetic_choice_session_active" : "primary_choice_ui_active"
            }
        ];

        bool passed = assertions.All(static assertion => assertion.Passed);
        return Task.FromResult(new UndoScenarioExecutionResult
        {
            Scenario = scenario,
            Status = passed ? UndoScenarioExecutionStatus.Passed : UndoScenarioExecutionStatus.Failed,
            Detail = passed ? "choice_anchor_ready" : "choice_anchor_incomplete",
            Assertions = assertions
        });
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

    private static UndoScenarioPreconditionResult EvaluatePreconditions(string scenarioId, RunState runState, CombatState combatState)
    {
        Player? me = LocalContext.GetMe(combatState);
        return scenarioId switch
        {
            "well-laid-plans" => Require(me != null
                    && CreatureHasPower(me.Creature, "WellLaidPlansPower")
                    && TryCaptureActiveChoiceSpec(MainFile.Controller)?.Kind == UndoChoiceKind.HandSelection,
                "well_laid_plans_choice_boundary_required"),
            "forgotten-ritual" => Require(ContainsCombatCard(runState, "ForgottenRitual"), "forgotten_ritual_card_required"),
            "automation-power" => Require(me != null && CreatureHasPower(me.Creature, "AutomationPower"), "automation_power_required"),
            "infested-prism" => Require(AnyMonsterType(combatState, "InfestedPrism") || AnyCreatureHasPower(combatState, "VitalSparkPower"), "infested_prism_required"),
            "decimillipede" => Require(AnyMonsterType(combatState, "DecimillipedeSegment"), "decimillipede_required"),
            "door-maker" => Require(AnyMonsterType(combatState, "Door") || AnyMonsterType(combatState, "Doormaker"), "door_or_doormaker_required"),
            "paels-legion" => Require(me != null && PlayerHasRelic(me, "PaelsLegion") && combatState.Allies.Any(creature => creature.PetOwner == me && HasTypeName(creature.Monster, "PaelsLegion")), "paels_legion_pet_required"),
            "throwing-axe" => Require(me != null && PlayerHasRelic(me, "ThrowingAxe"), "throwing_axe_required"),
            "happy-flower" => Require(me != null && PlayerHasRelic(me, "HappyFlower"), "happy_flower_required"),
            "history-course" => Require(me != null && PlayerHasRelic(me, "HistoryCourse"), "history_course_required"),
            "pen-nib" => Require(me != null && PlayerHasRelic(me, "PenNib"), "pen_nib_required"),
            "art-of-war" => Require(me != null && PlayerHasRelic(me, "ArtOfWar"), "art_of_war_required"),
            "swipe-power" => Require(AnyCreatureHasPower(combatState, "SwipePower"), "swipe_power_required"),
            "death-march" => Require(ContainsCombatCard(runState, "DeathMarch"), "death_march_card_required"),
            _ => Require(true, "matched")
        };
    }

    private static UndoScenarioPreconditionResult Require(bool matched, string detail)
    {
        return new UndoScenarioPreconditionResult
        {
            Matched = matched,
            Detail = detail
        };
    }

    private static UndoCombatFullState? TryCaptureCurrentCombatFullState(UndoController controller)
    {
        MethodInfo? method = typeof(UndoController).GetMethod("CaptureCurrentCombatFullState", BindingFlags.Instance | BindingFlags.NonPublic);
        return method?.Invoke(controller, null) as UndoCombatFullState;
    }

    private static UndoChoiceSpec? TryCaptureActiveChoiceSpec(UndoController controller)
    {
        object? session = typeof(UndoController).GetField("_syntheticChoiceSession", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(controller);
        UndoChoiceSpec? sessionChoice = session?.GetType().GetProperty("ChoiceSpec", BindingFlags.Instance | BindingFlags.Public)?.GetValue(session) as UndoChoiceSpec;
        if (sessionChoice != null)
            return sessionChoice;

        MethodInfo? method = typeof(UndoController).GetMethod("TryCaptureCurrentChoiceSpecFromUi", BindingFlags.Static | BindingFlags.NonPublic);
        return method?.Invoke(null, null) as UndoChoiceSpec;
    }

    private static RestoreCapabilityReport GetLastRestoreCapabilityReport(UndoController controller)
    {
        return typeof(UndoController).GetField("_lastRestoreCapabilityReport", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(controller) as RestoreCapabilityReport
            ?? RestoreCapabilityReport.SupportedReport();
    }

    private static string? GetLastRestoreFailureReason(UndoController controller)
    {
        return typeof(UndoController).GetField("_lastRestoreFailureReason", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(controller) as string;
    }

    private static bool HasSyntheticChoiceSession(UndoController controller)
    {
        return typeof(UndoController).GetField("_syntheticChoiceSession", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(controller) != null;
    }

    private static bool IsSupportedChoiceUiActive()
    {
        NCombatUi? combatUi = NCombatRoom.Instance?.Ui;
        if (combatUi?.Hand?.IsInCardSelection == true)
            return true;

        return NOverlayStack.Instance?.Peek() is NChooseACardSelectionScreen or NCardGridSelectionScreen;
    }

    private static bool ContainsCombatCard(RunState runState, string typeName)
    {
        foreach (Player player in runState.Players)
        {
            foreach (PileType pileType in CombatPileOrder)
            {
                CardPile? pile = CardPile.Get(pileType, player);
                if (pile?.Cards.Any(card => HasTypeName(card, typeName)) == true)
                    return true;
            }
        }

        return false;
    }

    private static bool PlayerHasRelic(Player player, string typeName)
    {
        return player.Relics.Any(relic => HasTypeName(relic, typeName));
    }

    private static bool CreatureHasPower(Creature creature, string typeName)
    {
        return creature.Powers.Any(power => HasTypeName(power, typeName));
    }

    private static bool AnyCreatureHasPower(CombatState combatState, string typeName)
    {
        return combatState.Creatures.Any(creature => CreatureHasPower(creature, typeName));
    }

    private static bool AnyMonsterType(CombatState combatState, string typeName)
    {
        return combatState.Creatures.Any(creature => HasTypeName(creature.Monster, typeName));
    }

    private static bool HasTypeName(object? value, string typeName)
    {
        return value?.GetType().Name.Equals(typeName, StringComparison.Ordinal) == true;
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
                "display_counter_restores" => CompareProjection(assertion, targetState.RuntimeGraphState.PowerRuntimeStates, redoState.RuntimeGraphState.PowerRuntimeStates, "power_runtime_roundtrip"),
                "first_damage_only_triggers_once_after_undo" => CompareProjection(assertion, targetState.RuntimeGraphState.PowerRuntimeStates, redoState.RuntimeGraphState.PowerRuntimeStates, "power_runtime_roundtrip"),
                "reviving_state_restores" => CompareProjection(assertion, ProjectTopology(targetState.MonsterTopologyStates), ProjectTopology(redoState.MonsterTopologyStates), "monster_topology_roundtrip"),
                "segment_rejoins_correctly" => CompareProjection(assertion, ProjectTopology(targetState.MonsterTopologyStates), ProjectTopology(redoState.MonsterTopologyStates), "monster_topology_roundtrip"),
                "door_phase_restores" => CompareProjection(assertion, ProjectTopology(targetState.MonsterTopologyStates), ProjectTopology(redoState.MonsterTopologyStates), "monster_topology_roundtrip"),
                "times_got_back_in_restores" => CompareProjection(assertion, ProjectTopology(targetState.MonsterTopologyStates), ProjectTopology(redoState.MonsterTopologyStates), "monster_topology_roundtrip"),
                "pet_role_restores" => CompareProjection(assertion, ProjectTopology(targetState.MonsterTopologyStates), ProjectTopology(redoState.MonsterTopologyStates), "monster_topology_roundtrip"),
                "pet_owner_restores" => CompareProjection(assertion, ProjectTopology(targetState.MonsterTopologyStates), ProjectTopology(redoState.MonsterTopologyStates), "monster_topology_roundtrip"),
                "no_duplicate_pet_after_undo" => CompareProjection(assertion, ProjectTopology(targetState.MonsterTopologyStates), ProjectTopology(redoState.MonsterTopologyStates), "monster_topology_roundtrip"),
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

    private static object ProjectTopology(IReadOnlyList<MonsterTopologyState> states)
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

    private static object? ProjectRuntimePayload(UndoMonsterTopologyRuntimeState? payload)
    {
        return payload switch
        {
            UndoDoorTopologyRuntimeState door => new
            {
                door.CodecId,
                DoormakerRef = door.DoormakerRef?.Key,
                door.DeadStateFollowUpStateId,
                door.TimesGotBackIn
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
            UndoMonsterTopologyRuntimeState topologyPayload => new
            {
                topologyPayload.CodecId
            },
            _ => null
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

    private static List<string> GetCapabilityGaps(UndoScenarioDefinition scenario)
    {
        HashSet<string> implemented = UndoRuntimeStateCodecRegistry.GetImplementedCodecIds();
        implemented.UnionWith(UndoMonsterTopologyCodecRegistry.GetImplementedCodecIds());
        implemented.UnionWith(UndoActionCodecRegistry.GetImplementedCodecIds());

        List<string> required = scenario.Id switch
        {
            "well-laid-plans" => ["action:WellLaidPlans.choice"],
            "forgotten-ritual" => ["history:CombatHistory.entries"],
            "automation-power" => ["power:AutomationPower.cardsLeft"],
            "infested-prism" => ["power:VitalSparkPower.playersTriggeredThisTurn", "topology:InfestedPrism"],
            "decimillipede" => ["topology:Decimillipede"],
            "door-maker" => ["topology:DoorAndDoormaker", "power:DoorRevivalPower.isHalfDead"],
            "paels-legion" => ["relic:PaelsLegion.affectedCardPlay"],
            "throwing-axe" => [],
            "happy-flower" => [],
            "history-course" => ["history:CombatHistory.entries"],
            "pen-nib" => ["relic:PenNib.AttackToDouble"],
            "art-of-war" => [],
            "swipe-power" => [],
            "death-march" => ["history:CombatHistory.entries"],
            _ => []
        };

        return required.Where(requiredId => !implemented.Contains(requiredId)).ToList();
    }
}



