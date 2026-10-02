// iss14: Economy
#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server.Economy;
using Content.Shared.CCVar;
using Content.Shared.Economy;
using Content.Shared.Roles;
using NUnit.Framework;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.Economy;

/// <summary>
/// Salary math: the presence factor, the time-weighted payout (job switching can't be exploited), base salary
/// lookup with overrides, and an end-to-end round-end payout that is paid exactly once.
/// </summary>
[TestFixture]
public sealed class SalaryTest : GameTest
{
    [SidedDependency(Side.Server)] private readonly IBankManager _bank = null!;
    [SidedDependency(Side.Server)] private readonly SalarySystem _salary = null!;

    [Test]
    public void PresenceFactor()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SalarySystem.PresenceFactor(0, 0.2f, 0.75f, 1.5f), Is.EqualTo(0.2f).Within(0.001f));
            Assert.That(SalarySystem.PresenceFactor(0.375, 0.2f, 0.75f, 1.5f), Is.EqualTo(0.6f).Within(0.001f), "halfway to full time");
            Assert.That(SalarySystem.PresenceFactor(0.75, 0.2f, 0.75f, 1.5f), Is.EqualTo(1f).Within(0.001f));
            Assert.That(SalarySystem.PresenceFactor(1, 0.2f, 0.75f, 1.5f), Is.EqualTo(1.5f).Within(0.001f));
            Assert.That(SalarySystem.PresenceFactor(5, 0.2f, 0.75f, 1.5f), Is.EqualTo(1.5f).Within(0.001f), "clamped");
            Assert.That(SalarySystem.PresenceFactor(1, 0.2f, 1f, 1.5f), Is.EqualTo(1.5f).Within(0.001f), "full fraction 1 still pays the max at the end");
            Assert.That(EconomyPricing.VendPrice(0, 1f), Is.EqualTo(0));
            Assert.That(EconomyPricing.VendPrice(0.2, 1f), Is.EqualTo(1), "never below 1 when priced");
            Assert.That(EconomyPricing.VendPrice(10, 2.5f), Is.EqualTo(25));
        });
    }

    [Test]
    public async Task TimeWeightedPayoutCannotBeGamed()
    {
        await OverrideCVar(Side.Server, CCVars.EconomySalaryMultiplier, 1f);
        await OverrideCVar(Side.Server, CCVars.EconomySalaryDefault, 60);
        await OverrideCVar(Side.Server, CCVars.EconomySalaryMinFactor, 0.2f);
        await OverrideCVar(Side.Server, CCVars.EconomySalaryFullTimeFraction, 0.75f);
        await OverrideCVar(Side.Server, CCVars.EconomySalaryMaxFactor, 1.5f);

        var round = TimeSpan.FromHours(2);
        long captainOnly = 0;
        long switched = 0;
        long passengerOnly = 0;
        long unlisted = 0;
        int captainBase = 0;

        await Server.WaitPost(() =>
        {
            captainBase = _salary.GetBaseSalary("Captain");
            Assert.That(_salary.GetBaseSalary("DefinitelyNotAJob"), Is.EqualTo(60), "unlisted jobs use the default cvar");

            captainOnly = _salary.ComputePayout(new Dictionary<ProtoId<JobPrototype>, TimeSpan> { ["Captain"] = round }, round, out var f1, out _);
            Assert.That(f1, Is.EqualTo(1.5f).Within(0.001f));

            // Half the round as captain, half as passenger: must be the time-weighted average, never more than captain-only.
            switched = _salary.ComputePayout(new Dictionary<ProtoId<JobPrototype>, TimeSpan>
            {
                ["Captain"] = round / 2,
                ["Passenger"] = round / 2,
            }, round, out _, out _);

            passengerOnly = _salary.ComputePayout(new Dictionary<ProtoId<JobPrototype>, TimeSpan> { ["Passenger"] = round }, round, out _, out _);
            unlisted = _salary.ComputePayout(new Dictionary<ProtoId<JobPrototype>, TimeSpan> { ["Captain"] = TimeSpan.Zero }, round, out _, out _);
        });

        Assert.Multiple(() =>
        {
            Assert.That(captainOnly, Is.EqualTo((long) Math.Floor(captainBase * 1.5)));
            Assert.That(switched, Is.LessThan(captainOnly));
            Assert.That(switched, Is.GreaterThan(passengerOnly));
            Assert.That(switched, Is.EqualTo((captainOnly + passengerOnly) / 2).Within(2));
            Assert.That(unlisted, Is.EqualTo(0), "no time, no pay");
        });
    }

    [Test]
    public async Task OverridesArePreferred()
    {
        int before = 0;
        int overridden = 0;
        int reset = 0;
        await Server.WaitPost(() =>
        {
            before = _salary.GetBaseSalary("Captain");
            _salary.SetOverride("Captain", 999);
            overridden = _salary.GetBaseSalary("Captain");
            Assert.That(_salary.HasOverride("Captain"), Is.True);
            _salary.SetOverride("Captain", null);
            reset = _salary.GetBaseSalary("Captain");
        });

        Assert.Multiple(() =>
        {
            Assert.That(overridden, Is.EqualTo(999));
            Assert.That(reset, Is.EqualTo(before));
            Assert.That(_salary.GetPrototypeSalary("Captain"), Is.EqualTo(before));
        });
    }

    [Test]
    public async Task RoundEndPaysOnce()
    {
        await OverrideCVar(Side.Server, CCVars.EconomySalaryEnabled, true);
        await OverrideCVar(Side.Server, CCVars.EconomySalaryMultiplier, 1f);
        await OverrideCVar(Side.Server, CCVars.EconomyMaxBalance, 1_000_000L);
        await OverrideCVar(Side.Server, CCVars.EconomyFinesFromSalary, false);

        var session = ServerSession!;
        Assert.That(_bank.IsLoaded(session.UserId), Is.True);

        long before = 0;
        (int Count, long Total) first = default;
        (int Count, long Total) second = default;
        await Server.WaitPost(() =>
        {
            _salary.ResetRound();
            before = _bank.GetBalance(session.UserId);
            _salary.Accrue(session.UserId, "Captain", TimeSpan.FromHours(1));
            first = _salary.PayAll();
            second = _salary.PayAll();
        });

        // Flush the async credit path if the bank had to go through the database.
        await _bank.FlushAsync();

        Assert.Multiple(() =>
        {
            Assert.That(first.Count, Is.GreaterThanOrEqualTo(1));
            Assert.That(first.Total, Is.GreaterThan(0));
            Assert.That(second.Count, Is.EqualTo(0), "a round is paid once");
            Assert.That(_bank.GetBalance(session.UserId), Is.EqualTo(before + first.Total));
        });
    }
}
