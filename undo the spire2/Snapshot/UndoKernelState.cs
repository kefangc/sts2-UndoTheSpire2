// 文件说明：保存 action kernel、同步器与 paused choice 的状态。
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;

namespace UndoTheSpire2;

internal enum ActionKernelBoundaryKind
{
    StableBoundary,
    PausedChoice,
    UnsupportedLiveAction
}

internal sealed class ActionKernelState
{
    public const int CurrentSchemaVersion = 4;

    public static ActionKernelState Empty { get; } = new()
    {
        SchemaVersion = CurrentSchemaVersion
    };

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public ActionKernelBoundaryKind BoundaryKind { get; init; } = ActionKernelBoundaryKind.StableBoundary;

    public string? CurrentActionTypeName { get; init; }

    public GameActionState? CurrentActionState { get; init; }

    public ActionRef? CurrentActionRef { get; init; }

    public string? CurrentActionCodecId { get; init; }

    public UndoSerializedActionPayload? CurrentActionPayload { get; init; }

    public ActionRef? CurrentHookActionRef { get; init; }

    public PausedChoiceState? PausedChoiceState { get; init; }

    public IReadOnlyList<ActionQueueState> Queues { get; init; } = [];

    public int WaitingForResumptionCount { get; init; }

    public IReadOnlyList<ActionResumeState> WaitingForResumption { get; init; } = [];
}

internal sealed class ActionQueueState
{
    public ulong OwnerNetId { get; init; }

    public bool IsPaused { get; init; }

    public int PendingActionCount { get; init; }

    public IReadOnlyList<ActionQueueEntryState> PendingActions { get; init; } = [];
}

internal sealed class ActionQueueEntryState
{
    public ActionRef? ActionRef { get; init; }

    public string? CodecId { get; init; }

    public UndoSerializedActionPayload? Payload { get; init; }

    public GameActionState State { get; init; }
}

internal sealed class ActionResumeState
{
    public uint OldActionId { get; init; }

    public uint NewActionId { get; init; }
}

internal sealed class UndoSerializedActionPayload
{
    public ulong OwnerNetId { get; init; }

    public INetAction? NetAction { get; init; }

    public GameActionType? GameActionType { get; init; }

    public uint? HookId { get; init; }
}

internal sealed class PausedChoiceState
{
    public UndoChoiceKind ChoiceKind { get; init; }

    public ulong? OwnerNetId { get; init; }

    public uint? ChoiceId { get; init; }

    public string? Prompt { get; init; }

    public int MinSelections { get; init; }

    public int MaxSelections { get; init; }

    public IReadOnlyList<CardRef> CandidateCardRefs { get; init; } = [];

    public IReadOnlyList<CardRef> PreselectedCardRefs { get; init; } = [];

    public ActionRef? SourceActionRef { get; init; }

    public string? SourceActionCodecId { get; init; }

    public UndoSerializedActionPayload? SourceActionPayload { get; init; }

    public uint? ResumeActionId { get; init; }

    public uint? ResumeToken { get; init; }

    public UndoChoiceSpec? ChoiceSpec { get; init; }
}

internal sealed class RuntimeGraphState
{
    public IReadOnlyList<UndoPlayerPileCardRuntimeState> CardRuntimeStates { get; init; } = [];

    public IReadOnlyList<UndoPowerRuntimeState> PowerRuntimeStates { get; init; } = [];

    public IReadOnlyList<UndoRelicRuntimeState> RelicRuntimeStates { get; init; } = [];
}

internal sealed class PresentationHints
{
    public UndoSelectionSessionState? SelectionSessionState { get; init; }
}
