// iss14: Economy
using Content.Shared.Access;
using Content.Shared.Cargo.Prototypes;
using Content.Shared.Nutrition.Prototypes;
using Content.Shared.Tag;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.Economy;

/// <summary>
/// A counter cash register: an anchored, APC-powered machine (wrench to move, delivered as a flatpack). There is
/// one kind of register for the whole station; who may use it and what it sells follows from the ID card of the
/// person clicking it:
/// <list type="bullet">
/// <item>The cashier sees the catalogs (<see cref="CashRegisterCatalogPrototype"/>) matching their access — a
/// bartender the bar, a chef the kitchen, the Captain all of them. Someone with no matching catalog is a customer.</item>
/// <item>Sales are booked to the cashier's department account (chosen in the UI when they belong to several).</item>
/// </list>
/// The cashier puts items on a draft bill from the list, by "scanning" (clicking the register with the item in
/// hand) or by typing a name and price, then writes the bill. Customers click the register to see it and pay with
/// the ID card they carry, or swipe their ID/PDA or hand over cash. A commission goes to the cashier.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class CashRegisterComponent : Component
{
    /// <summary>How far around the register items are listed as sellable.</summary>
    [DataField]
    public float Range = 1.6f;

    /// <summary>Price overrides of this register (prototype id -> price per piece), on top of the global defaults.</summary>
    [DataField]
    public Dictionary<EntProtoId, int> Prices = new();

    /// <summary>The bill the cashier is currently putting together (not yet payable).</summary>
    [DataField, AutoNetworkedField]
    public CashRegisterBill Draft = new();

    /// <summary>The open (payable) bill, if any.</summary>
    [DataField, AutoNetworkedField]
    public CashRegisterBill? Bill;

    [DataField]
    public EntProtoId CashPrototype = "SpaceCash";

    [DataField]
    public SoundSpecifier? PaySound = new SoundPathSpecifier("/Audio/Effects/Cargo/ping.ogg");

    [DataField]
    public SoundSpecifier? ScanSound = new SoundCollectionSpecifier("CargoBeep");

    [DataField]
    public SoundSpecifier? DenySound = new SoundPathSpecifier("/Audio/Machines/buzz-two.ogg");

    /// <summary>The player who wrote the open bill (receives the commission).</summary>
    [ViewVariables]
    public NetUserId? CashierUserId;
}

/// <summary>
/// What a job sells at a cash register: the price list the cashier gets and the filter for the items the register
/// auto-detects around it. An item matches when it has any of <see cref="Components"/>, any of <see cref="Tags"/>,
/// or the given <see cref="Edible"/> kind — and none of <see cref="BlacklistComponents"/>.
/// </summary>
[Prototype]
public sealed partial class CashRegisterCatalogPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public LocId Name;

    /// <summary>
    /// Access levels of the jobs this catalog is for. A person with any of them may use the register as a cashier
    /// and sells from this catalog. The Captain sees every catalog.
    /// </summary>
    [DataField(required: true)]
    public List<ProtoId<AccessLevelPrototype>> Access = new();

    /// <summary>Component names (as in prototypes, e.g. <c>Produce</c>) of which an item needs any.</summary>
    [DataField]
    public List<string> Components = new();

    [DataField]
    public List<ProtoId<TagPrototype>> Tags = new();

    /// <summary>Edible kind (<c>Food</c>, <c>Drink</c>, <c>Pill</c>) an item's Edible component must have.</summary>
    [DataField]
    public ProtoId<EdiblePrototype>? Edible;

    /// <summary>
    /// With <see cref="Edible"/>: the solution name the Edible component must use. Drinks use <c>drink</c>, food
    /// <c>food</c>; chemistry glassware uses <c>beaker</c>, which keeps beakers out of the bar catalog.
    /// </summary>
    [DataField]
    public string? EdibleSolution;

    /// <summary>Items with any of these components never match, whatever else they are.</summary>
    [DataField]
    public List<string> BlacklistComponents = new();

    /// <summary>Order in the cashier's catalog list.</summary>
    [DataField]
    public int Priority;
}

/// <summary>One line of a bill. Lines with the same name and price are merged.</summary>
[Serializable, NetSerializable, DataDefinition]
public sealed partial class CashRegisterLine
{
    /// <summary>The item prototype, or null for a line the cashier typed in by hand.</summary>
    [DataField]
    public EntProtoId? Prototype;

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

    /// <summary>Department account the sale is booked to (the cashier's department).</summary>
    [DataField]
    public ProtoId<CargoAccountPrototype> Account = "Service";

    public int Total
    {
        get
        {
            var total = 0;
            foreach (var line in Lines)
                total += line.Total;
            return total;
        }
    }
}

[Serializable, NetSerializable]
public enum CashRegisterUiKey : byte
{
    /// <summary>Cashier UI (needs a matching catalog).</summary>
    Key,

    /// <summary>Customer UI: the open bill and a pay button.</summary>
    Customer,
}

/// <summary>A sellable item as the UI shows it.</summary>
[Serializable, NetSerializable]
public sealed class CashRegisterItem(string prototype, string name, int count, int price, int defaultPrice, bool overridden, string catalog)
{
    public readonly string Prototype = prototype;
    public readonly string Name = name;

    /// <summary>How many lie around the register right now (0 for catalog-only entries).</summary>
    public readonly int Count = count;
    public readonly int Price = price;
    public readonly int DefaultPrice = defaultPrice;

    /// <summary>True when this register has its own price for the item.</summary>
    public readonly bool Overridden = overridden;

    /// <summary>Localized catalog name the item belongs to.</summary>
    public readonly string Catalog = catalog;
}

/// <summary>A department account the cashier may book sales to.</summary>
[Serializable, NetSerializable]
public sealed class CashRegisterAccount(string id, string name)
{
    public readonly string Id = id;
    public readonly string Name = name;
}

[Serializable, NetSerializable]
public sealed class CashRegisterBuiState(
    List<CashRegisterItem> items,
    List<string> catalogs,
    List<CashRegisterAccount> accounts,
    CashRegisterBill draft,
    CashRegisterBill? bill,
    string department,
    int departmentBalance,
    float commissionPercent,
    string message)
    : BoundUserInterfaceState
{
    /// <summary>Everything in the cashier's catalogs plus whatever sellable lies around the register.</summary>
    public readonly List<CashRegisterItem> Items = items;

    /// <summary>Localized names of the catalogs the cashier sells from (for the filter window).</summary>
    public readonly List<string> Catalogs = catalogs;

    /// <summary>Department accounts the cashier may book to; the draft's account is the selected one.</summary>
    public readonly List<CashRegisterAccount> Accounts = accounts;

    public readonly CashRegisterBill Draft = draft;
    public readonly CashRegisterBill? Bill = bill;

    /// <summary>Name of the selected department account.</summary>
    public readonly string Department = department;
    public readonly int DepartmentBalance = departmentBalance;
    public readonly float CommissionPercent = commissionPercent;
    public readonly string Message = message;
}

/// <summary>What the customer sees.</summary>
[Serializable, NetSerializable]
public sealed class CashRegisterCustomerBuiState(CashRegisterBill? bill, string department, string message) : BoundUserInterfaceState
{
    public readonly CashRegisterBill? Bill = bill;
    public readonly string Department = department;
    public readonly string Message = message;
}

/// <summary>Cashier: adds <paramref name="count"/> of an item to the draft (negative removes).</summary>
[Serializable, NetSerializable]
public sealed class CashRegisterAdjustDraftMessage(string prototype, int count) : BoundUserInterfaceMessage
{
    public readonly string Prototype = prototype;
    public readonly int Count = count;
}

/// <summary>Cashier: adds a hand-written line (e.g. a mixed drink not on the list) to the draft.</summary>
[Serializable, NetSerializable]
public sealed class CashRegisterAddManualMessage(string name, int price, int count) : BoundUserInterfaceMessage
{
    public readonly string Name = name;
    public readonly int Price = price;
    public readonly int Count = count;
}

/// <summary>Cashier: removes a draft line by index.</summary>
[Serializable, NetSerializable]
public sealed class CashRegisterRemoveLineMessage(int index) : BoundUserInterfaceMessage
{
    public readonly int Index = index;
}

/// <summary>Cashier: selects the department account the sale is booked to.</summary>
[Serializable, NetSerializable]
public sealed class CashRegisterSetAccountMessage(string account) : BoundUserInterfaceMessage
{
    public readonly string Account = account;
}

/// <summary>Cashier: empties the draft.</summary>
[Serializable, NetSerializable]
public sealed class CashRegisterClearDraftMessage : BoundUserInterfaceMessage;

/// <summary>Cashier: turns the draft into the open bill.</summary>
[Serializable, NetSerializable]
public sealed class CashRegisterCreateBillMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class CashRegisterCancelBillMessage : BoundUserInterfaceMessage;

/// <summary>Cashier: sets this register's price for a prototype; null resets to the default.</summary>
[Serializable, NetSerializable]
public sealed class CashRegisterSetPriceMessage(string prototype, int? price) : BoundUserInterfaceMessage
{
    public readonly string Prototype = prototype;
    public readonly int? Price = price;
}

[Serializable, NetSerializable]
public sealed class CashRegisterRefreshMessage : BoundUserInterfaceMessage;

/// <summary>Customer: pays the open bill with the ID card they carry.</summary>
[Serializable, NetSerializable]
public sealed class CashRegisterPayMessage : BoundUserInterfaceMessage;

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
