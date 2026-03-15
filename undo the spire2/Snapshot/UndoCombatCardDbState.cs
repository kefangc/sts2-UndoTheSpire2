// 文件说明：保存战斗内卡牌数据库及其派生状态。
namespace UndoTheSpire2;

// Captures the transient NetCombatCardDb mapping so restore can preserve
// combat card ids for live hand/input/action synchronization.
internal sealed class UndoCombatCardDbState
{
    public IReadOnlyList<UndoCombatCardDbEntryState> Entries { get; init; } = [];

    public uint NextId { get; init; }
}

internal sealed class UndoCombatCardDbEntryState
{
    public required uint CombatCardId { get; init; }

    public required CardRef Card { get; init; }
}
