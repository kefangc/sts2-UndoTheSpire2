// 文件说明：描述某条恢复路径是否受支持及原因。
namespace UndoTheSpire2;

internal enum RestoreCapabilityResult
{
    Supported,
    FallbackToSyntheticChoice,
    UnsupportedLiveAction,
    UnsupportedOfficialPattern,
    QueueStateMismatch,
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
        Result is RestoreCapabilityResult.UnsupportedLiveAction
            or RestoreCapabilityResult.UnsupportedOfficialPattern
            or RestoreCapabilityResult.UnsupportedThirdPartyPattern
            or RestoreCapabilityResult.TopologyMismatch
            or RestoreCapabilityResult.QueueStateMismatch
            or RestoreCapabilityResult.SchemaMismatch;
}
