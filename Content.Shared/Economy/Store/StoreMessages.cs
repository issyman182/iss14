// iss14: Economy
using Lidgren.Network;
using Robust.Shared.Network;
using Robust.Shared.Serialization;

namespace Content.Shared.Economy.Store;

/// <summary>Server -> client: the player's balance, owned store items and worn title.</summary>
public sealed class MsgStoreState : NetMessage
{
    public override MsgGroups MsgGroup => MsgGroups.EntityEvent;

    public long Balance;
    public HashSet<string> Owned = new();
    public string? Title;
    public string Status = string.Empty;

    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer)
    {
        Balance = buffer.ReadInt64();
        var count = buffer.ReadVariableInt32();
        Owned = new HashSet<string>(count);
        for (var i = 0; i < count; i++)
        {
            Owned.Add(buffer.ReadString());
        }

        var hasTitle = buffer.ReadBoolean();
        Title = hasTitle ? buffer.ReadString() : null;
        Status = buffer.ReadString();
    }

    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer)
    {
        buffer.Write(Balance);
        buffer.WriteVariableInt32(Owned.Count);
        foreach (var id in Owned)
        {
            buffer.Write(id);
        }

        buffer.Write(Title != null);
        if (Title != null)
            buffer.Write(Title);
        buffer.Write(Status);
    }
}

/// <summary>Client -> server: buy a store item.</summary>
public sealed class MsgStoreBuy : NetMessage
{
    public override MsgGroups MsgGroup => MsgGroups.EntityEvent;

    public string Item = string.Empty;

    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer)
    {
        Item = buffer.ReadString();
    }

    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer)
    {
        buffer.Write(Item);
    }
}

/// <summary>Client -> server: wear a title (empty = none).</summary>
public sealed class MsgStoreSetTitle : NetMessage
{
    public override MsgGroups MsgGroup => MsgGroups.EntityEvent;

    public string Title = string.Empty;

    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer)
    {
        Title = buffer.ReadString();
    }

    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer)
    {
        buffer.Write(Title);
    }
}

/// <summary>Client -> server: please resend the store state.</summary>
public sealed class MsgStoreRequest : NetMessage
{
    public override MsgGroups MsgGroup => MsgGroups.EntityEvent;

    public override void ReadFromBuffer(NetIncomingMessage buffer, IRobustSerializer serializer)
    {
    }

    public override void WriteToBuffer(NetOutgoingMessage buffer, IRobustSerializer serializer)
    {
    }
}

/// <summary>Read side of the player's unlocks, shared so loadout requirements can check them on both sides.</summary>
public interface ISharedStoreManager
{
    /// <summary>Whether the session (client: the local player, any session) owns a store item.</summary>
    bool HasUnlock(Robust.Shared.Player.ICommonSession session, string item);
}
