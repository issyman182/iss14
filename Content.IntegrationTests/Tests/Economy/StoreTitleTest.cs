// iss14: Economy
#nullable enable
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Server.Economy.Store;
using Content.Shared.Preferences;
using NUnit.Framework;
using Robust.Shared.IoC;

namespace Content.IntegrationTests.Tests.Economy;

/// <summary>Per-character title override: only owned titles survive validation, and resolution falls back correctly.</summary>
[TestFixture]
public sealed class StoreTitleTest : GameTest
{
    [SidedDependency(Side.Server)] private readonly StoreManager _store = null!;

    [Test]
    public async Task ProfileTitleIsValidatedAndResolved()
    {
        var session = ServerSession!;
        var userId = session.UserId;

        // Start without the title, buy (grant) it afterwards.
        await Run(() => _store.RevokeAsync(userId, "TitleVeteran"));

        await Server.WaitAssertion(() =>
        {
            var collection = IoCManager.Instance!;

            var profile = HumanoidCharacterProfile.Random().WithTitle("TitleVeteran");
            profile.EnsureValid(session, collection);
            Assert.That(profile.Title, Is.Null, "an unowned title is reset to the account default");

            profile = HumanoidCharacterProfile.Random().WithTitle("NotAStoreItem");
            profile.EnsureValid(session, collection);
            Assert.That(profile.Title, Is.Null, "an unknown item is reset");

            profile = HumanoidCharacterProfile.Random().WithTitle(string.Empty);
            profile.EnsureValid(session, collection);
            Assert.That(profile.Title, Is.EqualTo(string.Empty), "'no title' is always allowed");

            Assert.That(_store.ResolveTitle(userId, "TitleVeteran"), Is.Null, "unowned titles resolve to nothing");
        });

        var granted = await Run(() => _store.GrantAsync(userId, "TitleVeteran"));
        Assert.That(granted, Is.True);

        await Server.WaitAssertion(() =>
        {
            var collection = IoCManager.Instance!;
            var profile = HumanoidCharacterProfile.Random().WithTitle("TitleVeteran");
            profile.EnsureValid(session, collection);
            Assert.That(profile.Title, Is.EqualTo("TitleVeteran"), "owned titles are kept");

            Assert.Multiple(() =>
            {
                Assert.That(_store.ResolveTitle(userId, "TitleVeteran"), Is.EqualTo("Veteran"));
                Assert.That(_store.ResolveTitle(userId, string.Empty), Is.Null, "explicit 'none' overrides the account default");
                Assert.That(_store.ResolveTitle(userId, null), Is.EqualTo(_store.GetTitleText(userId)), "null uses the account default");
            });
        });

        await Run(() => _store.RevokeAsync(userId, "TitleVeteran"));
    }

    /// <summary>Starts an async store operation on the server thread and ticks until it completes.</summary>
    private async Task<bool> Run(System.Func<Task<bool>> op)
    {
        Task<bool>? task = null;
        await Server.WaitPost(() => task = op());
        for (var i = 0; i < 200 && !task!.IsCompleted; i++)
            await Pair.RunTicksSync(1);
        Assert.That(task!.IsCompleted, Is.True, "store DB operation finished");
        return await task;
    }
}
