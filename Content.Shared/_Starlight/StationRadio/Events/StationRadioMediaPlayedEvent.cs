// Ported from Starlight (https://github.com/ss14Starlight/space-station-14), station radio originally from Goob-Station.
// Starlight code is MIT / Starlight License; the Starlight License requires this attribution.
using Robust.Shared.Audio;
using Robust.Shared.Serialization;

namespace Content.Shared._Starlight.StationRadio.Events;

[Serializable, NetSerializable]
public sealed class StationRadioMediaPlayedEvent : EntityEventArgs
{
    public SoundPathSpecifier MediaPlayed { get; }
    public TimeSpan StartTime;
    public StationRadioMediaPlayedEvent(SoundPathSpecifier media, TimeSpan startTime = default)
    {
        MediaPlayed = media;
        StartTime = startTime;
    }
}
