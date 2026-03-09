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
    public required IReadOnlyList<UndoScenarioDefinition> Scenarios { get; init; }

    public required AuditCoverageValidationResult CoverageValidation { get; init; }

    public string ToMarkdown()
    {
        StringBuilder builder = new();
        builder.AppendLine("# Scenario Run Report");
        builder.AppendLine();
        builder.AppendLine($"- Scenario definitions loaded: {Scenarios.Count}");
        builder.AppendLine($"- Audit coverage aligned: {CoverageValidation.IsSuccess}");
        builder.AppendLine();
        foreach (UndoScenarioDefinition scenario in Scenarios)
        {
            builder.AppendLine($"## {scenario.Title}");
            builder.AppendLine($"- Id: {scenario.Id}");
            builder.AppendLine($"- Assertions: {(scenario.Assertions.Count == 0 ? "none" : string.Join(", ", scenario.Assertions))}");
            if (scenario.ExpectedUnsupportedCapabilities.Count > 0)
                builder.AppendLine($"- Expected unsupported: {string.Join(", ", scenario.ExpectedUnsupportedCapabilities)}");
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
        return new UndoScenarioReport
        {
            Scenarios = scenarios,
            CoverageValidation = coverage
        };
    }

    public static void WriteReports(string? scenarioRoot = null, string? reportsRoot = null)
    {
        UndoScenarioReport report = BuildDefinitionReport(scenarioRoot);
        string root = string.IsNullOrWhiteSpace(reportsRoot) ? DefaultReportsRoot : reportsRoot;
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "scenario-run-report.md"), report.ToMarkdown());
        File.WriteAllText(Path.Combine(root, "audit-coverage-report.md"), AuditCoverageValidator.BuildMarkdownReport(report.CoverageValidation));
        File.WriteAllText(
            Path.Combine(root, "unsupported-capabilities-report.md"),
            report.Scenarios.Any(static scenario => scenario.ExpectedUnsupportedCapabilities.Count > 0)
                ? string.Join(Environment.NewLine, report.Scenarios
                    .Where(static scenario => scenario.ExpectedUnsupportedCapabilities.Count > 0)
                    .Select(scenario => $"- {scenario.Id}: {string.Join(", ", scenario.ExpectedUnsupportedCapabilities)}"))
                : "- none");
    }
}
