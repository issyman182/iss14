// iss14: GIFs in chat via GifSnap
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Administration.Logs;
using Content.Server.Chat.Managers;
using Content.Server.Chat.Systems;
using Content.Server.Players.PlayTimeTracking;
using Content.Server.Players.RateLimiting;
using Content.Shared.CCVar;
using Content.Shared.Chat;
using Content.Shared.Database;
using Content.Shared.GameTicking;
using Content.Shared.Gifs;
using Content.Shared.Players.RateLimiting;
using Content.Shared.Roles;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Server.Player;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Server.Gifs;

/// <summary>
/// Server side of GIFs in OOC/LOOC chat. Clients search GifSnap through the server (the sandboxed client has no
/// HTTP access), pick a result, and the server downloads the animation, re-encodes it into a PNG sprite sheet
/// (<see cref="GifEncoder"/>), posts a normal OOC/LOOC line carrying a <c>[gif id="…" title="…"]</c> tag, and then
/// serves the sheet to any client that asks for an id that actually appeared in chat this round.
///
/// All HTTP/image work runs on the thread pool; results are marshalled back to the main thread via
/// <see cref="_mainThread"/> and drained in <see cref="Update"/>, so entities and chat are only ever touched there.
/// </summary>
public sealed partial class GifSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private PlayerRateLimitManager _rateLimit = default!;
    [Dependency] private PlayTimeTrackingSystem _playTime = default!;
    [Dependency] private IChatManager _chatManager = default!;
    [Dependency] private ChatSystem _chat = default!;
    [Dependency] private IAdminLogManager _adminLogger = default!;
    [Dependency] private IPrototypeManager _proto = default!;
    [Dependency] private IPlayerManager _playerManager = default!;

    private const string SendRateLimitKey = "GifSend";
    private const string SearchRateLimitKey = "GifSearch";

    private const int MaxInFlight = 16;
    private const int ThumbnailSize = 96;
    private const int MaxPreviewBytes = 512 * 1024;
    private const int MaxApiResponseBytes = 1024 * 1024;
    private const long SheetCacheByteCap = 64L * 1024 * 1024;
    private const int InfoCacheCap = 2048;
    private const int ThumbnailCacheCap = 1024;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private int _inFlight;

    /// <summary>Work marshalled back to the main thread (session/chat/cache mutations).</summary>
    private readonly ConcurrentQueue<Action> _mainThread = new();

    /// <summary>Basic GIF metadata by id (from search results or /gifs/{id}). Main thread only.</summary>
    private readonly Dictionary<string, GifInfo> _infoCache = new();

    /// <summary>Thumbnail PNGs by id. Main thread only.</summary>
    private readonly Dictionary<string, byte[]> _thumbnailCache = new();

    /// <summary>Encoded sprite sheets by id, LRU-ordered (oldest first). Main thread only.</summary>
    private readonly Dictionary<string, LinkedListNode<SheetEntry>> _sheetCache = new();
    private readonly LinkedList<SheetEntry> _sheetLru = new();
    private long _sheetCacheBytes;

    /// <summary>Ids that were actually posted to chat this round; only these are served to clients.</summary>
    private readonly HashSet<string> _sentIds = new();

    /// <summary>Which ids each session has already been served, to stop a client re-requesting the same sheet.</summary>
    private readonly Dictionary<ICommonSession, HashSet<string>> _served = new();

    /// <summary>Sheet fetches in progress (id → sessions waiting for the data), so concurrent requests coalesce.</summary>
    private readonly Dictionary<string, List<ICommonSession>> _pendingSheets = new();

    private sealed record GifInfo(string Id, string Title, string Url, string? PreviewUrl, int Width, int Height);

    private sealed record SheetEntry(string Id, GifEncoder.Sheet Sheet);

    public override void Initialize()
    {
        base.Initialize();

        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SpaceStation14-iss14/1.0 (gif chat; +https://github.com/space-wizards/space-station-14)");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json, image/*;q=0.8, */*;q=0.5");

        SubscribeNetworkEvent<GifSearchRequestEvent>(OnSearchRequest);
        SubscribeNetworkEvent<GifSendRequestEvent>(OnSendRequest);
        SubscribeNetworkEvent<GifDataRequestEvent>(OnDataRequest);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
        _playerManager.PlayerStatusChanged += OnPlayerStatusChanged;

        _rateLimit.Register(SendRateLimitKey,
            new RateLimitRegistration(CCVars.GifsRateLimitPeriod, CCVars.GifsRateLimitCount,
                session => SendError(session, "gifs-error-rate-limited")));
        _rateLimit.Register(SearchRateLimitKey,
            new RateLimitRegistration(CCVars.GifsSearchRateLimitPeriod, CCVars.GifsSearchRateLimitCount,
                session => SendError(session, "gifs-error-rate-limited")));
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _playerManager.PlayerStatusChanged -= OnPlayerStatusChanged;
        _http.Dispose();
    }

    private void OnPlayerStatusChanged(object? sender, SessionStatusEventArgs e)
    {
        if (e.NewStatus == SessionStatus.Disconnected)
            _served.Remove(e.Session);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        while (_mainThread.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Log.Error($"Error while processing GIF result: {e}");
            }
        }
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        _sentIds.Clear();
        _served.Clear();
        // The encoded sheet cache is deliberately kept across rounds.
    }

    private void SendError(ICommonSession session, string locKey)
    {
        if (session.Status == SessionStatus.Disconnected)
            return;

        RaiseNetworkEvent(new GifErrorEvent(locKey), session.Channel);
    }

    private bool TryBeginWork(ICommonSession session, string errorKey)
    {
        if (_inFlight >= MaxInFlight)
        {
            Log.Warning($"GIF in-flight cap ({MaxInFlight}) reached, dropping request from {session}");
            SendError(session, errorKey);
            return false;
        }

        return true;
    }

    #region Search

    private void OnSearchRequest(GifSearchRequestEvent ev, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        if (!_cfg.GetCVar(CCVars.GifsEnabled))
        {
            SendError(session, "gifs-error-disabled");
            return;
        }

        var query = (ev.Query ?? string.Empty).Trim();
        if (query.Length > GifConstants.MaxQueryLength)
            query = query[..GifConstants.MaxQueryLength];
        var page = Math.Clamp(ev.Page, 1, GifConstants.MaxPage);

        if (session.Status == SessionStatus.Disconnected
            || _rateLimit.CountAction(session, SearchRateLimitKey) != RateLimitStatus.Allowed)
            return;

        if (!TryBeginWork(session, "gifs-error-busy"))
            return;

        var limit = Math.Clamp(_cfg.GetCVar(CCVars.GifsSearchLimit), 1, 50);
        var baseUrl = _cfg.GetCVar(CCVars.GifsApiUrl).TrimEnd('/');
        var url = query.Length == 0
            ? $"{baseUrl}/gifs/trending?page={page}&limit={limit}"
            : $"{baseUrl}/gifs/search?q={Uri.EscapeDataString(query)}&page={page}&limit={limit}";

        // Snapshot thumbnails we already have so the worker can skip those downloads.
        var knownThumbs = new Dictionary<string, byte[]>(_thumbnailCache);

        _ = SearchAsync(session, query, page, url, knownThumbs);
    }

    private async Task SearchAsync(ICommonSession session, string query, int page, string url, Dictionary<string, byte[]> knownThumbs)
    {
        Interlocked.Increment(ref _inFlight);
        try
        {
            var body = await GetBytesAsync(url, MaxApiResponseBytes);
            if (body == null)
            {
                _mainThread.Enqueue(() => Reply(session, new GifSearchResponseEvent(query, page, false, new List<GifSearchEntry>(), "gifs-error-fetch-failed")));
                return;
            }

            var (infos, hasNext) = ParseSearch(body);

            var thumbs = new List<(GifInfo Info, byte[] Png, bool IsNew)>(infos.Count);
            foreach (var info in infos)
            {
                if (knownThumbs.TryGetValue(info.Id, out var cached))
                {
                    thumbs.Add((info, cached, false));
                    continue;
                }

                var thumb = Array.Empty<byte>();
                var previewUrl = info.PreviewUrl ?? info.Url;
                var preview = await GetBytesAsync(previewUrl, MaxPreviewBytes);
                if (preview != null)
                    thumb = GifEncoder.MakeThumbnail(preview, ThumbnailSize);

                thumbs.Add((info, thumb, thumb.Length > 0));
            }

            _mainThread.Enqueue(() =>
            {
                var results = new List<GifSearchEntry>(thumbs.Count);
                foreach (var (info, png, isNew) in thumbs)
                {
                    CacheInfo(info);
                    if (isNew)
                        CacheThumbnail(info.Id, png);
                    results.Add(new GifSearchEntry(info.Id, DisplayTitle(info), info.Width, info.Height, png));
                }

                Reply(session, new GifSearchResponseEvent(query, page, hasNext, results, null));
            });
        }
        catch (Exception e)
        {
            Log.Warning($"GIF search failed ({url}): {e.Message}");
            _mainThread.Enqueue(() => Reply(session, new GifSearchResponseEvent(query, page, false, new List<GifSearchEntry>(), "gifs-error-fetch-failed")));
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private void Reply(ICommonSession session, EntityEventArgs ev)
    {
        if (session.Status != SessionStatus.InGame)
            return;

        RaiseNetworkEvent(ev, session.Channel);
    }

    #endregion

    #region Send

    private void OnSendRequest(GifSendRequestEvent ev, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        if (!_cfg.GetCVar(CCVars.GifsEnabled))
        {
            SendError(session, "gifs-error-disabled");
            return;
        }

        if (session.Status != SessionStatus.InGame)
            return;

        if (!GifConstants.IsValidId(ev.Id))
        {
            SendError(session, "gifs-error-invalid");
            return;
        }

        var allowed = GifConstants.ParseAllowedChannels(_cfg.GetCVar(CCVars.GifsAllowedChannels));
        if (ev.Channel is not (ChatSelectChannel.OOC or ChatSelectChannel.LOOC) || (allowed & ev.Channel) == 0)
        {
            SendError(session, "gifs-error-channel");
            return;
        }

        // LOOC needs a body to speak from; OOC does not.
        if (ev.Channel == ChatSelectChannel.LOOC && session.AttachedEntity is not { Valid: true })
        {
            SendError(session, "gifs-error-no-entity");
            return;
        }

        if (!IsRoleAllowed(session))
        {
            SendError(session, "gifs-error-not-allowed");
            return;
        }

        if (_rateLimit.CountAction(session, SendRateLimitKey) != RateLimitStatus.Allowed)
            return;

        var id = ev.Id;
        var channel = ev.Channel;

        if (TryGetSheet(id, out var cachedSheet) && _infoCache.TryGetValue(id, out var cachedInfo))
        {
            PostToChat(session, cachedInfo, cachedSheet, channel);
            return;
        }

        if (!TryBeginWork(session, "gifs-error-busy"))
            return;

        _infoCache.TryGetValue(id, out var knownInfo);
        _ = FetchAndEncodeAsync(id, knownInfo, result =>
        {
            if (result.Error != null || result.Info == null || result.Sheet == null)
            {
                SendError(session, result.Error ?? "gifs-error-fetch-failed");
                return;
            }

            CacheInfo(result.Info);
            StoreSheet(id, result.Sheet);
            PostToChat(session, result.Info, result.Sheet, channel);
        });
    }

    /// <summary>Checks the role-timer gate (the <c>gifs.role</c> job). Missing prototype means no gate.</summary>
    private bool IsRoleAllowed(ICommonSession session)
    {
        var roleId = _cfg.GetCVar(CCVars.GifsRole);
        if (string.IsNullOrWhiteSpace(roleId) || !_proto.HasIndex<JobPrototype>(roleId))
            return true;

        try
        {
            return _playTime.IsAllowed(session, new ProtoId<JobPrototype>(roleId));
        }
        catch (Exception e)
        {
            // Preferences not loaded yet or similar; fail closed.
            Log.Warning($"Could not check GIF role requirement for {session}: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Posts the GIF line through the regular OOC/LOOC path. Main thread only. The frame size rides along in the
    /// markup so the client can lay the line out at its final size before the sheet has arrived.
    /// </summary>
    private void PostToChat(ICommonSession session, GifInfo info, GifEncoder.Sheet sheet, ChatSelectChannel channel)
    {
        if (session.Status != SessionStatus.InGame)
            return;

        var title = DisplayTitle(info);
        var markup = $" [gif id=\"{info.Id}\" title=\"{FormattedMessage.EscapeStringParameter(title)}\" w=\"{sheet.FrameWidth}\" h=\"{sheet.FrameHeight}\"]";
        var message = Loc.GetString("gifs-chat-message", ("title", title));

        bool sent;
        switch (channel)
        {
            case ChatSelectChannel.OOC:
                sent = _chatManager.TrySendOOCMessage(session, message, OOCChatType.OOC, markup);
                break;
            case ChatSelectChannel.LOOC:
                if (session.AttachedEntity is not { Valid: true } entity)
                {
                    SendError(session, "gifs-error-no-entity");
                    return;
                }

                sent = _chat.TrySendInGameOOCMessageWithMarkup(entity, message, InGameOOCChatType.Looc, session, markup);
                break;
            default:
                SendError(session, "gifs-error-channel");
                return;
        }

        if (!sent)
        {
            SendError(session, "gifs-error-chat-rejected");
            return;
        }

        _sentIds.Add(info.Id);
        _adminLogger.Add(LogType.Chat, LogImpact.Low, $"GIF {info.Id} ({title}) sent to {channel} by {session:Player}");
    }

    #endregion

    #region Data

    private void OnDataRequest(GifDataRequestEvent ev, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        if (session.Status != SessionStatus.InGame || !_cfg.GetCVar(CCVars.GifsEnabled))
            return;

        var id = ev.Id;
        // Only ids that actually went through chat this round are served, so the server can't be used as a proxy.
        if (!GifConstants.IsValidId(id) || !_sentIds.Contains(id))
            return;

        // Each session gets each sheet at most once; the client caches it.
        if (!_served.TryGetValue(session, out var served))
        {
            served = new HashSet<string>();
            _served[session] = served;
        }

        if (!served.Add(id))
            return;

        if (TryGetSheet(id, out var sheet))
        {
            Reply(session, ToDataEvent(id, sheet));
            return;
        }

        // Evicted from the cache: re-fetch and re-encode, coalescing concurrent requests for the same id.
        if (_pendingSheets.TryGetValue(id, out var waiters))
        {
            waiters.Add(session);
            return;
        }

        if (_inFlight >= MaxInFlight)
        {
            // Allow a retry later.
            served.Remove(id);
            return;
        }

        _pendingSheets[id] = new List<ICommonSession> { session };
        _infoCache.TryGetValue(id, out var knownInfo);
        _ = FetchAndEncodeAsync(id, knownInfo, result =>
        {
            _pendingSheets.Remove(id, out var sessions);
            if (result.Error != null || result.Info == null || result.Sheet == null)
            {
                Log.Warning($"Could not re-encode GIF {id}: {result.Error}");
                return;
            }

            CacheInfo(result.Info);
            StoreSheet(id, result.Sheet);
            if (sessions == null)
                return;

            var dataEvent = ToDataEvent(id, result.Sheet);
            foreach (var s in sessions)
                Reply(s, dataEvent);
        });
    }

    private static GifDataEvent ToDataEvent(string id, GifEncoder.Sheet sheet)
        => new(id, sheet.Png, sheet.FrameWidth, sheet.FrameHeight, sheet.FrameCount, sheet.Columns, sheet.DelaysMs);

    #endregion

    #region Fetch + encode

    private sealed record FetchResult(GifInfo? Info, GifEncoder.Sheet? Sheet, string? Error);

    /// <summary>
    /// Resolves the GIF (from the given info or <c>GET /gifs/{id}</c>), downloads and re-encodes it. The callback
    /// runs on the main thread.
    /// </summary>
    private async Task FetchAndEncodeAsync(string id, GifInfo? info, Action<FetchResult> onDone)
    {
        Interlocked.Increment(ref _inFlight);

        // Read config on the main thread before the first await.
        var baseUrl = _cfg.GetCVar(CCVars.GifsApiUrl).TrimEnd('/');
        var maxDownload = Math.Max(64 * 1024, _cfg.GetCVar(CCVars.GifsMaxDownloadBytes));
        var maxWidth = _cfg.GetCVar(CCVars.GifsMaxWidth);
        var maxFrames = _cfg.GetCVar(CCVars.GifsMaxFrames);
        var maxSheetBytes = Math.Max(32 * 1024, _cfg.GetCVar(CCVars.GifsMaxSheetBytes));

        try
        {
            var result = await FetchAndEncodeCoreAsync(id, info, baseUrl, maxDownload, maxWidth, maxFrames, maxSheetBytes);
            _mainThread.Enqueue(() => onDone(result));
        }
        catch (Exception e)
        {
            Log.Warning($"GIF fetch/encode failed for {id}: {e.Message}");
            _mainThread.Enqueue(() => onDone(new FetchResult(info, null, "gifs-error-fetch-failed")));
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private async Task<FetchResult> FetchAndEncodeCoreAsync(string id, GifInfo? info, string baseUrl, int maxDownload, int maxWidth, int maxFrames, int maxSheetBytes)
    {
        if (info == null)
        {
            var body = await GetBytesAsync($"{baseUrl}/gifs/{Uri.EscapeDataString(id)}", MaxApiResponseBytes);
            if (body == null)
                return new FetchResult(null, null, "gifs-error-fetch-failed");

            info = ParseSingle(body);
            if (info == null || info.Id != id)
                return new FetchResult(null, null, "gifs-error-not-found");
        }

        var data = await GetBytesAsync(info.Url, maxDownload);
        if (data == null)
            return new FetchResult(info, null, "gifs-error-too-large");

        // Decoding/encoding is CPU-bound; keep it off whatever thread the HTTP continuation landed on.
        var sheet = await Task.Run(() =>
        {
            using var image = GifEncoder.DecodeBounded(data);
            return image == null ? null : GifEncoder.BuildSheet(image, maxWidth, maxFrames, maxSheetBytes);
        });

        return sheet == null
            ? new FetchResult(info, null, "gifs-error-too-large")
            : new FetchResult(info, sheet, null);
    }

    /// <summary>GETs a URL with a hard byte cap (checked against Content-Length and while streaming). Null on failure.</summary>
    private async Task<byte[]?> GetBytesAsync(string url, int maxBytes)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return null;

        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
                return null;

            if (response.Content.Headers.ContentLength is { } length && length > maxBytes)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var ms = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer)) > 0)
            {
                if (ms.Length + read > maxBytes)
                    return null;
                ms.Write(buffer, 0, read);
            }

            return ms.ToArray();
        }
        catch (Exception e)
        {
            Log.Debug($"GIF GET {url} failed: {e.Message}");
            return null;
        }
    }

    #endregion

    #region JSON

    private static (List<GifInfo> Results, bool HasNext) ParseSearch(byte[] body)
    {
        var results = new List<GifInfo>();
        var hasNext = false;
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in data.EnumerateArray())
            {
                var info = ParseGif(element);
                if (info != null)
                    results.Add(info);
            }
        }

        if (root.TryGetProperty("pagination", out var pagination)
            && pagination.ValueKind == JsonValueKind.Object
            && pagination.TryGetProperty("has_next", out var next)
            && next.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            hasNext = next.GetBoolean();
        }

        return (results, hasNext);
    }

    private static GifInfo? ParseSingle(byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        // Accept either a bare object or a {"data": {...}} envelope.
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            root = data;

        return ParseGif(root);
    }

    private static GifInfo? ParseGif(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        var id = GetString(element, "id");
        var url = GetString(element, "url");
        if (!GifConstants.IsValidId(id) || url == null)
            return null;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return null;

        var previewUrl = GetString(element, "preview_url");
        if (previewUrl != null && (!Uri.TryCreate(previewUrl, UriKind.Absolute, out var pUri) || pUri.Scheme is not ("http" or "https")))
            previewUrl = null;

        var title = SanitizeTitle(GetString(element, "title"));
        var width = GetInt(element, "width");
        var height = GetInt(element, "height");

        return new GifInfo(id!, title, url, previewUrl, width, height);
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;

    private static int GetInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var v) ? Math.Max(0, v) : 0;

    /// <summary>Titles come from a third party and end up in chat: strip control characters and cap the length.</summary>
    private static string SanitizeTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var chars = raw.Where(c => !char.IsControl(c)).ToArray();
        var title = new string(chars).Trim();
        if (title.Length > GifConstants.MaxTitleLength)
            title = title[..GifConstants.MaxTitleLength].TrimEnd();

        return title;
    }

    /// <summary>Title to show players; localizes the fallback for untitled GIFs. Main thread only.</summary>
    private string DisplayTitle(GifInfo info)
        => info.Title.Length == 0 ? Loc.GetString("gifs-untitled") : info.Title;

    #endregion

    #region Caches (main thread only)

    private void CacheInfo(GifInfo info)
    {
        if (_infoCache.Count >= InfoCacheCap && !_infoCache.ContainsKey(info.Id))
        {
            // Simple bound: drop everything that isn't referenced by a cached sheet.
            foreach (var key in _infoCache.Keys.ToList())
            {
                if (!_sheetCache.ContainsKey(key) && !_sentIds.Contains(key))
                    _infoCache.Remove(key);
            }

            if (_infoCache.Count >= InfoCacheCap)
                _infoCache.Clear();
        }

        _infoCache[info.Id] = info;
    }

    private void CacheThumbnail(string id, byte[] png)
    {
        if (_thumbnailCache.Count >= ThumbnailCacheCap && !_thumbnailCache.ContainsKey(id))
            _thumbnailCache.Clear();

        _thumbnailCache[id] = png;
    }

    private bool TryGetSheet(string id, out GifEncoder.Sheet sheet)
    {
        if (_sheetCache.TryGetValue(id, out var node))
        {
            // Touch: move to the most-recently-used end.
            _sheetLru.Remove(node);
            _sheetLru.AddLast(node);
            sheet = node.Value.Sheet;
            return true;
        }

        sheet = default!;
        return false;
    }

    private void StoreSheet(string id, GifEncoder.Sheet sheet)
    {
        if (_sheetCache.TryGetValue(id, out var existing))
        {
            _sheetLru.Remove(existing);
            _sheetCache.Remove(id);
            _sheetCacheBytes -= existing.Value.Sheet.TotalBytes;
        }

        var cap = Math.Max(1, _cfg.GetCVar(CCVars.GifsCacheEntries));
        while (_sheetLru.Count > 0 && (_sheetLru.Count >= cap || _sheetCacheBytes + sheet.TotalBytes > SheetCacheByteCap))
        {
            var oldest = _sheetLru.First!;
            _sheetLru.RemoveFirst();
            _sheetCache.Remove(oldest.Value.Id);
            _sheetCacheBytes -= oldest.Value.Sheet.TotalBytes;
        }

        var node = _sheetLru.AddLast(new SheetEntry(id, sheet));
        _sheetCache[id] = node;
        _sheetCacheBytes += sheet.TotalBytes;
    }

    #endregion
}
