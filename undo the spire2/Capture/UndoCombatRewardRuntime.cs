using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace UndoTheSpire2;

internal static class UndoCombatRewardRuntime
{
    public static void ClearLiveExtraRewards(RunState runState)
    {
        if (runState.CurrentRoom is not CombatRoom combatRoom)
            return;

        if (!UndoReflectionUtil.TryGetFieldValue(combatRoom, "_extraRewards", out Dictionary<Player, List<Reward>>? liveExtraRewards)
            || liveExtraRewards == null)
            return;

        CleanupLiveRewardArtifacts(runState, liveExtraRewards);
        liveExtraRewards.Clear();
    }

    private static void CleanupLiveRewardArtifacts(RunState runState, Dictionary<Player, List<Reward>> liveExtraRewards)
    {
        foreach (List<Reward> rewards in liveExtraRewards.Values)
        {
            foreach (Reward reward in rewards)
                CleanupRewardArtifacts(runState, reward);
        }
    }

    private static void CleanupRewardArtifacts(RunState runState, Reward reward)
    {
        if (reward is not SpecialCardReward)
            return;

        if (!UndoReflectionUtil.TryGetFieldValue(reward, "_card", out CardModel? card)
            || card == null
            || card.Pile != null
            || !runState.ContainsCard(card))
        {
            return;
        }

        runState.RemoveCard(card);
    }
}
