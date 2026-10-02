// iss14: Economy
#nullable enable
using System.Linq;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server.Database;
using Content.Server.Economy;
using Content.Shared.CCVar;
using NUnit.Framework;
using Robust.Shared.Network;

namespace Content.IntegrationTests.Tests.Economy;

/// <summary>
/// Credit/debit/transfer rules of the <see cref="IBankManager"/> cache, the max-balance cap, the
/// never-negative rule, and the write-through to the (sqlite) database incl. the offline API and leaderboard.
/// </summary>
[TestFixture]
public sealed class BankManagerTest : GameTest
{
    [SidedDependency(Side.Server)] private readonly IBankManager _bank = null!;
    [SidedDependency(Side.Server)] private readonly IServerDbManager _db = null!;

    [Test]
    public async Task CreditDebitTransferAndCaps()
    {
        await OverrideCVar(Side.Server, CCVars.EconomyMaxBalance, 1_000L);

        var user = ServerSession!.UserId;
        Assert.That(_bank.IsLoaded(user), Is.True, "connected player's account should be cached");

        await Server.WaitPost(() =>
        {
            if (_bank.GetBalance(user) > 0)
                _bank.TryDebit(user, _bank.GetBalance(user), "test reset", out _);
        });
        Assert.That(_bank.GetBalance(user), Is.EqualTo(0));

        await Server.WaitAssertion(() =>
        {
            // Credit.
            Assert.That(_bank.Credit(user, 500, "test", out var balance), Is.True);
            Assert.That(balance, Is.EqualTo(500));
            Assert.That(_bank.GetBalance(user), Is.EqualTo(500));

            // Credit is capped at the max balance, not rejected.
            Assert.That(_bank.Credit(user, 10_000, "test cap", out balance), Is.True);
            Assert.That(balance, Is.EqualTo(1_000));

            // Debit works and never goes negative.
            Assert.That(_bank.TryDebit(user, 400, "test", out balance), Is.True);
            Assert.That(balance, Is.EqualTo(600));
            Assert.That(_bank.TryDebit(user, 601, "test overdraft", out _), Is.False);
            Assert.That(_bank.GetBalance(user), Is.EqualTo(600));

            // Zero/negative amounts are rejected.
            Assert.That(_bank.Credit(user, 0, "test", out _), Is.False);
            Assert.That(_bank.TryDebit(user, -5, "test", out _), Is.False);

            // Transfers need two loaded accounts; an unknown target (or self) fails and costs nothing.
            Assert.That(_bank.TryTransfer(user, new NetUserId(Guid.NewGuid()), 100, "test"), Is.False);
            Assert.That(_bank.TryTransfer(user, user, 100, "test"), Is.False);
            Assert.That(_bank.GetBalance(user), Is.EqualTo(600));
        });

        // Everything was written through to the database.
        await _bank.FlushAsync();
        Assert.That(await _db.GetBankBalanceAsync(user), Is.EqualTo(600));
    }

    [Test]
    public async Task OfflineAccountsAndLeaderboard()
    {
        await OverrideCVar(Side.Server, CCVars.EconomyMaxBalance, 1_000_000L);

        // A player that has never connected: operated on straight in the database.
        var offline = new NetUserId(Guid.NewGuid());
        Assert.That(_bank.IsLoaded(offline), Is.False);
        Assert.That(await OnServer(() => _bank.GetBalanceAsync(offline)), Is.EqualTo(0));

        await OnServer(() => _bank.SetBalanceAsync(offline, 250, "test set"));
        Assert.That(await OnServer(() => _bank.GetBalanceAsync(offline)), Is.EqualTo(250));

        Assert.That(await OnServer(() => _bank.AdjustBalanceAsync(offline, 100, "test add")), Is.EqualTo(350));
        Assert.That(await OnServer(() => _bank.AdjustBalanceAsync(offline, -1000, "test overdraft")), Is.Null, "must not go negative");
        Assert.That(await OnServer(() => _bank.GetBalanceAsync(offline)), Is.EqualTo(350));

        // Negative set is clamped to zero.
        await OnServer(() => _bank.SetBalanceAsync(offline, -5, "test clamp"));
        Assert.That(await OnServer(() => _bank.GetBalanceAsync(offline)), Is.EqualTo(0));

        // Leaderboard: the richest first.
        var rich = new NetUserId(Guid.NewGuid());
        await _db.SetBankBalanceAsync(rich, 999_999);
        var top = await _db.GetTopBankAccountsAsync(50);
        Assert.That(top, Is.Not.Empty);
        Assert.That(top[0].UserId, Is.EqualTo(rich));
        Assert.That(top.Select(t => t.Balance), Is.Ordered.Descending);

        // Lifetime earned only counts credits, never debits.
        var earner = new NetUserId(Guid.NewGuid());
        Assert.That(await _db.AdjustBankBalanceAsync(earner, 300, false, 0), Is.EqualTo(300));
        Assert.That(await _db.AdjustBankBalanceAsync(earner, -100, false, 0), Is.EqualTo(200));
        var row = (await _db.GetTopBankAccountsAsync(1000)).Single(t => t.UserId == earner);
        Assert.That(row.LifetimeEarned, Is.EqualTo(300));
    }

    /// <summary>Starts an async bank operation on the server thread and pumps ticks until it completes.</summary>
    private async Task<T> OnServer<T>(Func<Task<T>> operation)
    {
        Task<T> task = null!;
        await Server.WaitPost(() => task = operation());
        while (!task.IsCompleted)
            await Server.WaitRunTicks(1);
        return await task;
    }

    private async Task OnServer(Func<Task> operation)
    {
        Task task = null!;
        await Server.WaitPost(() => task = operation());
        while (!task.IsCompleted)
            await Server.WaitRunTicks(1);
        await task;
    }
}
