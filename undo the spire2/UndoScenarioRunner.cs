using System.Text;
using System.Text.Json;

namespace UndoTheSpire2;

internal sealed class UndoScenarioDefinition : IUndoScenario
{
    public string Id { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public IReadOnlyList<string> Tags { get; init; } = [];

    public IReadOnlyList<string> SourceFiles { get; init; } = [];

    public IReadOnlyList<string> Assertions { get; init; } = [];

    public IReadOnlyList<string> Setup { get; init; } = [];

    public IReadOnlyList<string> Steps { get; init; } = [];

    public IReadOnlyList<string> CapturePoints { get; init; } = [];

    public IReadOnlyList<string> ExpectedUnsupportedCapabilities { get; init; } = [];
}

internal sealed class UndoScenarioReport
{
    public required IReadOnlyList<UndoScenarioExecutionResult> Results { get; init; }

    public required AuditCoverageValidationResult CoverageValidation { get; init; }

    public string ToMarkdown()
    {
        StringBuilder builder = new();
        builder.AppendLine("# Scenario Run Report");
        builder.AppendLine();
        builder.AppendLine($"- Scenario definitions loaded: {Results.Count}");
        builder.AppendLine($"- Audit coverage aligned: {CoverageValidation.IsSuccess}");
        builder.AppendLine($"- Passed: {Results.Count(static result => result.Status == UndoScenarioExecutionStatus.Passed)}");
        builder.AppendLine($"- Failed: {Results.Count(static result => result.Status == UndoScenarioExecutionStatus.Failed)}");
        builder.AppendLine($"- Skipped: {Results.Count(static result => result.Status == UndoScenarioExecutionStatus.Skipped)}");
        builder.AppendLine();
        foreach (UndoScenarioExecutionResult result in Results)
        {
            builder.AppendLine($"## {result.Scenario.Title}");
            builder.AppendLine($"- Id: {result.Scenario.Id}");
            builder.AppendLine($"- Status: {result.Status}");
            if (!string.IsNullOrWhiteSpace(result.Detail))
                builder.AppendLine($"- Detail: {result.Detail}");
            if (result.UnsupportedCapabilities.Count > 0)
                builder.AppendLine($"- Unsupported: {string.Join(", ", result.UnsupportedCapabilities)}");
            if (result.Assertions.Count > 0)
            {
                builder.AppendLine("- Assertions:");
                foreach (UndoScenarioAssertionResult assertion in result.Assertions)
                    builder.AppendLine($"  - {(assertion.Passed ? "pass" : "fail")} {assertion.Assertion}{(string.IsNullOrWhiteSpace(assertion.Detail) ? string.Empty : $" ({assertion.Detail})")}");
            }
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }
}

internal static class UndoScenarioRunner
{
    public static string DefaultScenarioRoot { get; } = Path.Combine(
        "F:",
        "projects",
        "undo-the-spire2-cache",
        "artifacts",
        "scenario-definitions");

    public static string DefaultReportsRoot { get; } = Path.Combine(
        "F:",
        "projects",
        "undo-the-spire2-cache",
        "reports");

    public static IReadOnlyList<UndoScenarioDefinition> LoadScenarioDefinitions(string? root = null)
    {
        string scenarioRoot = string.IsNullOrWhiteSpace(root) ? DefaultScenarioRoot : root;
        if (!Directory.Exists(scenarioRoot))
            return [];

        JsonSerializerOptions options = new()
        {
            PropertyNameCaseInsensitive = true
        };

        List<UndoScenarioDefinition> scenarios = [];
        foreach (string filePath in Directory.EnumerateFiles(scenarioRoot, "*.json", SearchOption.TopDirectoryOnly))
        {
            string json = File.ReadAllText(filePath);
            UndoScenarioDefinition? scenario = JsonSerializer.Deserialize<UndoScenarioDefinition>(json, options);
            if (scenario != null)
                scenarios.Add(scenario);
        }

        return scenarios.OrderBy(static scenario => scenario.Id, StringComparer.Ordinal).ToList();
    }

    public static UndoScenarioReport BuildDefinitionReport(string? scenarioRoot = null)
    {
        IReadOnlyList<UndoScenarioDefinition> scenarios = LoadScenarioDefinitions(scenarioRoot);
        AuditCoverageValidationResult coverage = AuditCoverageValidator.ValidateAgainstCache();
        IReadOnlyList<UndoScenarioExecutionResult> results = scenarios.Select(scenario => new UndoScenarioExecutionResult
        {
            Scenario = scenario,
            Status = UndoScenarioExecutionStatus.Skipped,
            Detail = "definition_only"
        }).ToList();
        return new UndoScenarioReport
        {
            Results = results,
            CoverageValidation = coverage
        };
    }

    public static async Task<UndoScenarioReport> RunAsync(string? scenarioRoot = null)
    {
        IReadOnlyList<UndoScenarioDefinition> scenarios = LoadScenarioDefinitions(scenarioRoot);
        AuditCoverageValidationResult coverage = AuditCoverageValidator.ValidateAgainstCache();
        IReadOnlyList<UndoScenarioExecutionResult> results = await UndoScenarioExecutor.RunAllAsync(scenarios);
        return new UndoScenarioReport
        {
            Results = results,
            CoverageValidation = coverage
        };
    }

    public static void WriteReports(string? scenarioRoot = null, string? reportsRoot = null)
    {
        UndoScenarioReport report = RunAsync(scenarioRoot).GetAwaiter().GetResult();
        string root = string.IsNullOrWhiteSpace(reportsRoot) ? DefaultReportsRoot : reportsRoot;
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "scenario-run-report.md"), report.ToMarkdown());
        File.WriteAllText(Path.Combine(root, "audit-coverage-report.md"), AuditCoverageValidator.BuildMarkdownReport(report.CoverageValidation));
        File.WriteAllText(
            Path.Combine(root, "unsupported-capabilities-report.md"),
            BuildUnsupportedCapabilitiesMarkdown(report.Results));
    }

    private static string BuildUnsupportedCapabilitiesMarkdown(IReadOnlyList<UndoScenarioExecutionResult> results)
    {
        IReadOnlyList<string> lines = results
            .Where(static result => result.UnsupportedCapabilities.Count > 0)
            .Select(result => $"- {result.Scenario.Id}: {string.Join(", ", result.UnsupportedCapabilities)}")
            .ToList();

        return lines.Count == 0
            ? "# Unsupported Capabilities Report" + Environment.NewLine + Environment.NewLine + "- none"
            : "# Unsupported Capabilities Report" + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }
}
