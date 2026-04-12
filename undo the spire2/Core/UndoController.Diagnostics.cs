namespace UndoTheSpire2;

public sealed partial class UndoController
{
    internal UndoCombatFullState DebugCaptureCurrentCombatFullState()
    {
        return CaptureCurrentCombatFullState();
    }

    internal UndoSnapshot? DebugGetLatestUndoSnapshot()
    {
        return _pastSnapshots.First?.Value;
    }

    internal UndoChoiceSpec? DebugCaptureActiveChoiceSpec()
    {
        UndoChoiceSpec? sessionChoice = _syntheticChoiceSession?.ChoiceSpec;
        return sessionChoice ?? TryCaptureCurrentChoiceSpecFromUi();
    }

    internal RestoreCapabilityReport DebugGetLastRestoreCapabilityReport()
    {
        return _lastRestoreCapabilityReport;
    }

    internal string? DebugGetLastRestoreFailureReason()
    {
        return _lastRestoreFailureReason;
    }

    internal bool DebugHasSyntheticChoiceSession()
    {
        return _syntheticChoiceSession != null;
    }

    internal UndoPerformanceSnapshot DebugGetPerformanceSnapshot()
    {
        return UndoPerformanceDiagnostics.CaptureSnapshot();
    }
}
