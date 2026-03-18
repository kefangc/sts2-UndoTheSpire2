using System;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;

namespace UndoTheSpire2;

internal static class UndoMonsterMoveStateUtil
{
    public static bool TryGetStarterMoveIndex(MonsterModel monster, out int starterMoveIndex)
    {
        if (UndoReflectionUtil.FindProperty(monster.GetType(), "StarterMoveIndex")?.GetValue(monster) is int starterMoveIndexValue)
        {
            starterMoveIndex = starterMoveIndexValue;
            return true;
        }

        if (UndoReflectionUtil.FindProperty(monster.GetType(), "StarterMoveIdx")?.GetValue(monster) is int starterMoveIdxValue)
        {
            starterMoveIndex = starterMoveIdxValue;
            return true;
        }

        starterMoveIndex = default;
        return false;
    }

    public static void TrySetStarterMoveIndex(MonsterModel monster, int starterMoveIndex)
    {
        if (!UndoReflectionUtil.TrySetPropertyValue(monster, "StarterMoveIndex", starterMoveIndex))
            UndoReflectionUtil.TrySetPropertyValue(monster, "StarterMoveIdx", starterMoveIndex);
    }

    public static bool ShouldKeepDeadState(MonsterMoveStateMachine moveStateMachine, UndoMonsterState state, MoveState deadState)
    {
        string? savedNextMoveId = state.NextMoveId;
        if (string.IsNullOrWhiteSpace(savedNextMoveId))
            return true;

        if (string.Equals(savedNextMoveId, deadState.Id, StringComparison.Ordinal))
            return true;

        return !moveStateMachine.States.TryGetValue(savedNextMoveId, out MonsterState? savedNextState)
            || savedNextState is not MoveState;
    }

    public static bool HasVisibleNextIntent(Creature creature)
    {
        return creature.Monster?.NextMove?.Intents.Count > 0;
    }
}
