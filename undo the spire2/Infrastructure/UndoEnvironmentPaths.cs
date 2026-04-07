namespace UndoTheSpire2;

internal static class UndoEnvironmentPaths
{
    internal const string CacheRootEnvVar = "UNDO_THE_SPIRE2_CACHE_ROOT";
    internal const string OfficialSourceRootEnvVar = "UNDO_THE_SPIRE2_OFFICIAL_SOURCE_ROOT";
    internal const string ArtifactsRootEnvVar = "UNDO_THE_SPIRE2_ARTIFACTS_ROOT";
    internal const string ScenarioRootEnvVar = "UNDO_THE_SPIRE2_SCENARIO_ROOT";
    internal const string ReportsRootEnvVar = "UNDO_THE_SPIRE2_REPORTS_ROOT";

    internal const string DefaultCacheRoot = @"F:\projects\undo-the-spire2-cache";
    internal const string DefaultOfficialSourceRoot = @"F:\projects\slay the spire2\sts2\MegaCrit\sts2\Core";

    public static string ResolveCacheRoot(string? configuredPath = null)
    {
        return Resolve(configuredPath, CacheRootEnvVar, DefaultCacheRoot);
    }

    public static string ResolveOfficialSourceRoot(string? configuredPath = null)
    {
        return Resolve(configuredPath, OfficialSourceRootEnvVar, DefaultOfficialSourceRoot);
    }

    public static string ResolveArtifactsRoot(string? configuredPath = null)
    {
        return Resolve(configuredPath, ArtifactsRootEnvVar, Path.Combine(ResolveCacheRoot(), "artifacts"));
    }

    public static string ResolveScenarioRoot(string? configuredPath = null)
    {
        return Resolve(configuredPath, ScenarioRootEnvVar, Path.Combine(ResolveArtifactsRoot(), "scenario-definitions"));
    }

    public static string ResolveReportsRoot(string? configuredPath = null)
    {
        return Resolve(configuredPath, ReportsRootEnvVar, Path.Combine(ResolveCacheRoot(), "reports"));
    }

    private static string Resolve(string? configuredPath, string envVarName, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return configuredPath;

        string? environmentValue = Environment.GetEnvironmentVariable(envVarName);
        return string.IsNullOrWhiteSpace(environmentValue) ? fallback : environmentValue;
    }
}
