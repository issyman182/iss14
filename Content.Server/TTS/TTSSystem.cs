using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Players.RateLimiting;
using Content.Shared.Administration.Logs;
using Content.Shared.CCVar;
using Content.Shared.Database;
using Content.Shared.Humanoid;
using Content.Shared.Players.RateLimiting;
using Content.Shared.TTS;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server.TTS;

/// <summary>
/// Handles neural "read aloud" TTS: takes synthesis requests from clients, calls the
/// configured HTTP backend, caches the result, and ships the audio back to the requester.
///
/// Each player is automatically assigned a stable neural voice (by user id) drawn from the pool
/// of speakers matching their character's gender, so the same player always sounds the same.
/// If the backend is offline or unconfigured the client is told to fall back to gibberish, and
/// backend up/down transitions are written to the admin logs (once per transition). Also exposes
/// the speaker list, gender config, and test path used by the <c>ttsconfig</c> admin panel.
/// </summary>
public sealed partial class TTSSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IPrototypeManager _proto = default!;
    [Dependency] private ISharedAdminLogManager _adminLogger = default!;
    [Dependency] private PlayerRateLimitManager _rateLimit = default!; // iss14 fix

    private readonly HttpClient _http = new();

    // iss14 fix: per-player request rate limit (see CCVars.TtsRateLimitPeriod / TtsRateLimitCount).
    private const string RateLimitKey = "Tts";

    // iss14 fix: cap on concurrent backend requests so a burst of clients can't pile up unbounded
    // HTTP calls / audio buffers. Requests over the cap are dropped (client falls back to gibberish).
    private const int MaxInFlightRequests = 32;
    private int _inFlight;

    // Cache of synthesized audio keyed by "speakertext".
    private readonly Dictionary<string, byte[]> _cache = new();
    private const int CacheCap = 2048;
    // iss14 fix: also bound the cache by total PCM bytes, not just entry count.
    private const long CacheByteCap = 256L * 1024 * 1024;
    private long _cacheBytes;

    // Completed audio jobs, produced on background threads and drained on the main thread.
    private readonly ConcurrentQueue<JobResult> _results = new();

    // Arbitrary continuations (e.g. speaker-list fetches) marshalled back to the main thread.
    private readonly ConcurrentQueue<Action> _mainThread = new();

    // Available backend voices (fetched on demand from the panel), in language groups.
    private readonly List<SpeakerInfo> _speakers = new();
    public IReadOnlyList<SpeakerInfo> Speakers => _speakers;

    // Available backend models.
    private readonly List<string> _models = new();
    public IReadOnlyList<string> Models => _models;

    /// <summary>A backend voice and the language group it belongs to.</summary>
    public readonly record struct SpeakerInfo(string Language, string Name);

    // Speaker -> gender bucket, mirrored from the tts.voice_genders CVar.
    private readonly Dictionary<string, TtsVoiceGender> _genderMap = new();

    // Backend health; only logged on transition. Starts true so the first failure is reported.
    private bool _backendOnline = true;

    // Per-client TTS prefs, used to suppress built-in SFX the client's TTS would overlap.
    private readonly Dictionary<ICommonSession, ClientTtsState> _clientState = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<RequestTtsEvent>(OnRequestTts);
        SubscribeNetworkEvent<TtsSuppressionStateEvent>(OnClientState);
        Subs.CVar(_cfg, CCVars.TtsVoiceGenders, OnGenderMapChanged, true);

        // iss14 fix: rate-limit synthesis requests per player.
        _rateLimit.Register(RateLimitKey,
            new RateLimitRegistration(CCVars.TtsRateLimitPeriod, CCVars.TtsRateLimitCount, null));
    }

    #region Built-in sound suppression

    private void OnClientState(TtsSuppressionStateEvent ev, EntitySessionEventArgs args)
    {
        _clientState[args.SenderSession] = new ClientTtsState(ev.Enabled, ev.Reading, ev.ReadAnnouncements);

        // Opportunistically drop entries for clients that have disconnected.
        if (_clientState.Count > 256)
        {
            foreach (var session in _clientState.Keys.ToList())
            {
                if (session.Status == SessionStatus.Disconnected)
                    _clientState.Remove(session);
            }
        }
    }

    /// <summary>
    /// True if this client's TTS will read announcements aloud (neural), so the announcement chime
    /// should be suppressed for them to avoid overlapping the spoken text.
    /// </summary>
    public bool SuppressesAnnouncements(ICommonSession session)
        => _cfg.GetCVar(CCVars.TtsEnabled)
           && _clientState.TryGetValue(session, out var s)
           && s.Enabled && s.Reading && s.ReadAnnouncements;

    /// <summary>
    /// True if this client's TTS will voice speech (gibberish or neural), so the vanilla per-message
    /// speech blip should be suppressed for them. Turning TTS off restores the blip.
    /// </summary>
    public bool SuppressesSpeech(ICommonSession session)
        => _cfg.GetCVar(CCVars.TtsEnabled)
           && _clientState.TryGetValue(session, out var s)
           && s.Enabled;

    private readonly record struct ClientTtsState(bool Enabled, bool Reading, bool ReadAnnouncements);

    #endregion

    public override void Shutdown()
    {
        base.Shutdown();
        _http.Dispose();
    }

    private void OnRequestTts(RequestTtsEvent ev, EntitySessionEventArgs args)
    {
        if (!_cfg.GetCVar(CCVars.TtsEnabled))
            return;

        var text = ev.Text.Trim();
        if (text.Length == 0 || text.Length > _cfg.GetCVar(CCVars.TtsMaxMessageLength))
            return;

        // iss14 fix: per-player rate limit; excess requests are silently dropped.
        if (args.SenderSession.Status == SessionStatus.Disconnected
            || _rateLimit.CountAction(args.SenderSession, RateLimitKey) != RateLimitStatus.Allowed)
            return;

        var url = _cfg.GetCVar(CCVars.TtsApiUrl);
        if (string.IsNullOrWhiteSpace(url))
        {
            // No backend configured: tell the client to use gibberish instead of going silent.
            _results.Enqueue(JobResult.Fallback(args.SenderSession, ev));
            return;
        }

        // iss14 fix: cap concurrent backend requests; fall back to gibberish when saturated.
        if (_inFlight >= MaxInFlightRequests)
        {
            Log.Warning($"TTS in-flight request cap ({MaxInFlightRequests}) reached, dropping request from {args.SenderSession}");
            _results.Enqueue(JobResult.Fallback(args.SenderSession, ev));
            return;
        }

        var speaker = ResolveSpeaker(ev);
        _ = ProcessRequestAsync(url, speaker, text, args.SenderSession, ev, null);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        while (_mainThread.TryDequeue(out var action))
            action();

        while (_results.TryDequeue(out var result))
        {
            // Test jobs report status to the panel (no gibberish fallback).
            if (result.TestCallback != null)
            {
                if (result.Audio is { Length: > 0 } && result.Session.Status == SessionStatus.InGame)
                    RaiseNetworkEvent(new PlayTtsEvent(result.Audio, NetEntity.Invalid), result.Session.Channel);

                result.TestCallback(result.Audio is { Length: > 0 }, result.Error ?? "");
                continue;
            }

            if (result.Session.Status != SessionStatus.InGame || result.Request == null)
                continue;

            if (result.Audio is { Length: > 0 })
                RaiseNetworkEvent(new PlayTtsEvent(result.Audio, result.Request.Source), result.Session.Channel);
            else
                RaiseNetworkEvent(
                    new TtsFallbackEvent(result.Request.Text, result.Request.VoiceId, result.Request.Source, result.Request.Whisper),
                    result.Session.Channel);
        }
    }

    #region Speaker resolution

    /// <summary>
    /// Picks the neural speaker for a request: a stable, gender-matched voice for the speaking
    /// player, or the default speaker for announcements / unconfigured setups.
    /// </summary>
    private string ResolveSpeaker(RequestTtsEvent ev)
    {
        var def = _cfg.GetCVar(CCVars.TtsDefaultSpeaker);

        if (!ev.Source.IsValid())
            return def;

        var ent = GetEntity(ev.Source);
        if (!Exists(ent))
            return def;

        if (!TryComp<HumanoidAppearanceComponent>(ent, out var profile))
            return def;

        // Seed by the character's name so a given character always gets the same voice (and two
        // different characters differ), stable across restarts. Uses a deterministic hash because
        // string.GetHashCode() is randomized per process.
        var seed = StableHash(MetaData(ent).EntityName);

        // Male/female pick from the matching pool; any other gender (they/them, it/its) picks a
        // random voice from the whole assigned pool. Stable per character either way.
        List<string> pool;
        switch (profile.Gender)
        {
            case Gender.Male:
                pool = VoicesOf(TtsVoiceGender.Male);
                break;
            case Gender.Female:
                pool = VoicesOf(TtsVoiceGender.Female);
                break;
            default:
                pool = AllAssignedVoices();
                break;
        }

        // Fall back to the whole pool, then to the default speaker, if nothing is assigned.
        if (pool.Count == 0)
            pool = AllAssignedVoices();
        if (pool.Count == 0)
            return def;

        var idx = (int) ((uint) seed % (uint) pool.Count);
        return pool[idx];
    }

    private List<string> VoicesOf(TtsVoiceGender gender)
        => _genderMap.Where(kv => kv.Value == gender).Select(kv => kv.Key)
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

    private List<string> AllAssignedVoices()
        => _genderMap.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();

    /// <summary>Deterministic string hash (FNV-1a), unlike <see cref="string.GetHashCode"/> which is randomized per process.</summary>
    private static int StableHash(string text)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in text)
                hash = (hash ^ c) * 16777619u;
            return (int) hash;
        }
    }

    #endregion

    #region Gender config (CVar-backed)

    private void OnGenderMapChanged(string raw)
    {
        _genderMap.Clear();
        foreach (var entry in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = entry.Split('=', 2);
            if (kv.Length != 2)
                continue;

            var gender = kv[1].Trim() switch
            {
                "m" => TtsVoiceGender.Male,
                "f" => TtsVoiceGender.Female,
                _ => TtsVoiceGender.None,
            };
            _genderMap[kv[0].Trim()] = gender;
        }
    }

    public TtsVoiceGender GetVoiceGender(string speaker)
        => _genderMap.GetValueOrDefault(speaker, TtsVoiceGender.None);

    public void SetVoiceGender(string speaker, TtsVoiceGender gender)
    {
        _genderMap[speaker] = gender;
        WriteGenderMap();
    }

    public void SetAllVoiceGenders(TtsVoiceGender gender)
    {
        foreach (var speaker in _speakers)
            _genderMap[speaker.Name] = gender;
        WriteGenderMap();
    }

    /// <summary>
    /// Assigns each voice's gender from the Kokoro id convention (2nd char: <c>f</c>emale / <c>m</c>ale).
    /// </summary>
    public void AutoAssignGenders()
    {
        foreach (var speaker in _speakers)
            _genderMap[speaker.Name] = KokoroGender(speaker.Name);
        WriteGenderMap();
    }

    /// <summary>
    /// Kokoro voice ids are <c>{lang}{gender}_{name}</c> (e.g. <c>af_heart</c>, <c>am_adam</c>).
    /// The 2nd char gives the gender.
    /// </summary>
    private static TtsVoiceGender KokoroGender(string id)
    {
        if (id.Length < 3 || id[2] != '_')
            return TtsVoiceGender.None;

        return id[1] switch
        {
            'f' => TtsVoiceGender.Female,
            'm' => TtsVoiceGender.Male,
            _ => TtsVoiceGender.None,
        };
    }

    /// <summary>The 1st char of a Kokoro voice id gives the language.</summary>
    private static string KokoroLanguage(string id)
    {
        if (id.Length < 3 || id[2] != '_' || id[1] is not ('f' or 'm'))
            return string.Empty;

        return id[0] switch
        {
            'a' => "English (US)",
            'b' => "English (UK)",
            'e' => "Spanish",
            'f' => "French",
            'h' => "Hindi",
            'i' => "Italian",
            'j' => "Japanese",
            'p' => "Portuguese",
            'z' => "Chinese",
            _ => string.Empty,
        };
    }

    private void WriteGenderMap()
    {
        var sb = new StringBuilder();
        foreach (var (speaker, gender) in _genderMap)
        {
            var g = gender switch
            {
                TtsVoiceGender.Male => "m",
                TtsVoiceGender.Female => "f",
                _ => null, // None: unavailable, no need to store
            };
            if (g == null)
                continue;

            sb.Append(speaker).Append('=').Append(g).Append(';');
        }

        _cfg.SetCVar(CCVars.TtsVoiceGenders, sb.ToString());
    }

    #endregion

    #region Speaker list fetch (admin panel)

    /// <summary>Fetches the voice and model lists from the backend (best-effort for models).</summary>
    public void RefreshSpeakers(Action<bool, string> onDone)
    {
        if (!ResolveUrl(CCVars.TtsSpeakersUrl, "/v1/audio/voices", out var voicesUrl))
        {
            onDone(false, "set tts.speakers_url or tts.api_url first");
            return;
        }

        ResolveUrl(CCVars.TtsModelsUrl, "/v1/models", out var modelsUrl);
        _ = FetchAsync(voicesUrl, modelsUrl, onDone);
    }

    private bool ResolveUrl(CVarDef<string> cvar, string defaultPath, out string url)
    {
        url = _cfg.GetCVar(cvar).Trim();
        if (!string.IsNullOrWhiteSpace(url))
            return true;

        var apiUrl = _cfg.GetCVar(CCVars.TtsApiUrl);
        if (!string.IsNullOrWhiteSpace(apiUrl) && Uri.TryCreate(apiUrl, UriKind.Absolute, out var apiUri))
        {
            url = $"{apiUri.Scheme}://{apiUri.Authority}{defaultPath}";
            return true;
        }

        return false;
    }

    private async Task FetchAsync(string voicesUrl, string modelsUrl, Action<bool, string> onDone)
    {
        try
        {
            using var response = await _http.GetAsync(voicesUrl);
            response.EnsureSuccessStatusCode();
            var voices = ParseVoices(await response.Content.ReadAsByteArrayAsync());

            // Models are optional; don't fail the whole refresh if that endpoint is missing.
            var models = new List<string>();
            if (!string.IsNullOrWhiteSpace(modelsUrl))
            {
                try
                {
                    using var mResponse = await _http.GetAsync(modelsUrl);
                    if (mResponse.IsSuccessStatusCode)
                        models = ParseModels(await mResponse.Content.ReadAsByteArrayAsync());
                }
                catch { /* ignore */ }
            }

            _mainThread.Enqueue(() =>
            {
                _speakers.Clear();
                _speakers.AddRange(voices);
                _models.Clear();
                _models.AddRange(models);

                // First time only: seed gender assignments from the voice-name convention.
                if (_genderMap.Count == 0)
                    AutoAssignGenders();

                onDone(true, $"Loaded {voices.Count} voices, {models.Count} models.");
            });
        }
        catch (Exception e)
        {
            _mainThread.Enqueue(() => onDone(false, e.Message));
        }
    }

    /// <summary>
    /// Parses the OpenAI/Kokoro voices response <c>{"voices":[{"id","name"}]}</c> (also accepts a
    /// bare array or a language-grouped object). Language is derived from the Kokoro id prefix.
    /// </summary>
    private static List<SpeakerInfo> ParseVoices(byte[] body)
    {
        var result = new List<SpeakerInfo>();
        var seen = new HashSet<string>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("voices", out var voices))
                root = voices;

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in root.EnumerateArray())
                {
                    var name = element.ValueKind == JsonValueKind.String
                        ? element.GetString()
                        : element.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                            ? idEl.GetString()
                            : element.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                                ? nameEl.GetString()
                                : null;

                    if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
                        result.Add(new SpeakerInfo(KokoroLanguage(name), name));
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                // Language-grouped object (non-Kokoro backends): keys are languages.
                foreach (var group in root.EnumerateObject())
                {
                    foreach (var element in group.Value.EnumerateArray())
                    {
                        var name = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
                            result.Add(new SpeakerInfo(group.Name, name));
                    }
                }
            }
        }
        catch
        {
            // Leave result as-is (possibly empty) on malformed responses.
        }

        return result;
    }

    /// <summary>Parses the OpenAI models response <c>{"data":[{"id"}]}</c>.</summary>
    private static List<string> ParseModels(byte[] body)
    {
        var result = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data))
                root = data;

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in root.EnumerateArray())
                {
                    var id = element.ValueKind == JsonValueKind.String
                        ? element.GetString()
                        : element.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;

                    if (!string.IsNullOrWhiteSpace(id))
                        result.Add(id);
                }
            }
        }
        catch
        {
            // ignore
        }

        return result;
    }

    #endregion

    #region Synthesis

    /// <summary>
    /// Synthesizes a test line (used by the admin config panel), bypassing the enabled gate.
    /// On success the audio is played for the requesting admin; the callback reports status.
    /// </summary>
    public void QueueTest(ICommonSession session, string text, string speaker, Action<bool, string> onComplete)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            onComplete(false, "no text");
            return;
        }

        var url = _cfg.GetCVar(CCVars.TtsApiUrl);
        if (string.IsNullOrWhiteSpace(url))
        {
            onComplete(false, "tts.api_url is not set");
            return;
        }

        if (string.IsNullOrWhiteSpace(speaker))
            speaker = _cfg.GetCVar(CCVars.TtsDefaultSpeaker);

        _ = ProcessRequestAsync(url, speaker, text, session, null, onComplete);
    }

    private async Task ProcessRequestAsync(
        string url, string speaker, string text, ICommonSession session, RequestTtsEvent? request, Action<bool, string>? testCallback)
    {
        // iss14 fix: track in-flight requests (incremented synchronously before the first await).
        Interlocked.Increment(ref _inFlight);
        try
        {
            var (audio, error) = await SynthesizeAsync(url, speaker, text);

            // Test requests don't represent real backend health, so don't flip the health state on them.
            if (testCallback == null)
                _mainThread.Enqueue(() => ReportHealth(url, error));

            _results.Enqueue(new JobResult(session, request, audio, error, testCallback));
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private void ReportHealth(string url, string? error)
    {
        var online = error == null;
        if (online == _backendOnline)
            return;

        _backendOnline = online;

        if (online)
        {
            _adminLogger.Add(LogType.Tts, LogImpact.Low,
                $"TTS backend is reachable again at {url}; neural read-aloud restored.");
        }
        else
        {
            _adminLogger.Add(LogType.Tts, LogImpact.Medium,
                $"TTS backend at {url} is unreachable ({error}). Players using read-aloud fall back to gibberish until it recovers.");
        }
    }

    private async Task<(byte[]? audio, string? error)> SynthesizeAsync(string url, string speaker, string text)
    {
        var key = $"{speaker}{text}";
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var cached))
                return (cached, null);
        }

        try
        {
            // OpenAI-compatible (Kokoro-FastAPI) request. We ask for raw PCM (16-bit, 24kHz, mono)
            // because the engine's WAV loader only accepts PCM WAV and Kokoro emits float WAV;
            // raw PCM is decoded directly via LoadAudioRaw on the client.
            var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["model"] = _cfg.GetCVar(CCVars.TtsModel),
                ["input"] = text,
                ["voice"] = speaker,
                ["response_format"] = "pcm",
                ["speed"] = _cfg.GetCVar(CCVars.TtsSpeed),
                ["stream"] = false,
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };

            var token = _cfg.GetCVar(CCVars.TtsApiToken);
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await _http.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var bytes = await response.Content.ReadAsByteArrayAsync();
            var audio = ExtractAudio(bytes, response.Content.Headers.ContentType?.MediaType);

            if (audio is not { Length: > 0 })
                return (null, "backend returned no audio");

            lock (_cache)
            {
                // iss14 fix: evict everything when either the entry count or the total byte cap is hit
                // (simple, matches the existing clear-all style; a lone oversized clip is still cached once).
                if (_cache.Count >= CacheCap || _cacheBytes + audio.Length > CacheByteCap)
                {
                    _cache.Clear();
                    _cacheBytes = 0;
                }

                if (_cache.TryGetValue(key, out var existing))
                    _cacheBytes -= existing.Length;
                _cache[key] = audio;
                _cacheBytes += audio.Length;
            }

            return (audio, null);
        }
        catch (Exception e)
        {
            return (null, e.Message);
        }
    }

    /// <summary>
    /// Accepts raw audio bytes (WAV/OGG) or a JSON envelope of the form {"audio":"&lt;base64&gt;"}.
    /// </summary>
    private static byte[]? ExtractAudio(byte[] body, string? mediaType)
    {
        // Only treat as a JSON envelope when the content type says so — raw PCM/WAV bytes can
        // coincidentally start with '{' or '['.
        if (mediaType?.Contains("json") != true)
            return body;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object && TryGetBase64(root, out var b))
                return b;

            if (root.TryGetProperty("results", out var results)
                && results.ValueKind == JsonValueKind.Array
                && results.GetArrayLength() > 0
                && TryGetBase64(results[0], out var rb))
                return rb;
        }
        catch
        {
            // Fall through: treat as raw bytes.
        }

        return body;
    }

    private static bool TryGetBase64(JsonElement element, out byte[] audio)
    {
        audio = [];
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        if (!element.TryGetProperty("audio", out var audioProp) || audioProp.ValueKind != JsonValueKind.String)
            return false;

        try
        {
            audio = Convert.FromBase64String(audioProp.GetString()!);
            return audio.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    #endregion

    private readonly struct JobResult(
        ICommonSession session, RequestTtsEvent? request, byte[]? audio, string? error, Action<bool, string>? testCallback)
    {
        public readonly ICommonSession Session = session;
        public readonly RequestTtsEvent? Request = request;
        public readonly byte[]? Audio = audio;
        public readonly string? Error = error;
        public readonly Action<bool, string>? TestCallback = testCallback;

        public static JobResult Fallback(ICommonSession session, RequestTtsEvent request)
            => new(session, request, null, null, null);
    }
}
