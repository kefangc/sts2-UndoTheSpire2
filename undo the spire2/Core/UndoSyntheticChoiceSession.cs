// 文件说明：跟踪一次 synthetic choice 恢复过程的锚点、模板与缓存分支。
namespace UndoTheSpire2;

internal sealed class UndoSyntheticChoiceSession
{
    public UndoSyntheticChoiceSession(
        UndoSnapshot anchorSnapshot,
        UndoChoiceSpec choiceSpec,
        UndoSnapshot? templateSnapshot = null,
        bool requiresAuthoritativeBranchExecution = false)
    {
        AnchorSnapshot = anchorSnapshot;
        ChoiceSpec = choiceSpec;
        CachedBranches = [];
        TemplateSnapshot = templateSnapshot?.ChoiceResultKey != null ? templateSnapshot : null;
        RequiresAuthoritativeBranchExecution = requiresAuthoritativeBranchExecution;
    }

    public UndoSnapshot AnchorSnapshot { get; }

    public UndoChoiceSpec ChoiceSpec { get; }

    public Dictionary<UndoChoiceResultKey, UndoSnapshot> CachedBranches { get; }

    public UndoSnapshot? TemplateSnapshot { get; private set; }

    public bool RequiresAuthoritativeBranchExecution { get; }

    public void RememberBranch(UndoChoiceResultKey choiceResultKey, UndoSnapshot snapshot, bool preferAsTemplate = true)
    {
        CachedBranches[choiceResultKey] = snapshot;
        if (preferAsTemplate && snapshot.ChoiceResultKey != null)
            TemplateSnapshot = snapshot;
    }
}
