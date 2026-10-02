// Ported from Starlight (https://github.com/ss14Starlight/space-station-14), station radio originally from Goob-Station.
// Starlight code is MIT / Starlight License; the Starlight License requires this attribution.
namespace Content.Shared._Starlight.StationRadio.Events;

/// <summary>
/// Raised on a vinyl player when a vinyl is inserted and starts playing.
/// </summary>
[ByRefEvent]
public record struct VinylInsertedEvent(EntityUid Vinyl);

/// <summary>
/// Raised on a vinyl player when a vinyl is removed.
/// </summary>
[ByRefEvent]
public record struct VinylRemovedEvent(EntityUid Vinyl);
