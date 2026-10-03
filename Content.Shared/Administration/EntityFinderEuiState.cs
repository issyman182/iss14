// iss14
using Content.Shared.Eui;
using Robust.Shared.Serialization;

namespace Content.Shared.Administration;

/// <summary>Which kinds of entities the finder lists.</summary>
[Serializable, NetSerializable]
public enum EntityFinderCategory : byte
{
    All,

    /// <summary>Anything anchored to a grid: walls, machines, furniture, pipes...</summary>
    Anchored,

    /// <summary>Powered machines and devices (APC power receivers).</summary>
    Machines,

    /// <summary>Economy devices: cash registers, task boards, ATMs, fine terminals, chip exchangers.</summary>
    Economy,

    /// <summary>Lockers, crates and other entity storage.</summary>
    Storage,

    Items,
    Mobs,
    Players,

    /// <summary>Station beacons (nav map names) and warp points.</summary>
    Beacons,
}

/// <summary>One entity in the finder's result list.</summary>
[Serializable, NetSerializable]
public sealed class EntityFinderRow(
    NetEntity entity,
    string name,
    string prototype,
    string gridPosition,
    string worldPosition,
    int mapId,
    float distance,
    bool anchored)
{
    public readonly NetEntity Entity = entity;
    public readonly string Name = name;
    public readonly string Prototype = prototype;

    /// <summary>Position relative to the grid (what the map file calls <c>pos</c>), or "-" when not on a grid.</summary>
    public readonly string GridPosition = gridPosition;

    /// <summary>World (map) position, what <c>tp</c> and the F3 overlay's map coordinates use.</summary>
    public readonly string WorldPosition = worldPosition;

    public readonly int MapId = mapId;

    /// <summary>Distance in tiles from the admin; negative when on another map.</summary>
    public readonly float Distance = distance;

    public readonly bool Anchored = anchored;
}

[Serializable, NetSerializable]
public sealed class EntityFinderEuiState(List<EntityFinderRow> rows, int total, string adminPosition, string status) : EuiStateBase
{
    /// <summary>Matching entities, nearest first, cut to the row limit.</summary>
    public readonly List<EntityFinderRow> Rows = rows;

    /// <summary>How many entities matched before the cut.</summary>
    public readonly int Total = total;

    /// <summary>Where the admin is right now (world and grid position), for reference.</summary>
    public readonly string AdminPosition = adminPosition;

    public readonly string Status = status;
}

/// <summary>Client -> server: run a search.</summary>
[Serializable, NetSerializable]
public sealed class EntityFinderSearchMessage(string search, EntityFinderCategory category, float radius, bool sameMapOnly) : EuiMessageBase
{
    /// <summary>Matched against the entity name and the prototype id, case-insensitive. Empty = everything.</summary>
    public readonly string Search = search;
    public readonly EntityFinderCategory Category = category;

    /// <summary>Only entities within this many tiles of the admin; 0 = no limit.</summary>
    public readonly float Radius = radius;

    public readonly bool SameMapOnly = sameMapOnly;
}

/// <summary>Client -> server: teleport the admin to the entity.</summary>
[Serializable, NetSerializable]
public sealed class EntityFinderTeleportMessage(NetEntity entity) : EuiMessageBase
{
    public readonly NetEntity Entity = entity;
}
