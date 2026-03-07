namespace UndoTheSpire2;

internal sealed class UndoSyntheticChoiceSession
{
    public UndoSyntheticChoiceSession(UndoSnapshot anchorSnapshot, UndoChoiceSpec choiceSpec)
    {
        AnchorSnapshot = anchorSnapshot;
        ChoiceSpec = choiceSpec;
        CachedBranches = [];
    }

    public UndoSnapshot AnchorSnapshot { get; }

    public UndoChoiceSpec ChoiceSpec { get; }

    public Dictionary<UndoChoiceResultKey, UndoSnapshot> CachedBranches { get; }
}