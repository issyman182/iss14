// iss14: GIFs in chat via GifSnap
using System.IO;
using Content.Shared.Gifs;
using Robust.Client.Graphics;
using Robust.Shared.Graphics;
using Robust.Shared.Network;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Client.Gifs;

/// <summary>
/// Client side of GIFs in chat: owns the decoded sprite sheets, requests missing ones from the server (once per id),
/// and relays search responses / errors to the picker UI.
/// </summary>
public sealed partial class GifClientSystem : EntitySystem
{
    [Dependency] private IClyde _clyde = default!;
    [Dependency] private IClientNetManager _net = default!;

    private readonly Dictionary<string, GifEntry> _entries = new();
    private readonly HashSet<string> _pending = new();

    /// <summary>Raised on the main thread when a sheet for the given id has been decoded and is ready to draw.</summary>
    public event Action<string>? GifLoaded;

    /// <summary>Search results from the server, for the picker.</summary>
    public event Action<GifSearchResponseEvent>? SearchResponse;

    /// <summary>Error from the server, already localized, for the picker and the chat notice.</summary>
    public event Action<string>? Error;

    /// <summary>A decoded GIF: one texture holding all frames, plus the precomputed frame rectangles and timing.</summary>
    public sealed class GifEntry
    {
        public OwnedTexture Sheet = default!;
        public int FrameWidth;
        public int FrameHeight;
        public int FrameCount;
        public int Columns;
        public int[] DelaysMs = Array.Empty<int>();
        public TimeSpan TotalDuration;

        /// <summary>Sub-rectangle of <see cref="Sheet"/> (texture pixels) for each frame.</summary>
        public List<UIBox2> FrameRegions = new();

        /// <summary>Cumulative end time (ms) of each frame, for a binary search in the draw loop.</summary>
        public int[] FrameEndsMs = Array.Empty<int>();
    }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<GifDataEvent>(OnGifData);
        SubscribeNetworkEvent<GifSearchResponseEvent>(OnSearchResponse);
        SubscribeNetworkEvent<GifErrorEvent>(OnError);
        _net.Disconnect += OnDisconnect;
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _net.Disconnect -= OnDisconnect;
        Clear();
    }

    private void OnDisconnect(object? sender, NetDisconnectedArgs e)
    {
        // Sheets are only served once per session by the server; forget everything so a reconnect re-requests.
        Clear();
    }

    private void Clear()
    {
        foreach (var entry in _entries.Values)
            entry.Sheet.Dispose();

        _entries.Clear();
        _pending.Clear();
    }

    public bool TryGet(string id, out GifEntry entry)
        => _entries.TryGetValue(id, out entry!);

    /// <summary>Asks the server for the sheet of the given id, at most once per id per connection.</summary>
    public void Request(string id)
    {
        if (!GifConstants.IsValidId(id) || _entries.ContainsKey(id) || !_pending.Add(id))
            return;

        if (!_net.IsConnected)
        {
            _pending.Remove(id);
            return;
        }

        RaiseNetworkEvent(new GifDataRequestEvent(id));
    }

    public void Search(string query, int page)
    {
        if (query.Length > GifConstants.MaxQueryLength)
            query = query[..GifConstants.MaxQueryLength];

        RaiseNetworkEvent(new GifSearchRequestEvent(query, Math.Clamp(page, 1, GifConstants.MaxPage)));
    }

    public void Send(string id, Content.Shared.Chat.ChatSelectChannel channel)
    {
        if (!GifConstants.IsValidId(id))
            return;

        RaiseNetworkEvent(new GifSendRequestEvent(id, channel));
    }

    private void OnSearchResponse(GifSearchResponseEvent ev)
        => SearchResponse?.Invoke(ev);

    private void OnError(GifErrorEvent ev)
        => Error?.Invoke(Loc.GetString(ev.Message, ("minutes", ev.Minutes)));

    private void OnGifData(GifDataEvent ev)
    {
        _pending.Remove(ev.Id);

        if (!GifConstants.IsValidId(ev.Id) || _entries.ContainsKey(ev.Id))
            return;

        if (ev.FrameCount <= 0 || ev.Columns <= 0 || ev.FrameWidth <= 0 || ev.FrameHeight <= 0
            || ev.FrameDelaysMs.Length != ev.FrameCount || ev.SheetPng.Length == 0)
        {
            Log.Warning($"Rejected malformed GIF data for {ev.Id}");
            return;
        }

        OwnedTexture texture;
        try
        {
            using var stream = new MemoryStream(ev.SheetPng, false);
            using var image = Image.Load<Rgba32>(stream);

            var rows = (ev.FrameCount + ev.Columns - 1) / ev.Columns;
            if (image.Width < ev.FrameWidth * ev.Columns || image.Height < ev.FrameHeight * rows)
            {
                Log.Warning($"GIF sheet for {ev.Id} is smaller than its declared frame grid");
                return;
            }

            var loadParams = TextureLoadParameters.Default;
            loadParams.SampleParameters = new TextureSampleParameters { Filter = true };
            texture = _clyde.LoadTextureFromImage(image, $"gif-{ev.Id}", loadParams);
        }
        catch (Exception e)
        {
            Log.Warning($"Failed to decode GIF sheet for {ev.Id}: {e.Message}");
            return;
        }

        var entry = new GifEntry
        {
            Sheet = texture,
            FrameWidth = ev.FrameWidth,
            FrameHeight = ev.FrameHeight,
            FrameCount = ev.FrameCount,
            Columns = ev.Columns,
            DelaysMs = ev.FrameDelaysMs,
            FrameEndsMs = new int[ev.FrameCount],
        };

        var total = 0;
        for (var i = 0; i < ev.FrameCount; i++)
        {
            var x = (i % ev.Columns) * ev.FrameWidth;
            var y = (i / ev.Columns) * ev.FrameHeight;
            entry.FrameRegions.Add(new UIBox2(x, y, x + ev.FrameWidth, y + ev.FrameHeight));

            total += Math.Max(1, ev.FrameDelaysMs[i]);
            entry.FrameEndsMs[i] = total;
        }

        entry.TotalDuration = TimeSpan.FromMilliseconds(total);
        _entries[ev.Id] = entry;
        GifLoaded?.Invoke(ev.Id);
    }

    /// <summary>Picks the frame index for a point in time, cycling the animation. No allocations.</summary>
    public static int FrameAt(GifEntry entry, TimeSpan time)
    {
        if (entry.FrameCount <= 1)
            return 0;

        var totalMs = entry.FrameEndsMs[entry.FrameCount - 1];
        var t = (int) (time.Ticks / TimeSpan.TicksPerMillisecond % totalMs);

        // Binary search the first frame whose end time is past t.
        var lo = 0;
        var hi = entry.FrameCount - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (entry.FrameEndsMs[mid] > t)
                hi = mid;
            else
                lo = mid + 1;
        }

        return lo;
    }
}
