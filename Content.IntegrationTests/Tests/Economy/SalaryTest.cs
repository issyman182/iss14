// iss14: Economy
#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server.Economy;
using Content.Server.Players.PlayTimeTracking;
using Content.Shared.CCVar;
using Content.Shared.Economy;
using Content.Shared.Players.PlayTimeTracking;
using NUnit.Framework;

namespace Content.IntegrationTests.Tests.Economy;

/// <summary>
/// Salary math: the pure calculation, the per-job line computation against a baseline, and an end-to-end payout
/// with faked tracker time (deduplicated so a second payout pays nothing).
/// </summary>
[TestFixture]
public sealed class SalaryTest : GameTest
{
    [SidedDependency(Side.Server)] private readonly IBankManager _bank = null!;
    [SidedDependency(Side.Server)] private readonly PlayTimeTrackingManager _tracking = null!;
    [SidedDependency(Side.Server)] private readonly SalarySystem _salary = null!;

    [Test]
    public void PureMath()
    {
        Assert.Multiple(() =>
        {
            Assert.That(EconomyPricing.Salary(60, 150, 1f), Is.EqualTo(150));
            Assert.That(EconomyPricing.Salary(30, 150, 1f), Is.EqualTo(75));
            Assert.That(EconomyPricing.Salary(30, 150, 2f), Is.EqualTo(150));
            Assert.That(EconomyPricing.Salary(1, 10, 1f), Is.EqualTo(0), "rounds down to whole credits");
            Assert.That(EconomyPricing.Salary(0, 150, 1f), Is.EqualTo(0));
            Assert.That(EconomyPricing.Salary(60, 150, 0f), Is.EqualTo(0));
            Assert.That(EconomyPricing.VendPrice(0, 1f), Is.EqualTo(0));
            Assert.That(EconomyPricing.VendPrice(0.2, 1f), Is.EqualTo(1), "never below 1 when priced");
            Assert.That(EconomyPricing.VendPrice(10, 2.5f), Is.EqualTo(25));
        });
    }

    [Test]
    public async Task ComputeLinesUsesJobTrackersOnly()
    {
        await OverrideCVar(Side.Server, CCVars.EconomySalaryMultiplier, 1f);
        await OverrideCVar(Side.Server, CCVars.EconomySalaryDefaultPerHour, 30);

        var baseline = new Dictionary<string, TimeSpan>
        {
            ["JobCaptain"] = TimeSpan.FromHours(10),
            [PlayTimeTrackingShared.TrackerOverall] = TimeSpan.FromHours(100),
        };
        var current = new Dictionary<string, TimeSpan>
        {
            ["JobCaptain"] = TimeSpan.FromHours(11), // +60 min as captain (150/h)
            ["JobPassenger"] = TimeSpan.FromMinutes(30), // +30 min as passenger (10/h) -> 5
            [PlayTimeTrackingShared.TrackerOverall] = TimeSpan.FromHours(200), // never paid
        };
        var paid = new Dictionary<string, TimeSpan>
        {
            ["JobCaptain"] = TimeSpan.FromMinutes(30), // already paid half of it
        };

        List<SalaryLine> lines = null!;
        await Server.WaitPost(() => lines = _salary.ComputeLines(baseline, current, paid));

        Assert.Multiple(() =>
        {
            Assert.That(lines.Select(l => l.Tracker), Is.EquivalentTo(new[] { "JobCaptain", "JobPassenger" }));
            var captain = lines.Single(l => l.Tracker == "JobCaptain");
            Assert.That(captain.Time, Is.EqualTo(TimeSpan.FromMinutes(30)));
            Assert.That(captain.Credits, Is.EqualTo(75));
            var passenger = lines.Single(l => l.Tracker == "JobPassenger");
            Assert.That(passenger.Credits, Is.EqualTo(5));
            Assert.That(_salary.GetHourlyRate("Captain"), Is.EqualTo(150));
            Assert.That(_salary.GetHourlyRate("DefinitelyNotAJob"), Is.EqualTo(30), "unlisted jobs use the default cvar");
        });
    }

    [Test]
    public async Task PayoutIsDeduplicated()
    {
        await OverrideCVar(Side.Server, CCVars.EconomySalaryEnabled, true);
        await OverrideCVar(Side.Server, CCVars.EconomySalaryMultiplier, 1f);
        await OverrideCVar(Side.Server, CCVars.EconomyMaxBalance, 1_000_000L);

        var session = ServerSession!;
        Assert.That(_bank.IsLoaded(session.UserId), Is.True);

        long before = 0;
        long first = 0;
        long second = 0;
        await Server.WaitPost(() =>
        {
            // Settle anything accrued so far so the test starts from a clean slate.
            _salary.PayOut(session);
            Assert.That(_salary.HasBaseline(session), Is.True, "baseline is taken when the session enters the game");

            before = _bank.GetBalance(session.UserId);

            // Fake one hour as captain and one hour of overall time (which must not be paid).
            _tracking.AddTimeToTracker(session, "JobCaptain", TimeSpan.FromHours(1));
            _tracking.AddTimeToOverallPlaytime(session, TimeSpan.FromHours(1));

            first = _salary.PayOut(session);
            second = _salary.PayOut(session);
        });

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(150), "60 min as captain at 150/h");
            Assert.That(second, Is.EqualTo(0), "the same minutes are never paid twice");
            Assert.That(_bank.GetBalance(session.UserId), Is.EqualTo(before + 150));
        });

        // Disabled salary pays nothing even with unpaid time.
        await OverrideCVar(Side.Server, CCVars.EconomySalaryEnabled, false);
        long disabled = 1;
        await Server.WaitPost(() =>
        {
            _tracking.AddTimeToTracker(session, "JobCaptain", TimeSpan.FromHours(1));
            disabled = _salary.PayOut(session);
        });
        Assert.That(disabled, Is.EqualTo(0));

        // Re-enabling pays the hour that accrued while disabled (it was never settled).
        await OverrideCVar(Side.Server, CCVars.EconomySalaryEnabled, true);
        long reenabled = 0;
        await Server.WaitPost(() => reenabled = _salary.PayOut(session));
        Assert.That(reenabled, Is.EqualTo(150));
    }
}
