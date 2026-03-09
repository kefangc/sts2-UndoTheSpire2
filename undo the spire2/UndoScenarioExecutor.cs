using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Runs;

namespace UndoTheSpire2;

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
}

internal static class UndoScenarioExecutor
{
    private static readonly IReadOnlyDictionary<string, Func<UndoScenarioDefinition, Task<UndoScenarioExecutionResult>>> Handlers =
        new Dictionary<string, Func<UndoScenarioDefinition, Task<UndoScenarioExecutionResult>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["well-laid-plans"] = ExecuteLiveCombatScenarioAsync,
            ["forgotten-ritual"] = ExecuteLiveCombatScenarioAsync,
            ["automation-power"] = ExecuteLiveCombatScenarioAsync,
            ["infested-prism"] = ExecuteLiveCombatScenarioAsync,
            ["decimillipede"] = ExecuteLiveCombatScenarioAsync,
            ["door-maker"] = ExecuteLiveCombatScenarioAsync,
            ["throwing-axe"] = ExecuteLiveCombatScenarioAsync,
            ["happy-flower"] = ExecuteLiveCombatScenarioAsync,
            ["history-course"] = ExecuteLiveCombatScenarioAsync,
            ["pen-nib"] = ExecuteLiveCombatScenarioAsync,
            ["art-of-war"] = ExecuteLiveCombatScenarioAsync,
            ["swipe-power"] = ExecuteLiveCombatScenarioAsync,
            ["death-march"] = ExecuteLiveCombatScenarioAsync
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
        if (!Handlers.TryGetValue(scenario.Id, out Func<UndoScenarioDefinition, Task<UndoScenarioExecutionResult>>? handler))
        {
            return new UndoScenarioExecutionResult
            {
                Scenario = scenario,
                Status = UndoScenarioExecutionStatus.Skipped,
                Detail = "no_registered_handler"
            };
        }

        return await handler(scenario);
    }

    private static Task<UndoScenarioExecutionResult> ExecuteLiveCombatScenarioAsync(UndoScenarioDefinition scenario)
    {
        List<string> unsupported = GetCapabilityGaps(scenario);
        if (unsupported.Count > 0)
        {
            return Task.FromResult(new UndoScenarioExecutionResult
            {
                Scenario = scenario,
                Status = UndoScenarioExecutionStatus.Failed,
                Detail = "missing_capabilities",
                UnsupportedCapabilities = unsupported,
                Assertions = scenario.Assertions.Select(assertion => new UndoScenarioAssertionResult
                {
                    Assertion = assertion,
                    Passed = false,
                    Detail = string.Join(", ", unsupported)
                }).ToList()
            });
        }

        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        if (runState == null || combatState == null || !CombatManager.Instance.IsInProgress)
        {
            return Task.FromResult(new UndoScenarioExecutionResult
            {
                Scenario = scenario,
                Status = UndoScenarioExecutionStatus.Skipped,
                Detail = "live_scenario_setup_required"
            });
        }

        return Task.FromResult(new UndoScenarioExecutionResult
        {
            Scenario = scenario,
            Status = UndoScenarioExecutionStatus.Skipped,
            Detail = "live_combat_detected_but_repro_steps_not_automated",
            Assertions = scenario.Assertions.Select(assertion => new UndoScenarioAssertionResult
            {
                Assertion = assertion,
                Passed = false,
                Detail = "manual setup required"
            }).ToList()
        });
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
