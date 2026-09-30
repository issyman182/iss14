using System.IO;
using Content.Client.Barks;
using Content.Client.UserInterface.Systems.Chat;
using Content.Shared.Barks;
using Content.Shared.CCVar;
using Content.Shared.Chat;
using Content.Shared.TTS;
using Robust.Client.Audio;
using Robust.Client.UserInterface;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Client.TTS;

/// <summary>
/// Drives Text-To-Speech from the chat messages this client receives. Each message's channel
/// decides its category; per-category client toggles decide whether it's voiced, and the mode
/// toggle decides gibberish vs. neural reading. Utterances are played one at a time in a queue
/// so voices don't overlap.
/// </summary>
public sealed partial class TTSSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private INetManager _net = default!;
    [Dependency] private IUserInterfaceManager _ui = default!;
    [Dependency] private IAudioManager _audioManager = default!;
    [Dependency] private AudioSystem _audio = default!;
    [Dependency] private BarkSystem _barks = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private ISharedPlayerManager _playerManager = default!;

    private const string DefaultVoice = "Alto";
    private const int MaxQueue = 32;

    private ChatUIController? _chat;

    // Serial playback queue so voices line up instead of overlapping.
    private readonly Queue<QueuedTts> _queue = new();
    private TimeSpan _busyUntil;

    // Recently-voiced text -> time, to drop the duplicate copy of one utterance. A comms message
    // arrives as both a local Whisper (with a sender) and a Radio message (no sender), so we key on
    // text alone — there's no shared sender to match on.
    private readonly Dictionary<string, TimeSpan> _recent = new();
    private static readonly TimeSpan DedupeWindow = TimeSpan.FromSeconds(1);

    // Text of your own recent local messages -> time. Radio messages carry no sender, so this is
    // how we recognise (and skip) the radio echo of something you just said.
    private readonly Dictionary<string, TimeSpan> _ownRecent = new();
    private static readonly TimeSpan OwnEchoWindow = TimeSpan.FromSeconds(3);

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<PlayTtsEvent>(OnPlayTts);
        SubscribeNetworkEvent<TtsFallbackEvent>(OnTtsFallback);

        // Keep the server informed of our prefs so it can suppress overlapping built-in SFX.
        Subs.CVar(_cfg, CCVars.TtsClientEnabled, _ => SendSuppressionState());
        Subs.CVar(_cfg, CCVars.TtsReading, _ => SendSuppressionState());
        Subs.CVar(_cfg, CCVars.TtsReadAnnouncements, _ => SendSuppressionState());
        // iss14: the boot-time send never reached the server (client isn't connected yet at system
        // init) - send on connect instead so the server learns the prefs of every joining player.
        _net.Connected += OnNetConnected; // iss14 fix: named handler so Shutdown can unsubscribe it.

        _chat = _ui.GetUIController<ChatUIController>();
        _chat.MessageAdded += OnChatMessage;
    }

    private void SendSuppressionState()
    {
        // iss14: cvar callbacks also fire while disconnected (main menu, replay loading applies
        // recorded cvars) - don't spam 'tried to send message while not connected' errors.
        if (!_net.IsConnected)
            return;

        RaiseNetworkEvent(new TtsSuppressionStateEvent(
            _cfg.GetCVar(CCVars.TtsClientEnabled),
            _cfg.GetCVar(CCVars.TtsReading),
            _cfg.GetCVar(CCVars.TtsReadAnnouncements)));
    }

    // iss14 fix: named handler for INetManager.Connected so it can be unsubscribed on Shutdown.
    private void OnNetConnected(object? sender, NetChannelArgs args)
    {
        SendSuppressionState();
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _net.Connected -= OnNetConnected;
        if (_chat != null)
            _chat.MessageAdded -= OnChatMessage;
    }

    private void OnChatMessage(ChatMessage msg)
    {
        if (msg.HideChat)
            return;

        if (!_cfg.GetCVar(CCVars.TtsEnabled) || !_cfg.GetCVar(CCVars.TtsClientEnabled))
            return;

        var text = msg.Message;
        if (string.IsNullOrWhiteSpace(text))
            return;

        var source = GetEntity(msg.SenderEntity);
        var hasSource = msg.SenderEntity.IsValid() && Exists(source);
        var readOwn = _cfg.GetCVar(CCVars.TtsReadOwn);
        var own = hasSource && source == _playerManager.LocalEntity;

        // Remember what you say locally so we can recognise its sender-less radio echo below.
        if (own)
            RememberOwn(text);

        // A radio/comms message has no sender entity, so the check above can't tell it's yours.
        // Match it to the local copy you just spoke and skip it when not reading your own.
        if (!readOwn && !hasSource && WasRecentlyOwn(text))
            return;

        // Skip your own messages unless opted in.
        if (own && !readOwn)
            return;

        if (!ShouldVoice(msg.Channel, hasSource))
            return;

        // Speaking on the radio also makes you speak locally (a whisper), so the same line reaches a
        // nearby listener twice — once as Local/Whisper, once as Radio. Read it only once.
        if (IsDuplicate(text))
            return;

        var whisper = msg.Channel == ChatChannel.Whisper;
        var voiceId = ResolveVoice(hasSource ? source : null);
        var playSource = hasSource ? source : (EntityUid?) null;

        // A distance-muffled whisper is mostly '~' characters; reading those literally sounds wrong,
        // so play it as gibberish blips instead — like overhearing indistinct whispering.
        var muffled = whisper && IsMuffled(text);

        if (_cfg.GetCVar(CCVars.TtsReading) && !muffled)
            RaiseNetworkEvent(new RequestTtsEvent(text, voiceId, hasSource ? msg.SenderEntity : NetEntity.Invalid, whisper));
        else
            Enqueue(QueuedTts.Gibberish(playSource, text, whisper, voiceId));
    }

    /// <summary>
    /// A whisper heard from a distance has ~80% of its characters replaced with '~'. Detect that so
    /// it can be played as muffled blips rather than read literally.
    /// </summary>
    private static bool IsMuffled(string text)
    {
        var nonSpace = 0;
        var tilde = 0;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
                continue;

            nonSpace++;
            if (c == '~')
                tilde++;
        }

        return nonSpace > 0 && tilde * 3 >= nonSpace;
    }

    private void OnPlayTts(PlayTtsEvent ev)
    {
        if (ev.Data.Length != 0)
            Enqueue(QueuedTts.Neural(ev.Data, ev.Source));
    }

    private void OnTtsFallback(TtsFallbackEvent ev)
    {
        var source = GetEntity(ev.Source);
        var playSource = ev.Source.IsValid() && Exists(source) ? source : (EntityUid?) null;
        Enqueue(QueuedTts.Gibberish(playSource, ev.Text, ev.Whisper, ev.VoiceId));
    }

    private void Enqueue(QueuedTts item)
    {
        if (_queue.Count >= MaxQueue)
            return;
        _queue.Enqueue(item);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_timing.CurTime < _busyUntil || _queue.Count == 0)
            return;

        var duration = Play(_queue.Dequeue());
        var gap = TimeSpan.FromSeconds(MathF.Max(0f, _cfg.GetCVar(CCVars.TtsQueueDelay)));
        _busyUntil = _timing.CurTime + duration + gap;
    }

    private TimeSpan Play(QueuedTts item)
    {
        if (!item.IsNeural)
            return _barks.PlayGibberish(item.GibSource, item.Message, item.Whisper, item.VoiceId);

        var data = item.Data!;
        AudioStream stream;
        try
        {
            if (StartsWith(data, 'R', 'I', 'F', 'F'))
            {
                using var ms = new MemoryStream(data);
                stream = _audioManager.LoadAudioWav(ms, "tts");
            }
            else if (StartsWith(data, 'O', 'g', 'g', 'S'))
            {
                using var ms = new MemoryStream(data);
                stream = _audioManager.LoadAudioOggVorbis(ms, "tts");
            }
            else
            {
                // Raw PCM (Kokoro/OpenAI pcm): 16-bit signed little-endian, mono, at the configured rate.
                var sampleRate = _cfg.GetCVar(CCVars.TtsSampleRate);
                var samples = new short[data.Length / 2];
                for (var i = 0; i < samples.Length; i++)
                    samples[i] = (short) (data[i * 2] | (data[i * 2 + 1] << 8));

                stream = _audioManager.LoadAudioRaw(samples, 1, sampleRate, "tts");
            }
        }
        catch (Exception e)
        {
            Log.Warning($"Failed to decode TTS audio ({data.Length} bytes): {e.Message}");
            return TimeSpan.Zero;
        }

        var volume = SharedAudioSystem.GainToVolume(_cfg.GetCVar(CCVars.TtsVolume));
        var audioParams = AudioParams.Default.WithVolume(volume);

        var source = GetEntity(item.NeuralSource);
        if (item.NeuralSource.IsValid() && Exists(source))
            _audio.PlayEntity(stream, source, null, audioParams);
        else
            _audio.PlayGlobal(stream, null, audioParams);

        return stream.Length;
    }

    private bool ShouldVoice(ChatChannel channel, bool hasSource)
    {
        switch (channel)
        {
            case ChatChannel.Local:
                return _cfg.GetCVar(CCVars.TtsReadSay);
            case ChatChannel.Whisper:
                return _cfg.GetCVar(CCVars.TtsReadWhisper);
            case ChatChannel.Radio:
                // Radio carries both comms (a real speaker) and station announcements (no speaker).
                return hasSource
                    ? _cfg.GetCVar(CCVars.TtsReadRadio)
                    : _cfg.GetCVar(CCVars.TtsReadAnnouncements);
            case ChatChannel.Notifications:
                return _cfg.GetCVar(CCVars.TtsReadAnnouncements);
            case ChatChannel.Server:
                return _cfg.GetCVar(CCVars.TtsReadSystem);
            default:
                return false;
        }
    }

    private void RememberOwn(string text)
    {
        var now = _timing.CurTime;
        _ownRecent[text] = now;

        if (_ownRecent.Count > 64)
        {
            var stale = new List<string>();
            foreach (var (k, t) in _ownRecent)
            {
                if (now - t >= OwnEchoWindow)
                    stale.Add(k);
            }
            foreach (var k in stale)
                _ownRecent.Remove(k);
        }
    }

    private bool WasRecentlyOwn(string text)
    {
        return _ownRecent.TryGetValue(text, out var last) && _timing.CurTime - last < OwnEchoWindow;
    }

    private bool IsDuplicate(string text)
    {
        var now = _timing.CurTime;

        if (_recent.TryGetValue(text, out var last) && now - last < DedupeWindow)
            return true;

        _recent[text] = now;

        // Opportunistically drop stale entries.
        if (_recent.Count > 64)
        {
            var stale = new List<string>();
            foreach (var (k, t) in _recent)
            {
                if (now - t >= DedupeWindow)
                    stale.Add(k);
            }
            foreach (var k in stale)
                _recent.Remove(k);
        }

        return false;
    }

    private string ResolveVoice(EntityUid? source)
    {
        if (source != null
            && TryComp<SpeechSynthesisComponent>(source.Value, out var comp)
            && comp.VoicePrototypeId is { } voice)
        {
            return voice;
        }

        return DefaultVoice;
    }

    private static bool StartsWith(byte[] data, char c0, char c1, char c2, char c3)
    {
        return data.Length >= 4
               && data[0] == c0 && data[1] == c1 && data[2] == c2 && data[3] == c3;
    }

    private sealed class QueuedTts
    {
        public bool IsNeural;
        public byte[]? Data;
        public NetEntity NeuralSource;

        public EntityUid? GibSource;
        public string Message = string.Empty;
        public bool Whisper;
        public string VoiceId = string.Empty;

        public static QueuedTts Neural(byte[] data, NetEntity source)
            => new() { IsNeural = true, Data = data, NeuralSource = source };

        public static QueuedTts Gibberish(EntityUid? source, string message, bool whisper, string voiceId)
            => new() { IsNeural = false, GibSource = source, Message = message, Whisper = whisper, VoiceId = voiceId };
    }
}
