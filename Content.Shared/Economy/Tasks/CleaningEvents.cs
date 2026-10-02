// iss14: Economy — janitor task hooks.
using Robust.Shared.Map;

namespace Content.Shared.Economy.Tasks;

/// <summary>
/// Raised (broadcast) whenever a mop/absorbent removed actual contaminant from a puddle. <see cref="Removed"/> is
/// the volume of non-water reagent taken out; a puddle that was only diluted raises nothing.
/// </summary>
[ByRefEvent]
public readonly record struct PuddleMoppedEvent(EntityUid User, EntityUid Puddle, float Removed, bool Deleted);

/// <summary>Raised (broadcast) when cleanable decals (graffiti, dirt) were scrubbed off a tile.</summary>
[ByRefEvent]
public readonly record struct DecalsCleanedEvent(EntityUid Grid, Vector2i Tile, int Count);
