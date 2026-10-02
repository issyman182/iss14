// iss14: Economy
using System.Threading.Tasks;
using Robust.Shared.Network;

namespace Content.Server.Economy;

/// <summary>
/// Persistent, game-wide bank accounts (one per player across all characters).
/// Connected players are served from a cache that writes through to the database; offline players are
/// read/written directly in the database via the *Async methods.
/// </summary>
public interface IBankManager
{
    /// <summary>True once the player's balance was loaded from the database (i.e. they are connected).</summary>
    bool IsLoaded(NetUserId userId);

    /// <summary>Cached balance of a connected player; 0 for unknown players.</summary>
    long GetBalance(NetUserId userId);

    /// <summary>
    /// Takes <paramref name="amount"/> (&gt; 0) from a connected player's account. Fails if the player isn't loaded
    /// or the balance is insufficient.
    /// </summary>
    bool TryDebit(NetUserId userId, long amount, string reason, out long newBalance);

    /// <summary>Adds <paramref name="amount"/> (&gt; 0) to a connected player's account, capped at the max balance.</summary>
    bool Credit(NetUserId userId, long amount, string reason, out long newBalance, bool salary = false);

    /// <summary>Moves <paramref name="amount"/> between two connected players' accounts.</summary>
    bool TryTransfer(NetUserId from, NetUserId to, long amount, string reason);

    /// <summary>Balance of any player (connected or not), straight from the database.</summary>
    Task<long> GetBalanceAsync(NetUserId userId);

    /// <summary>Sets the balance of any player (connected or not); refreshes the cache if they are connected.</summary>
    Task SetBalanceAsync(NetUserId userId, long balance, string reason);

    /// <summary>Adjusts the balance of any player (connected or not). Returns the new balance or null if it would go negative.</summary>
    Task<long?> AdjustBalanceAsync(NetUserId userId, long delta, string reason);

    /// <summary>Waits for all pending database writes (tests, shutdown).</summary>
    Task FlushAsync();

    /// <summary>Raised on the entity event bus as <see cref="BankBalanceChangedEvent"/> too; this is for managers.</summary>
    event Action<BankBalanceChangedEvent>? BalanceChanged;
}

/// <summary>Broadcast whenever a bank balance changes, so UIs (ATM, PDA) can refresh.</summary>
public sealed class BankBalanceChangedEvent(NetUserId userId, long oldBalance, long newBalance, string reason) : EntityEventArgs
{
    public readonly NetUserId UserId = userId;
    public readonly long OldBalance = oldBalance;
    public readonly long NewBalance = newBalance;
    public readonly string Reason = reason;
}
