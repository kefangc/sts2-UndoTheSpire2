// 文件说明：定义撤销历史中的动作类别。
namespace UndoTheSpire2;

public enum UndoActionKind
{
    PlayCard,
    UsePotion,
    DiscardPotion,
    EndTurn,
    PlayerChoice
}
