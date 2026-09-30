using Content.Shared.Barks;
using Content.Shared.CCVar;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Client.Barks;

/// <summary>
/// The gibberish ("blip") voice engine: steps through a message and plays a short, pitched
/// sound per character. Driven by <see cref="Content.Client.TTS.TTSSystem"/> for received chat,
/// and by <see cref="PreviewBarkEvent"/> for the character editor preview. Ported from Goob-Station.
/// </summary>
public sealed partial class BarkSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototypeManager = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private SharedAudioSystem _sharedAudio = default!;

    private readonly Dictionary<NetEntity, EntityUid> _playingSounds = new();
    private static readonly char[] Characters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890".ToCharArray();

    private readonly List<ActiveBark> _activeBarks = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PreviewBarkEvent>(OnPreviewBark);
    }

    public void OnPreviewBark(PreviewBarkEvent ev)
    {
        if (!_prototypeManager.TryIndex<BarkPrototype>(ev.BarkProtoId, out var proto))
            return;

        var messageLength = _random.Next(5, 20);
        var message = new char[messageLength];
        for (var i = 0; i < messageLength; i++)
        {
            message[i] = _random.Pick(Characters);
        }
        PlayBark(null, new string(message), false, proto);
    }

    /// <summary>
    /// Plays a gibberish line for a received message. <paramref name="source"/> null plays globally.
    /// Returns the expected duration of the utterance (zero if nothing played).
    /// </summary>
    public TimeSpan PlayGibberish(EntityUid? source, string message, bool whisper, string voiceId)
    {
        if (!_prototypeManager.TryIndex<BarkPrototype>(voiceId, out var proto))
            return TimeSpan.Zero;

        return PlayBark(source, message, whisper, proto);
    }

    private TimeSpan PlayBark(EntityUid? source, string message, bool whisper, BarkPrototype proto)
    {
        if (proto.SoundCollection is null)
            return TimeSpan.Zero;

        if (message.Length > 50)
            message = message[..50];

        var volume = GetVolume(whisper, proto);
        if (volume <= -20f)
            return TimeSpan.Zero;

        var upperCount = 0;
        foreach (var c in message)
            if (char.IsUpper(c))
                upperCount++;

        if (upperCount > message.Length / 2
            || message.EndsWith("!!"))
            volume += 5;

        var messageLength = message.Length;
        var totalDuration = Math.Max(0.1f, messageLength * 0.05f);
        var soundInterval = 0.08f / proto.Frequency;
        var soundCount = (int) Math.Max(1, totalDuration / soundInterval);

        var activeBark = new ActiveBark
        {
            Source = source,
            IsGlobal = source == null,
            Message = message,
            Prototype = proto,
            Volume = volume,
            TotalSounds = soundCount,
            SoundInterval = soundInterval,
            NextSound = _timing.CurTime
        };

        _activeBarks.Add(activeBark);

        return TimeSpan.FromSeconds(soundCount * soundInterval);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // iss14 fix: prune entries whose source or audio entity is gone, so the dictionary doesn't grow forever.
        if (_playingSounds.Count > 0)
        {
            List<NetEntity>? stale = null;
            foreach (var (netSource, audioUid) in _playingSounds)
            {
                if (TryGetEntity(netSource, out var source) && !TerminatingOrDeleted(source.Value)
                    && !TerminatingOrDeleted(audioUid))
                    continue;

                stale ??= new List<NetEntity>();
                stale.Add(netSource);
            }

            if (stale != null)
            {
                foreach (var netSource in stale)
                {
                    if (_playingSounds.Remove(netSource, out var audioUid) && !TerminatingOrDeleted(audioUid))
                        _sharedAudio.Stop(audioUid);
                }
            }
        }

        for (var i = _activeBarks.Count - 1; i >= 0; i--)
        {
            var bark = _activeBarks[i];

            if (bark.NextSound > _timing.CurTime)
                continue;

            if (!bark.IsGlobal && TerminatingOrDeleted(bark.Source!.Value))
            {
                _activeBarks.RemoveAt(i);
                continue;
            }

            var character = bark.Message[bark.CurrentSound % bark.Message.Length];
            if (character != ' ' && character != '-')
                PlaySound(bark, character);

            bark.CurrentSound++;
            bark.NextSound += TimeSpan.FromSeconds(bark.SoundInterval);

            if (bark.CurrentSound >= bark.TotalSounds)
                _activeBarks.RemoveAt(i);
        }
    }

    private void PlaySound(ActiveBark bark, char character)
    {
        var proto = bark.Prototype;
        var sound = _sharedAudio.ResolveSound(proto.SoundCollection!);
        var audioParams = proto.SoundCollection!.Params;

        if (proto.Predictable)
        {
            // iss14 fix: char.GetHashCode() is negative for chars >= U+8000, giving negative indices/pitch.
            var hashCode = (int) character;

            if (sound is ResolvedCollectionSpecifier collection && collection.Collection != null)
            {
                var soundCollection = _prototypeManager.Index<SoundCollectionPrototype>(collection.Collection);
                if (soundCollection.PickFiles.Count > 0)
                {
                    var index = hashCode % soundCollection.PickFiles.Count;
                    sound = new ResolvedCollectionSpecifier(collection.Collection, index);
                }
            }

            var minPitchInt = (int) (proto.MinPitch * 100);
            var maxPitchInt = (int) (proto.MaxPitch * 100);
            var pitchRangeInt = Math.Abs(maxPitchInt - minPitchInt);
            if (pitchRangeInt != 0)
            {
                var predictablePitchInt = hashCode % pitchRangeInt + Math.Min(minPitchInt, maxPitchInt);
                var predictablePitch = predictablePitchInt / 100f;
                audioParams = audioParams.WithPitchScale(predictablePitch);
            }
            else
            {
                audioParams = audioParams.WithPitchScale(proto.MinPitch);
            }
        }
        else
        {
            audioParams = audioParams.WithPitchScale(_random.NextFloat(proto.MinPitch, proto.MaxPitch));
        }

        audioParams = audioParams.WithVolume(bark.Volume);

        var filter = Filter.Local();
        var soundEntity = bark.IsGlobal
            ? _sharedAudio.PlayGlobal(sound, filter, false, audioParams)
            : _sharedAudio.PlayEntity(sound, filter, bark.Source!.Value, false, audioParams);

        if (!bark.IsGlobal && proto.Stop)
        {
            if (_playingSounds.TryGetValue(GetNetEntity(bark.Source!.Value), out var playing))
                _sharedAudio.Stop(playing);
        }

        if (!bark.IsGlobal && soundEntity is not null)
            _playingSounds[GetNetEntity(bark.Source!.Value)] = soundEntity.Value.Entity;
    }

    private float GetVolume(bool whisper, BarkPrototype proto)
    {
        var volume = proto.Volume;

        if (whisper)
            volume = 0.05f + (volume - 0.05f) * 0.25f;

        var ttsVolume = _cfg.GetCVar(CCVars.TtsVolume);
        volume *= ttsVolume / 3f;

        return SharedAudioSystem.GainToVolume(volume);
    }

    private sealed class ActiveBark
    {
        public EntityUid? Source;
        public bool IsGlobal;
        public string Message = string.Empty;
        public BarkPrototype Prototype = default!;
        public float Volume;

        public int TotalSounds;
        public int CurrentSound;
        public float SoundInterval;
        public TimeSpan NextSound;
    }
}
