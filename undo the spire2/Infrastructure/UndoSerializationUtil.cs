// 文件说明：提供通用序列化与深拷贝辅助。
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace UndoTheSpire2;

internal static class UndoSerializationUtil
{
    public static NetFullCombatState CloneFullState(NetFullCombatState state)
    {
        return ClonePacketSerializable(state);
    }

    public static T ClonePacketSerializable<T>(T value) where T : IPacketSerializable, new()
    {
        PacketReader reader = new();
        reader.Reset(SerializePacketSerializable(value));
        return reader.Read<T>();
    }

    public static INetAction CloneNetAction(INetAction value)
    {
        PacketReader reader = new();
        reader.Reset(SerializeNetAction(value));
        int typeId = (int)reader.ReadByte(8);
        if (!ActionTypes.TryGetActionType(typeId, out Type? actionType))
            throw new InvalidOperationException($"Unknown net action type id {typeId}.");
        if (actionType == null)
            throw new InvalidOperationException($"Action type id {typeId} resolved to null.");

        INetAction action = (INetAction)(Activator.CreateInstance(actionType)
            ?? throw new InvalidOperationException($"Could not create net action {actionType}."));
        action.Deserialize(reader);
        return action;
    }

    public static bool PacketDataEquals<T>(T left, T right) where T : IPacketSerializable
    {
        return SerializePacketSerializable(left).AsSpan().SequenceEqual(SerializePacketSerializable(right));
    }

    public static string GetPacketFingerprint<T>(T value) where T : IPacketSerializable
    {
        return Convert.ToBase64String(SerializePacketSerializable(value));
    }

    public static SerializableReward CloneSerializableReward(SerializableReward value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return new SerializableReward
        {
            RewardType = value.RewardType,
            SpecialCard = value.SpecialCard == null ? null! : ClonePacketSerializable(value.SpecialCard),
            GoldAmount = value.GoldAmount,
            WasGoldStolenBack = value.WasGoldStolenBack,
            Source = value.Source,
            RarityOdds = value.RarityOdds,
            CardPoolIds = value.CardPoolIds == null ? [] : [.. value.CardPoolIds],
            OptionCount = value.OptionCount,
            CustomDescriptionEncounterSourceId = value.CustomDescriptionEncounterSourceId
        };
    }

    public static bool SerializableRewardEquals(SerializableReward left, SerializableReward right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (left.RewardType != right.RewardType
            || left.GoldAmount != right.GoldAmount
            || left.WasGoldStolenBack != right.WasGoldStolenBack
            || left.Source != right.Source
            || left.RarityOdds != right.RarityOdds
            || left.OptionCount != right.OptionCount
            || left.CustomDescriptionEncounterSourceId != right.CustomDescriptionEncounterSourceId)
        {
            return false;
        }

        if ((left.SpecialCard == null) != (right.SpecialCard == null))
            return false;

        if (left.SpecialCard != null && right.SpecialCard != null && !PacketDataEquals(left.SpecialCard, right.SpecialCard))
            return false;

        IReadOnlyList<ModelId> leftPools = left.CardPoolIds ?? [];
        IReadOnlyList<ModelId> rightPools = right.CardPoolIds ?? [];
        if (leftPools.Count != rightPools.Count)
            return false;

        for (int i = 0; i < leftPools.Count; i++)
        {
            if (leftPools[i] != rightPools[i])
                return false;
        }

        return true;
    }

    private static byte[] SerializePacketSerializable<T>(T value) where T : IPacketSerializable
    {
        PacketWriter writer = new() { WarnOnGrow = false };
        writer.Write(value);
        writer.ZeroByteRemainder();

        byte[] buffer = new byte[writer.BytePosition];
        Array.Copy(writer.Buffer, buffer, writer.BytePosition);
        return buffer;
    }

    private static byte[] SerializeNetAction(INetAction action)
    {
        PacketWriter writer = new() { WarnOnGrow = false };
        writer.WriteByte((byte)action.ToId(), 8);
        writer.Write(action);
        writer.ZeroByteRemainder();

        byte[] buffer = new byte[writer.BytePosition];
        Array.Copy(writer.Buffer, buffer, writer.BytePosition);
        return buffer;
    }
}
