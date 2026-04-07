using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace UndoTheSpire2;

internal static class UndoDelayedCombatRewardService
{
    private static readonly object Sync = new();
    private static List<UndoPendingCombatRewardState> _pendingRewards = [];

    public static void QueueTheHuntReward(Player player, RoomType roomType)
    {
        lock (Sync)
        {
            _pendingRewards.Add(new UndoPendingCombatRewardState
            {
                Kind = UndoPendingCombatRewardKind.TheHuntCardReward,
                PlayerNetId = player.NetId,
                RoomType = roomType
            });
            UndoDebugLog.Write($"delayed_reward_queued kind=the_hunt player={player.NetId} roomType={roomType} pending={_pendingRewards.Count}");
        }
    }

    public static void QueueSwipeReward(Player player, CardModel deckVersion, ModelId encounterSourceId)
    {
        SerializableCard cardState = UndoSerializationUtil.ClonePacketSerializable(deckVersion.ToSerializable());
        lock (Sync)
        {
            _pendingRewards.Add(new UndoPendingCombatRewardState
            {
                Kind = UndoPendingCombatRewardKind.SwipeSpecialCardReward,
                PlayerNetId = player.NetId,
                RoomType = RoomType.Monster,
                Card = cardState,
                EncounterSourceId = encounterSourceId
            });
            UndoDebugLog.Write($"delayed_reward_queued kind=swipe player={player.NetId} encounter={encounterSourceId} pending={_pendingRewards.Count}");
        }
    }

    public static IReadOnlyList<UndoPendingCombatRewardState> CaptureSnapshot()
    {
        lock (Sync)
            return CloneStates(_pendingRewards);
    }

    public static void RestoreSnapshot(IReadOnlyList<UndoPendingCombatRewardState> snapshotStates)
    {
        lock (Sync)
            _pendingRewards = [.. CloneStates(snapshotStates)];
    }

    public static bool HasMatchingState(IReadOnlyList<UndoPendingCombatRewardState> snapshotStates)
    {
        lock (Sync)
        {
            if (_pendingRewards.Count != snapshotStates.Count)
                return false;

            for (int i = 0; i < _pendingRewards.Count; i++)
            {
                if (!StateEquals(_pendingRewards[i], snapshotStates[i]))
                    return false;
            }

            return true;
        }
    }

    public static void FlushPendingRewards(string reason)
    {
        List<UndoPendingCombatRewardState> pendingRewards;
        lock (Sync)
        {
            if (_pendingRewards.Count == 0)
                return;

            pendingRewards = _pendingRewards;
            _pendingRewards = [];
        }

        RunState? runState = RunManager.Instance.DebugOnlyGetState();
        if (runState?.CurrentRoom is not CombatRoom combatRoom)
        {
            UndoDebugLog.Write($"delayed_reward_flush_skipped reason={reason} detail=missing_combat_room pending={pendingRewards.Count}");
            return;
        }

        int flushed = 0;
        foreach (UndoPendingCombatRewardState pendingReward in pendingRewards)
        {
            Player? player = runState.GetPlayer(pendingReward.PlayerNetId);
            if (player == null)
            {
                UndoDebugLog.Write($"delayed_reward_flush_skipped reason={reason} detail=missing_player player={pendingReward.PlayerNetId} kind={pendingReward.Kind}");
                continue;
            }

            Reward? reward = CreateReward(runState, player, pendingReward);
            if (reward == null)
                continue;

            combatRoom.AddExtraReward(player, reward);
            flushed++;
        }

        UndoDebugLog.Write($"delayed_reward_flushed reason={reason} flushed={flushed} pending={pendingRewards.Count}");
    }

    public static void Clear(string reason)
    {
        lock (Sync)
        {
            if (_pendingRewards.Count == 0)
                return;

            UndoDebugLog.Write($"delayed_reward_cleared reason={reason} pending={_pendingRewards.Count}");
            _pendingRewards.Clear();
        }
    }

    private static Reward? CreateReward(RunState runState, Player player, UndoPendingCombatRewardState pendingReward)
    {
        return pendingReward.Kind switch
        {
            UndoPendingCombatRewardKind.TheHuntCardReward => new CardReward(CardCreationOptions.ForRoom(player, pendingReward.RoomType), 3, player),
            UndoPendingCombatRewardKind.SwipeSpecialCardReward => CreateSwipeReward(runState, player, pendingReward),
            _ => null
        };
    }

    private static Reward CreateSwipeReward(RunState runState, Player player, UndoPendingCombatRewardState pendingReward)
    {
        SerializableCard cardState = pendingReward.Card
            ?? throw new InvalidOperationException("Swipe delayed reward was missing card data.");
        CardModel card = CardModel.FromSerializable(UndoSerializationUtil.ClonePacketSerializable(cardState));
        runState.AddCard(card, player);

        SpecialCardReward reward = new(card, player);
        if (pendingReward.EncounterSourceId != ModelId.none)
            reward.SetCustomDescriptionEncounterSource(pendingReward.EncounterSourceId);
        return reward;
    }

    private static IReadOnlyList<UndoPendingCombatRewardState> CloneStates(IReadOnlyList<UndoPendingCombatRewardState> source)
    {
        return [.. source.Select(CloneState)];
    }

    private static UndoPendingCombatRewardState CloneState(UndoPendingCombatRewardState state)
    {
        return new UndoPendingCombatRewardState
        {
            Kind = state.Kind,
            PlayerNetId = state.PlayerNetId,
            RoomType = state.RoomType,
            Card = state.Card == null ? null : UndoSerializationUtil.ClonePacketSerializable(state.Card),
            EncounterSourceId = state.EncounterSourceId
        };
    }

    private static bool StateEquals(UndoPendingCombatRewardState left, UndoPendingCombatRewardState right)
    {
        if (left.Kind != right.Kind
            || left.PlayerNetId != right.PlayerNetId
            || left.RoomType != right.RoomType
            || left.EncounterSourceId != right.EncounterSourceId)
        {
            return false;
        }

        if ((left.Card == null) != (right.Card == null))
            return false;

        return left.Card == null
            || (right.Card != null && UndoSerializationUtil.PacketDataEquals(left.Card, right.Card));
    }
}
