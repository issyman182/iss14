// iss14: Economy
using Robust.Shared.Player;

namespace Content.Server.GameTicking.Events;

/// <summary>
/// Raised (broadcast) when a player session is disconnecting, right before its database-backed per-session
/// data (playtime trackers, bank cache, ...) is unloaded. Lets systems settle up with the leaving player.
/// </summary>
public sealed class PlayerDisconnectingEvent(ICommonSession session) : EntityEventArgs
{
    public readonly ICommonSession Session = session;
}
