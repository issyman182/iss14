// iss14: GIFs in chat via GifSnap (chat timeouts / GIF mutes)
using System.Linq;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server.Administration.Managers;

/// <summary>
/// In-memory, timed chat restrictions keyed by user id (so they survive reconnects within the server's lifetime
/// and expire on their own). Two independent maps: a full chat timeout (IC speech, whisper, emote, OOC, LOOC,
/// dead chat and GIFs) and a GIF-only mute. Consulted at the chat entry points next to the rate-limit checks.
/// </summary>
public sealed class ChatTimeoutManager
{
    /// <summary>What a timeout covers.</summary>
    public enum Kind : byte
    {
        /// <summary>All player chat (and GIFs).</summary>
        Chat,

        /// <summary>Only GIF sends.</summary>
        Gif,
    }

    /// <summary>An active restriction.</summary>
    public readonly record struct Entry(NetUserId UserId, string Name, DateTime Until, string Reason, NetUserId? Admin)
    {
        public TimeSpan Remaining => Until - DateTime.UtcNow;
    }

    private readonly Dictionary<NetUserId, Entry> _chat = new();
    private readonly Dictionary<NetUserId, Entry> _gif = new();

    private Dictionary<NetUserId, Entry> Map(Kind kind) => kind == Kind.Gif ? _gif : _chat;

    /// <summary>Starts (or replaces) a restriction on the given user.</summary>
    public Entry Set(Kind kind, NetUserId userId, string name, TimeSpan duration, string reason, NetUserId? admin)
    {
        var entry = new Entry(userId, name, DateTime.UtcNow + duration, reason, admin);
        Map(kind)[userId] = entry;
        return entry;
    }

    /// <summary>Lifts a restriction early. Returns false if there was none.</summary>
    public bool Remove(Kind kind, NetUserId userId)
        => Map(kind).Remove(userId);

    /// <summary>
    /// Whether the user is currently restricted. Expired entries are dropped lazily here, so nothing has to tick.
    /// </summary>
    public bool TryGetActive(Kind kind, NetUserId userId, out Entry entry)
    {
        var map = Map(kind);
        if (!map.TryGetValue(userId, out entry))
            return false;

        if (entry.Until > DateTime.UtcNow)
            return true;

        map.Remove(userId);
        entry = default;
        return false;
    }

    public bool IsActive(Kind kind, ICommonSession session)
        => TryGetActive(kind, session.UserId, out _);

    /// <summary>All currently active restrictions of a kind, soonest to expire first.</summary>
    public List<Entry> Active(Kind kind)
    {
        var map = Map(kind);
        var now = DateTime.UtcNow;
        foreach (var key in map.Where(kv => kv.Value.Until <= now).Select(kv => kv.Key).ToList())
            map.Remove(key);

        return map.Values.OrderBy(e => e.Until).ToList();
    }

    /// <summary>Whole minutes left, rounded up (at least 1 while active).</summary>
    public static int RemainingMinutes(in Entry entry)
        => Math.Max(1, (int) Math.Ceiling(entry.Remaining.TotalMinutes));
}
