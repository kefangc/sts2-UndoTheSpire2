// 文件说明：定义 undo 内部 codec 与场景执行接口约定。
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
