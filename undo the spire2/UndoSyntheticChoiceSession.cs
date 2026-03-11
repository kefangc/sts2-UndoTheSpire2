namespace UndoTheSpire2;

internal sealed class UndoSyntheticChoiceSession
{
    public UndoSyntheticChoiceSession(UndoSnapshot anchorSnapshot, UndoChoiceSpec choiceSpec, UndoSnapshot? templateSnapshot = null)
    {
        AnchorSnapshot = anchorSnapshot;
        ChoiceSpec = choiceSpec;
        CachedBranches = [];
        TemplateSnapshot = templateSnapshot?.ChoiceResultKey != null ? templateSnapshot : null;
    }

    public UndoSnapshot AnchorSnapshot { get; }

    public UndoChoiceSpec ChoiceSpec { get; }

    public Dictionary<UndoChoiceResultKey, UndoSnapshot> CachedBranches { get; }

    public UndoSnapshot? TemplateSnapshot { get; private set; }

    public void RememberBranch(UndoChoiceResultKey choiceResultKey, UndoSnapshot snapshot, bool preferAsTemplate = true)
    {
        CachedBranches[choiceResultKey] = snapshot;
        if (preferAsTemplate && snapshot.ChoiceResultKey != null)
            TemplateSnapshot = snapshot;
    }
}
