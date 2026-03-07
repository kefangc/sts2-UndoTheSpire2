namespace UndoTheSpire2;

internal sealed class UndoSnapshot
{
    public UndoSnapshot(
        UndoCombatFullState combatState,
        int replayEventCount,
        UndoActionKind actionKind,
        long sequenceId,
        string? actionLabel,
        bool isChoiceAnchor = false,
        UndoChoiceSpec? choiceSpec = null,
        UndoChoiceResultKey? choiceResultKey = null)
    {
        CombatState = combatState;
        ReplayEventCount = replayEventCount;
        ActionKind = actionKind;
        SequenceId = sequenceId;
        ActionLabel = string.IsNullOrWhiteSpace(actionLabel) ? GetDefaultLabel(actionKind) : actionLabel;
        IsChoiceAnchor = isChoiceAnchor;
        ChoiceSpec = choiceSpec;
        ChoiceResultKey = choiceResultKey;
    }

    public UndoCombatFullState CombatState { get; }

    public int ReplayEventCount { get; }

    public UndoActionKind ActionKind { get; }

    public long SequenceId { get; }

    public string ActionLabel { get; }

    public bool IsChoiceAnchor { get; }

    public UndoChoiceSpec? ChoiceSpec { get; }

    public UndoChoiceResultKey? ChoiceResultKey { get; }

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
