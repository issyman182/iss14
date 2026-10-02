using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.VendingMachines;

[Serializable, NetSerializable, DataDefinition]
public sealed partial class VendingMachineInventoryEntry(InventoryType type, EntProtoId id, uint amount)
{
    [DataField]
    public InventoryType Type = type;

    [DataField]
    public EntProtoId ID = id;

    [DataField]
    public uint Amount = amount;

    /// <summary>
    /// iss14: Economy — credits charged per vend when <c>economy.vending_prices</c> is on. Filled by the server
    /// from the item's static price; 0 means free.
    /// </summary>
    public int Price;

    public VendingMachineInventoryEntry(VendingMachineInventoryEntry entry) : this(entry.Type, entry.ID, entry.Amount)
    {
        Price = entry.Price;
    }
}

[Serializable, NetSerializable]
public enum InventoryType : byte
{
    Regular,
    Emagged,
    Contraband
}

[Serializable, NetSerializable]
public sealed class VendingMachineComponentState : ComponentState
{
    public Dictionary<string, VendingMachineInventoryEntry> Inventory = new();

    public Dictionary<string, VendingMachineInventoryEntry> EmaggedInventory = new();

    public Dictionary<string, VendingMachineInventoryEntry> ContrabandInventory = new();

    public bool Contraband;

    public bool Broken;
}
