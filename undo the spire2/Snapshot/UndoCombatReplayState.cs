using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Saves;

namespace UndoTheSpire2;

public sealed class UndoCombatReplayState
{
    public UndoCombatReplayState(
        SerializableRun initialRun,
        uint initialNextActionId,
        uint initialNextHookId,
        uint initialNextChecksumId,
        List<uint> initialChoiceIds,
        List<ReplayChecksumData>? checksumData = null)
    {
        InitialRun = initialRun;
        InitialNextActionId = initialNextActionId;
        InitialNextHookId = initialNextHookId;
        InitialNextChecksumId = initialNextChecksumId;
        InitialChoiceIds = initialChoiceIds;
        ChecksumData = checksumData ?? [];
        Events = [];
    }

    public SerializableRun InitialRun { get; }

    public uint InitialNextActionId { get; }

    public uint InitialNextHookId { get; }

    public uint InitialNextChecksumId { get; }

    public List<uint> InitialChoiceIds { get; }

    public List<ReplayChecksumData> ChecksumData { get; }

    public List<CombatReplayEvent> Events { get; }

    public int ActiveEventCount { get; set; }

    public void TruncateToActiveCount()
    {
        if (ActiveEventCount < 0)
            ActiveEventCount = 0;

        if (ActiveEventCount >= Events.Count)
            return;

        Events.RemoveRange(ActiveEventCount, Events.Count - ActiveEventCount);
    }
}
