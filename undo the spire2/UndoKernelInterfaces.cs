using MegaCrit.Sts2.Core.Runs;

namespace UndoTheSpire2;

internal interface IUndoActionCodec<TActionState>
{
    string CodecId { get; }

    bool CanHandle(TActionState state);

    Task<UndoChoiceResultKey?> RestoreAsync(TActionState state, RunState runState);
}

internal interface IUndoTopologyCodec<TState>
{
    string CodecId { get; }

    TState Capture();

    void Restore(TState state);
}

internal interface IUndoScenario
{
    string Id { get; }

    string Title { get; }

    IReadOnlyList<string> Tags { get; }
}
