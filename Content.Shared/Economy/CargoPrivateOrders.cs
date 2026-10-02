// iss14: Economy
using Content.Shared.Cargo.Prototypes;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.Economy;

/// <summary>
/// Per-station (per-round) policy for privately paid cargo orders. Lives on the station entity, networked so the
/// console UI can show it. Cargo staff edit it from any cargo order console of the Cargo account.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class CargoPrivateOrderPolicyComponent : Component
{
    /// <summary>Whether the blocklist is applied this round (cargo can switch it off entirely).</summary>
    [DataField, AutoNetworkedField]
    public bool BlocklistEnabled = true;

    /// <summary>Products crew may not buy privately.</summary>
    [DataField, AutoNetworkedField]
    public HashSet<ProtoId<CargoProductPrototype>> Blocked = new();

    /// <summary>Whether <see cref="Blocked"/> was seeded from the default blocklist prototype yet.</summary>
    [DataField]
    public bool Seeded;
}

/// <summary>Default private-order blocklist: whole categories and/or single products.</summary>
[Prototype]
public sealed partial class CargoPrivateBlocklistPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>Category loc ids (as in <see cref="CargoProductPrototype.Category"/>) whose products are blocked.</summary>
    [DataField]
    public List<string> Categories = new();

    [DataField]
    public List<ProtoId<CargoProductPrototype>> Products = new();
}

/// <summary>Approves an order and pays it from the actor's own bank account (ID card).</summary>
[Serializable, NetSerializable]
public sealed class CargoConsoleApprovePrivateOrderMessage(int orderId) : BoundUserInterfaceMessage
{
    public readonly int OrderId = orderId;
}

/// <summary>Cargo staff: block or unblock a product for private orders this round.</summary>
[Serializable, NetSerializable]
public sealed class CargoConsoleSetPrivateBlockMessage(string product, bool blocked) : BoundUserInterfaceMessage
{
    public readonly string Product = product;
    public readonly bool Blocked = blocked;
}

/// <summary>Cargo staff: switch the private-order blocklist on or off this round.</summary>
[Serializable, NetSerializable]
public sealed class CargoConsoleTogglePrivateBlocklistMessage : BoundUserInterfaceMessage;
