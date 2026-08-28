using Content.Server.TTS;
using Content.Shared.Speech.EntitySystems;

namespace Content.Server.Speech.EntitySystems;

/// <summary>
/// Suppresses the vanilla speech blip for listeners whose TTS will voice the message (gibberish or
/// neural). Clients with TTS off still hear it.
/// </summary>
/// <remarks>
/// iss14: <see cref="SpeechSoundSystem"/> lives in Shared and cannot reach the server-only TTS system,
/// so the filtering is applied through <see cref="SpeechSoundFilterEvent"/>.
/// </remarks>
public sealed class SpeechSoundTtsFilterSystem : EntitySystem
{
    [Dependency] private TTSSystem _tts = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<SpeechSoundFilterEvent>(OnSpeechSoundFilter);
    }

    private void OnSpeechSoundFilter(ref SpeechSoundFilterEvent args)
    {
        args.Filter = args.Filter.RemoveWhere(_tts.SuppressesSpeech);
    }
}
