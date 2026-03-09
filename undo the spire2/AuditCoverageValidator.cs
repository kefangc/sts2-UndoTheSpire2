using System.Text.Json;

namespace UndoTheSpire2;

internal sealed class AuditCoverageRecord
{
    public string Id { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    public bool Implemented { get; init; }
}

internal sealed class AuditCoverageValidationResult
{
    public IReadOnlyList<string> MissingOfficialPatterns { get; init; } = [];

    public IReadOnlyList<string> UnimplementedRegistryPatterns { get; init; } = [];

    public bool IsSuccess => MissingOfficialPatterns.Count == 0 && UnimplementedRegistryPatterns.Count == 0;
}

internal static class AuditCoverageValidator
{
    public static string DefaultArtifactsRoot { get; } = Path.Combine(
        "F:",
        "projects",
        "undo-the-spire2-cache",
        "artifacts");

    public static AuditCoverageValidationResult ValidateAgainstCache(string? artifactsRoot = null)
    {
        string root = string.IsNullOrWhiteSpace(artifactsRoot) ? DefaultArtifactsRoot : artifactsRoot;
        string officialRuntimePatternsPath = Path.Combine(root, "official-runtime-patterns.json");
        if (!File.Exists(officialRuntimePatternsPath))
            return new AuditCoverageValidationResult();

        JsonSerializerOptions options = new()
        {
            PropertyNameCaseInsensitive = true
        };

        IReadOnlyList<AuditCoverageRecord> records = JsonSerializer.Deserialize<List<AuditCoverageRecord>>(
            File.ReadAllText(officialRuntimePatternsPath),
            options) ?? [];
        HashSet<string> implementedIds = UndoRuntimeStateCodecRegistry.GetImplementedCodecIds();
        implementedIds.UnionWith(UndoMonsterTopologyCodecRegistry.GetImplementedCodecIds());
        implementedIds.UnionWith(UndoActionCodecRegistry.GetImplementedCodecIds());

        List<string> missingOfficialPatterns = records
            .Where(record => record.Category is "power" or "relic" or "card" or "topology" or "action")
            .Where(record => record.Implemented && !implementedIds.Contains(record.Id))
            .Select(record => record.Id)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToList();

        List<string> unimplementedRegistryPatterns = records
            .Where(record => !record.Implemented && implementedIds.Contains(record.Id))
            .Select(record => record.Id)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToList();

        return new AuditCoverageValidationResult
        {
            MissingOfficialPatterns = missingOfficialPatterns,
            UnimplementedRegistryPatterns = unimplementedRegistryPatterns
        };
    }

    public static string BuildMarkdownReport(AuditCoverageValidationResult result)
    {
        if (result.IsSuccess)
            return "# Audit Coverage Report`r`n`r`n- All tracked official runtime patterns are aligned with the registry.";

        List<string> lines =
        [
            "# Audit Coverage Report",
            string.Empty
        ];

        if (result.MissingOfficialPatterns.Count > 0)
        {
            lines.Add("- Missing official patterns:");
            lines.AddRange(result.MissingOfficialPatterns.Select(static id => $"  - {id}"));
        }

        if (result.UnimplementedRegistryPatterns.Count > 0)
        {
            lines.Add("- Registry implements patterns not yet marked in cache artifacts:");
            lines.AddRange(result.UnimplementedRegistryPatterns.Select(static id => $"  - {id}"));
        }

        return string.Join(Environment.NewLine, lines);
    }
}
