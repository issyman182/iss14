// iss14: Economy
#nullable enable
using System.Linq;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server.Cargo.Systems;
using Content.Server.Economy;
using Content.Shared.Cargo.Components;
using Content.Shared.CCVar;
using Content.Shared.Economy;
using NUnit.Framework;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests.Economy;

/// <summary>The central payment API: player/department/external transfers are atomic and respect balances.</summary>
[TestFixture]
public sealed class EconomySystemTest : GameTest
{
    [SidedDependency(Side.Server)] private readonly IBankManager _bank = null!;
    [SidedDependency(Side.Server)] private readonly EconomySystem _economy = null!;
    [SidedDependency(Side.Server)] private readonly CargoSystem _cargo = null!;
    [SidedDependency(Side.Server)] private readonly IEntityManager _entMan = null!;

    [Test]
    public async Task TransfersBetweenPartiesAreAtomic()
    {
        await OverrideCVar(Side.Server, CCVars.EconomyMaxBalance, 1_000_000L);

        var session = ServerSession!;
        var player = EconomyParty.Player(session.UserId);
        EntityUid station = default;

        await Server.WaitPost(() =>
        {
            station = _entMan.Spawn();
            _entMan.AddComponent<StationBankAccountComponent>(station);
            foreach (var account in _cargo.GetAccounts(station).Keys.ToList())
            {
                _cargo.TrySetBankAccount(station, account, 0);
            }
            _cargo.TrySetBankAccount(station, "Service", 1000);
            _cargo.TrySetBankAccount(station, "Cargo", 500);

            // Clean slate for the player.
            _bank.Credit(session.UserId, 1, "test", out _);
            if (_bank.TryDebit(session.UserId, _bank.GetBalance(session.UserId), "test", out _)) { }
        });

        var service = EconomyParty.Department(station, "Service");
        var stationBudget = EconomyParty.StationBudget(station);

        await Server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(_economy.GetBalance(player), Is.EqualTo(0));
                Assert.That(_economy.GetBalance(service), Is.EqualTo(1000));
                Assert.That(_economy.GetBalance(stationBudget), Is.EqualTo(500), "station budget is the primary (Cargo) account");

                // Broke player can't pay.
                Assert.That(_economy.TryCharge(player, 10, "vend", out var error), Is.False);
                Assert.That(error, Is.EqualTo(EconomyPaymentError.InsufficientFunds));
                Assert.That(_economy.GetBalance(player), Is.EqualTo(0));

                // Department pays the player, player pays the station.
                Assert.That(_economy.TryTransfer(service, player, 300, "task", out error), Is.True, error.ToString());
                Assert.That(_economy.GetBalance(service), Is.EqualTo(700));
                Assert.That(_economy.GetBalance(player), Is.EqualTo(300));

                Assert.That(_economy.TryTransfer(player, stationBudget, 100, "fine", out error), Is.True);
                Assert.That(_economy.GetBalance(player), Is.EqualTo(200));
                Assert.That(_economy.GetBalance(stationBudget), Is.EqualTo(600));

                // Over-budget department transfer fails without touching anything.
                Assert.That(_economy.TryTransfer(service, player, 5000, "too much", out error), Is.False);
                Assert.That(error, Is.EqualTo(EconomyPaymentError.InsufficientFunds));
                Assert.That(_economy.GetBalance(service), Is.EqualTo(700));
                Assert.That(_economy.GetBalance(player), Is.EqualTo(200));

                // Zero and negative amounts are rejected.
                Assert.That(_economy.TryTransfer(service, player, 0, "zero", out error), Is.False);
                Assert.That(error, Is.EqualTo(EconomyPaymentError.InvalidAmount));

                // Draining the station takes from all accounts proportionally and never below zero.
                var drained = _economy.DrainStation(station, 10_000, "heist");
                Assert.That(drained, Is.EqualTo(1300));
                Assert.That(_economy.GetBalance(service), Is.EqualTo(0));
                Assert.That(_economy.GetBalance(stationBudget), Is.EqualTo(0));
            });
        });

        await Server.WaitPost(() => _entMan.DeleteEntity(station));
    }
}
