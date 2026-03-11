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
        List<uint> initialChoiceIds)
    {
        InitialRun = initialRun;
        InitialNextActionId = initialNextActionId;
        InitialNextHookId = initialNextHookId;
        InitialNextChecksumId = initialNextChecksumId;
        InitialChoiceIds = initialChoiceIds;
        Events = [];
    }

    public SerializableRun InitialRun { get; }

    public uint InitialNextActionId { get; }

    public uint InitialNextHookId { get; }

    public uint InitialNextChecksumId { get; }

    public List<uint> InitialChoiceIds { get; }

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
