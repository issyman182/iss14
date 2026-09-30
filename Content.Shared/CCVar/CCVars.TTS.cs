using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    // --- Server / master switches ---

    /// <summary>
    ///     Master switch for the Text-To-Speech feature. When off, no TTS (gibberish or neural)
    ///     plays and the in-game voice picker is hidden. Replicated so clients can react.
    /// </summary>
    public static readonly CVarDef<bool> TtsEnabled =
        CVarDef.Create("tts.enabled", false, CVar.SERVER | CVar.REPLICATED | CVar.ARCHIVE);

    /// <summary>
    ///     OpenAI-compatible speech endpoint (e.g. Kokoro-FastAPI). The server POSTs JSON
    ///     <c>{"model","input","voice","response_format":"wav","speed","stream":false}</c> and
    ///     expects WAV audio back. Empty disables neural reading (gibberish still works).
    /// </summary>
    public static readonly CVarDef<string> TtsApiUrl =
        CVarDef.Create("tts.api_url", "http://localhost:8880/v1/audio/speech", CVar.SERVER | CVar.ARCHIVE);

    /// <summary>
    ///     Endpoint returning the available voices (GET, JSON <c>{"voices":[{"id","name"}]}</c>).
    ///     If empty, derived from <see cref="TtsApiUrl"/>'s host as <c>{host}/v1/audio/voices</c>.
    /// </summary>
    public static readonly CVarDef<string> TtsSpeakersUrl =
        CVarDef.Create("tts.speakers_url", "http://localhost:8880/v1/audio/voices?legacy=false", CVar.SERVER | CVar.ARCHIVE);

    /// <summary>
    ///     Endpoint returning the available models (GET, OpenAI-style <c>{"data":[{"id"}]}</c>).
    ///     If empty, derived from <see cref="TtsApiUrl"/>'s host as <c>{host}/v1/models</c>.
    /// </summary>
    public static readonly CVarDef<string> TtsModelsUrl =
        CVarDef.Create("tts.models_url", "http://localhost:8880/v1/models", CVar.SERVER | CVar.ARCHIVE);

    /// <summary>
    ///     Optional bearer token sent as the Authorization header to the TTS endpoint.
    /// </summary>
    public static readonly CVarDef<string> TtsApiToken =
        CVarDef.Create("tts.api_token", "", CVar.SERVER | CVar.ARCHIVE | CVar.CONFIDENTIAL); // iss14 fix: keep out of logs/dumps

    /// <summary>
    ///     Backend voice used for any message whose voice has no explicit mapping
    ///     (announcements, or unconfigured genders).
    /// </summary>
    public static readonly CVarDef<string> TtsDefaultSpeaker =
        CVarDef.Create("tts.default_speaker", "af_heart", CVar.SERVER | CVar.ARCHIVE);

    /// <summary>The model name sent to the backend (e.g. <c>kokoro</c>).</summary>
    public static readonly CVarDef<string> TtsModel =
        CVarDef.Create("tts.model", "kokoro", CVar.SERVER | CVar.ARCHIVE);

    /// <summary>Speech speed multiplier sent to the backend (Kokoro accepts ~0.25–4.0).</summary>
    public static readonly CVarDef<float> TtsSpeed =
        CVarDef.Create("tts.speed", 1f, CVar.SERVER | CVar.ARCHIVE);

    /// <summary>
    ///     Sample rate of the raw PCM audio returned by the backend (Kokoro/OpenAI pcm = 24000 Hz,
    ///     16-bit, mono). Replicated so the client can decode it.
    /// </summary>
    public static readonly CVarDef<int> TtsSampleRate =
        CVarDef.Create("tts.sample_rate", 24000, CVar.SERVER | CVar.REPLICATED | CVar.ARCHIVE);

    /// <summary>
    ///     Messages longer than this (characters) are not sent for neural synthesis.
    /// </summary>
    public static readonly CVarDef<int> TtsMaxMessageLength =
        CVarDef.Create("tts.max_message_length", 400, CVar.SERVER | CVar.ARCHIVE);

    // iss14 fix: per-player rate limit for TTS synthesis requests (PlayerRateLimitManager needs CVars).

    /// <summary>
    ///     Period (seconds) over which <see cref="TtsRateLimitCount"/> TTS requests are allowed per player.
    /// </summary>
    public static readonly CVarDef<float> TtsRateLimitPeriod =
        CVarDef.Create("tts.rate_limit_period", 2f, CVar.SERVER | CVar.ARCHIVE);

    /// <summary>
    ///     How many TTS synthesis requests a player may send per <see cref="TtsRateLimitPeriod"/>.
    /// </summary>
    public static readonly CVarDef<int> TtsRateLimitCount =
        CVarDef.Create("tts.rate_limit_count", 8, CVar.SERVER | CVar.ARCHIVE);

    /// <summary>
    ///     Seconds of silence inserted between queued TTS utterances so voices don't run together.
    ///     Replicated so the client's playback queue uses the server-configured value.
    /// </summary>
    public static readonly CVarDef<float> TtsQueueDelay =
        CVarDef.Create("tts.queue_delay", 0.15f, CVar.SERVER | CVar.REPLICATED | CVar.ARCHIVE);

    /// <summary>
    ///     Per-speaker gender assignments, used to pick a player's neural voice from the pool
    ///     matching their character's gender. Serialized as <c>speaker=g;speaker=g</c> where g is
    ///     m/f/e/n (male/female/they-them/it-its). Edited via the <c>ttsconfig</c> admin panel.
    /// </summary>
    public static readonly CVarDef<string> TtsVoiceGenders =
        CVarDef.Create("tts.voice_genders", "", CVar.SERVER | CVar.ARCHIVE);

    // --- Client preferences ---

    /// <summary>
    ///     Per-player toggle for hearing TTS at all.
    /// </summary>
    public static readonly CVarDef<bool> TtsClientEnabled =
        CVarDef.Create("tts.client_enabled", true, CVar.CLIENTONLY | CVar.ARCHIVE);

    /// <summary>
    ///     When true, messages are read aloud with a neural voice (when one is available). When false,
    ///     they play as gibberish blips sized to the message length.
    /// </summary>
    public static readonly CVarDef<bool> TtsReading =
        CVarDef.Create("tts.reading", true, CVar.CLIENTONLY | CVar.ARCHIVE);

    /// <summary>
    ///     Client-side TTS volume multiplier.
    /// </summary>
    public static readonly CVarDef<float> TtsVolume =
        CVarDef.Create("tts.volume", 1f, CVar.CLIENTONLY | CVar.ARCHIVE);

    /// <summary>Read local "say" speech aloud.</summary>
    public static readonly CVarDef<bool> TtsReadSay =
        CVarDef.Create("tts.read_say", true, CVar.CLIENTONLY | CVar.ARCHIVE);

    /// <summary>Read whispered speech aloud.</summary>
    public static readonly CVarDef<bool> TtsReadWhisper =
        CVarDef.Create("tts.read_whisper", true, CVar.CLIENTONLY | CVar.ARCHIVE);

    /// <summary>Read radio / comms messages aloud.</summary>
    public static readonly CVarDef<bool> TtsReadRadio =
        CVarDef.Create("tts.read_radio", true, CVar.CLIENTONLY | CVar.ARCHIVE);

    /// <summary>Read station announcements and AI / computer messages aloud.</summary>
    public static readonly CVarDef<bool> TtsReadAnnouncements =
        CVarDef.Create("tts.read_announcements", true, CVar.CLIENTONLY | CVar.ARCHIVE);

    /// <summary>Read system messages (joins, server notices) aloud.</summary>
    public static readonly CVarDef<bool> TtsReadSystem =
        CVarDef.Create("tts.read_system", false, CVar.CLIENTONLY | CVar.ARCHIVE);

    /// <summary>Read your own outgoing messages aloud.</summary>
    public static readonly CVarDef<bool> TtsReadOwn =
        CVarDef.Create("tts.read_own", false, CVar.CLIENTONLY | CVar.ARCHIVE);
}
