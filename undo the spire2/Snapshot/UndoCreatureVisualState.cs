// 文件说明：保存所有战斗 creature 的可恢复视觉缩放/色相状态。

namespace UndoTheSpire2;

internal sealed class UndoCreatureVisualState
{
    public required string CreatureKey { get; init; }

    public float? VisualDefaultScale { get; init; }

    public float? VisualHue { get; init; }

    public float? TempScale { get; init; }
}
