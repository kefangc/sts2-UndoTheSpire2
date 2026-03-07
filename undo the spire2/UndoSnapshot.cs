namespace UndoTheSpire2;

public sealed class UndoSnapshot
{
    public UndoSnapshot(UndoCombatFullState combatState, int replayEventCount, UndoActionKind actionKind, long sequenceId, string? actionLabel)
    {
        CombatState = combatState;
        ReplayEventCount = replayEventCount;
        ActionKind = actionKind;
        SequenceId = sequenceId;
        ActionLabel = string.IsNullOrWhiteSpace(actionLabel) ? GetDefaultLabel(actionKind) : actionLabel;
    }

    public UndoCombatFullState CombatState { get; }

    public int ReplayEventCount { get; }

    public UndoActionKind ActionKind { get; }

    public long SequenceId { get; }

    public string ActionLabel { get; }

    private static string GetDefaultLabel(UndoActionKind actionKind)
    {
        return actionKind switch
        {
            UndoActionKind.PlayCard => "Play card",
            UndoActionKind.UsePotion => "Use potion",
            UndoActionKind.DiscardPotion => "Discard potion",
            UndoActionKind.EndTurn => "End turn",
            UndoActionKind.PlayerChoice => "Choose",
            _ => "Undo"
        };
    }
}
