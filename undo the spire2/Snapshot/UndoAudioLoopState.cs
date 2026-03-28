using System.Collections.Generic;

namespace UndoTheSpire2;

internal sealed class UndoAudioLoopState
{
    public required string EventPath { get; init; }

    public bool UsesLoopParam { get; init; }

    public IReadOnlyList<UndoAudioLoopParamState> Parameters { get; init; } = [];
}

internal sealed class UndoAudioLoopParamState
{
    public required string Name { get; init; }

    public float Value { get; init; }
}
