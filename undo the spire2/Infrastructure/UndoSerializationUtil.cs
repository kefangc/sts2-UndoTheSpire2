// 文件说明：提供通用序列化与深拷贝辅助。
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

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

        INetAction action = (INetAction)(Activator.CreateInstance(actionType)
            ?? throw new InvalidOperationException($"Could not create net action {actionType}."));
        action.Deserialize(reader);
        return action;
    }

    public static bool PacketDataEquals<T>(T left, T right) where T : IPacketSerializable
    {
        return SerializePacketSerializable(left).AsSpan().SequenceEqual(SerializePacketSerializable(right));
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
