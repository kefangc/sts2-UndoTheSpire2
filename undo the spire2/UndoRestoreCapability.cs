namespace UndoTheSpire2;

internal enum RestoreCapabilityResult
{
    Supported,
    FallbackToSyntheticChoice,
    UnsupportedOfficialPattern,
    UnsupportedThirdPartyPattern,
    TopologyMismatch,
    SchemaMismatch
}

internal sealed class RestoreCapabilityReport
{
    public static RestoreCapabilityReport SupportedReport(string? detail = null)
    {
        return new RestoreCapabilityReport
        {
            Result = RestoreCapabilityResult.Supported,
            Detail = detail
        };
    }

    public required RestoreCapabilityResult Result { get; init; }

    public string? Detail { get; init; }

    public bool IsFailure =>
        Result is RestoreCapabilityResult.UnsupportedOfficialPattern
            or RestoreCapabilityResult.UnsupportedThirdPartyPattern
            or RestoreCapabilityResult.TopologyMismatch
            or RestoreCapabilityResult.SchemaMismatch;
}
