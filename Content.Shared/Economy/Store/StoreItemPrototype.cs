// iss14: Economy
using Content.Shared.Preferences.Loadouts;
using Robust.Shared.Prototypes;

namespace Content.Shared.Economy.Store;

public enum StoreItemKind : byte
{
    /// <summary>A title shown in front of the character's name (ID card, examine, chat).</summary>
    Title,

    /// <summary>Unlocks a loadout item (one that has an <c>UnlockLoadoutEffect</c> requiring this store item).</summary>
    Loadout,
}

/// <summary>
/// Something a player can buy once with their persistent bank balance (lobby store). Owned items are stored per
/// player in the database (<c>player_unlock</c>).
/// </summary>
[Prototype]
public sealed partial class StoreItemPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public LocId Name;

    [DataField]
    public LocId? Description;

    [DataField]
    public StoreItemKind Kind = StoreItemKind.Title;

    [DataField]
    public long Price;

    /// <summary>Localized group heading in the store.</summary>
    [DataField]
    public LocId Category = "store-category-titles";

    /// <summary>For titles: the text shown in front of the name.</summary>
    [DataField]
    public string? TitleText;

    /// <summary>For loadout unlocks: the loadout this grants, for display.</summary>
    [DataField]
    public ProtoId<LoadoutPrototype>? Loadout;

    /// <summary>Hidden from the store (admin-granted only).</summary>
    [DataField]
    public bool Hidden;
}
