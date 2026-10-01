// iss14: GIFs in chat via GifSnap
#nullable enable
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Content.Client.Gifs;
using Content.Client.UserInterface.Systems.Chat;
using Content.IntegrationTests.Fixtures;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.Shared.CCVar;
using Content.Shared.Chat;
using Content.Shared.Gifs;
using NUnit.Framework;
using Robust.Client.UserInterface;
using Robust.Shared.Enums;

namespace Content.IntegrationTests.Tests.Gifs;

/// <summary>
/// End-to-end: client searches GifSnap through the server, posts a result to OOC, sees the GIF line in its chat
/// history, and can fetch the encoded sprite sheet. Needs internet access to gifsnap.com; when the first request
/// fails with a network error the test is ignored rather than failed so offline CI stays green.
/// </summary>
[TestFixture]
public sealed class GifSendTest : GameTest
{
    [SidedDependency(Side.Client)] private readonly IUserInterfaceManager _uiManager = null!;

    private const int TimeoutSeconds = 20;

    [Test]
    public async Task SearchSendAndFetch()
    {
        // No playtime gate, generous rate limits, OOC on.
        await OverrideCVar(Side.Server, CCVars.GifsEnabled, true);
        await OverrideCVar(Side.Server, CCVars.GameRoleTimers, false);
        await OverrideCVar(Side.Server, CCVars.GifsRateLimitCount, 1000);
        await OverrideCVar(Side.Server, CCVars.GifsSearchRateLimitCount, 1000);
        await OverrideCVar(Side.Server, CCVars.OocEnabled, true);

        Assert.That(ServerSession, Is.Not.Null);
        Assert.That(ServerSession!.Status, Is.EqualTo(SessionStatus.InGame), "Test player should be in game");

        GifClientSystem gifs = null!;
        GifSearchResponseEvent? searchResponse = null;
        var errors = new System.Collections.Generic.List<string>();

        await Client.WaitPost(() =>
        {
            gifs = CEntMan.System<GifClientSystem>();
            gifs.SearchResponse += ev => searchResponse = ev;
            gifs.Error += e => errors.Add(e);
            gifs.Search("cat", 1);
        });

        await WaitUntil(() => searchResponse != null, "search response");

        if (searchResponse!.Error != null)
        {
            // Network/service failure (offline CI, proxy, GifSnap down): don't fail the suite over it.
            Assert.Ignore($"GifSnap search failed ({searchResponse.Error}); skipping (no internet?)");
            return;
        }

        Assert.That(searchResponse.Results, Is.Not.Empty, "search returned no results");
        var entry = searchResponse.Results[0];
        Assert.That(GifConstants.IsValidId(entry.Id), Is.True);

        // Send it to OOC and wait for the chat line carrying the inline tag.
        var marker = $"[gif id=\"{entry.Id}\"";
        var chat = _uiManager.GetUIController<ChatUIController>();

        await Client.WaitPost(() => gifs.Send(entry.Id, ChatSelectChannel.OOC));

        var seen = false;
        await WaitUntil(() =>
        {
            seen = chat.History.Any(h => h.Msg.WrappedMessage.Contains(marker));
            return seen || errors.Count > 0;
        }, "GIF chat line");

        Assert.That(errors, Is.Empty, $"server reported GIF errors: {string.Join("; ", errors)}");
        Assert.That(seen, Is.True, "no OOC line with the GIF tag arrived");

        var line = chat.History.First(h => h.Msg.WrappedMessage.Contains(marker)).Msg;
        Assert.Multiple(() =>
        {
            Assert.That(line.Channel, Is.EqualTo(ChatChannel.OOC));
            Assert.That(line.WrappedMessage.TrimEnd(), Does.EndWith("]"), "the gif tag must end the wrapped line");
            Assert.That(line.Message, Does.Contain(entry.Title.Length == 0 ? "GIF" : entry.Title));
        });

        // Fetch the sprite sheet (the chat control would do this itself when it is in a UI tree).
        await Client.WaitPost(() => gifs.Request(entry.Id));
        await WaitUntil(() => gifs.TryGet(entry.Id, out _), "GIF sheet data");

        Assert.That(gifs.TryGet(entry.Id, out var decoded), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(decoded.FrameCount, Is.GreaterThan(0));
            Assert.That(decoded.FrameRegions, Has.Count.EqualTo(decoded.FrameCount));
            Assert.That(decoded.FrameWidth, Is.LessThanOrEqualTo(Server.CfgMan.GetCVar(CCVars.GifsFrameWidth)));
            Assert.That(decoded.FrameHeight, Is.LessThanOrEqualTo(Server.CfgMan.GetCVar(CCVars.GifsFrameHeight)));
            Assert.That(decoded.TotalDuration.TotalMilliseconds, Is.GreaterThan(0));
        });
    }

    /// <summary>Ticks the pair while waiting (in real time) for an asynchronous (HTTP) result.</summary>
    private async Task WaitUntil(System.Func<bool> condition, string what)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < TimeoutSeconds)
        {
            await RunTicksSync(5);
            if (condition())
                return;

            await Task.Delay(50);
        }

        Assert.Fail($"Timed out after {TimeoutSeconds}s waiting for {what}");
    }
}
