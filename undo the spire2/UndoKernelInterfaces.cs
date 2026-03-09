namespace UndoTheSpire2;

internal interface IUndoActionCodec<TAction, TState>
{
    string CodecId { get; }

    bool CanHandle(TAction action);

    TState? Capture(TAction action);

    void Restore(TAction action, TState state);
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
