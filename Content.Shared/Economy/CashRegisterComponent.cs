// iss14: Economy
using Content.Shared.Cargo.Prototypes;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.Economy;

/// <summary>
/// A counter cash register. Staff (access reader) open the UI, see what lies on the counter around the register,
/// set prices and write a bill; the customer pays by swiping their ID card on the register or handing it cash.
/// Proceeds go to the register's department account, minus a commission for the cashier.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class CashRegisterComponent : Component
{
    /// <summary>Department account that receives the sales.</summary>
    [DataField, AutoNetworkedField]
    public ProtoId<CargoAccountPrototype> Account = "Service";

    /// <summary>How far around the register items are listed as sellable.</summary>
    [DataField]
    public float Range = 1.6f;

    /// <summary>Staff price overrides per item prototype (credits). Missing = default price.</summary>
    [DataField]
    public Dictionary<EntProtoId, int> Prices = new();

    /// <summary>The open bill, if any.</summary>
    [DataField, AutoNetworkedField]
    public CashRegisterBill? Bill;

    /// <summary>Cash change is paid out as this stack.</summary>
    [DataField]
    public EntProtoId CashPrototype = "SpaceCash";

    [DataField]
    public SoundSpecifier? PaySound = new SoundPathSpecifier("/Audio/Effects/Cargo/ping.ogg");

    [DataField]
    public SoundSpecifier? DenySound = new SoundPathSpecifier("/Audio/Machines/buzz-two.ogg");

    /// <summary>Player who wrote the current bill (gets the commission).</summary>
    [ViewVariables]
    public NetUserId? CashierUserId;
}

/// <summary>One line of a bill.</summary>
[Serializable, NetSerializable, DataDefinition]
public sealed partial class CashRegisterLine
{
    [DataField]
    public EntProtoId Prototype;

    [DataField]
    public string Name = string.Empty;

    [DataField]
    public int Count;

    /// <summary>Price per piece.</summary>
    [DataField]
    public int Price;

    public int Total => Count * Price;
}

[Serializable, NetSerializable, DataDefinition]
public sealed partial class CashRegisterBill
{
    [DataField]
    public List<CashRegisterLine> Lines = new();

    [DataField]
    public string Cashier = string.Empty;

    public int Total
    {
        get
        {
            var total = 0;
            foreach (var line in Lines)
            {
                total += line.Total;
            }
            return total;
        }
    }
}

[Serializable, NetSerializable]
public enum CashRegisterUiKey : byte
{
    Key,
}

/// <summary>A sellable item near the register as the UI shows it.</summary>
[Serializable, NetSerializable]
public sealed class CashRegisterItem(string prototype, string name, int count, int price, int defaultPrice, bool overridden)
{
    public readonly string Prototype = prototype;
    public readonly string Name = name;
    public readonly int Count = count;
    public readonly int Price = price;
    public readonly int DefaultPrice = defaultPrice;
    public readonly bool Overridden = overridden;
}

[Serializable, NetSerializable]
public sealed class CashRegisterBuiState(
    List<CashRegisterItem> items,
    CashRegisterBill? bill,
    string department,
    int departmentBalance,
    float commissionPercent,
    string message)
    : BoundUserInterfaceState
{
    public readonly List<CashRegisterItem> Items = items;
    public readonly CashRegisterBill? Bill = bill;
    public readonly string Department = department;
    public readonly int DepartmentBalance = departmentBalance;
    public readonly float CommissionPercent = commissionPercent;
    public readonly string Message = message;
}

/// <summary>Writes a bill from the selected items (prototype id -> count).</summary>
[Serializable, NetSerializable]
public sealed class CashRegisterCreateBillMessage(Dictionary<string, int> items) : BoundUserInterfaceMessage
{
    public readonly Dictionary<string, int> Items = items;
}

[Serializable, NetSerializable]
public sealed class CashRegisterCancelBillMessage : BoundUserInterfaceMessage;

/// <summary>Sets a price override; null resets to the default.</summary>
[Serializable, NetSerializable]
public sealed class CashRegisterSetPriceMessage(string prototype, int? price) : BoundUserInterfaceMessage
{
    public readonly string Prototype = prototype;
    public readonly int? Price = price;
}

[Serializable, NetSerializable]
public sealed class CashRegisterRefreshMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public enum CashRegisterVisuals : byte
{
    HasBill,
}

[Serializable, NetSerializable]
public enum CashRegisterVisualLayers : byte
{
    Screen,
}
