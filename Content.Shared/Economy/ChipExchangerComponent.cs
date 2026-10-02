// iss14: Economy
using Robust.Shared.Audio;
using Robust.Shared.Prototypes;

namespace Content.Shared.Economy;

/// <summary>
/// Swaps spesos for poker chips and back at <c>economy.chip_rate</c> credits per chip.
/// Put a cash stack in the cash slot and use "Cash → chips", or chips in the chip slot and "Chips → cash".
/// </summary>
[RegisterComponent]
public sealed partial class ChipExchangerComponent : Component
{
    public const string CashSlotId = "exchanger-cash";
    public const string ChipSlotId = "exchanger-chips";

    [DataField]
    public EntProtoId CashPrototype = "SpaceCash";

    [DataField]
    public EntProtoId ChipPrototype = "PokerChips";

    [DataField]
    public SoundSpecifier? ExchangeSound = new SoundPathSpecifier("/Audio/Effects/Cargo/ping.ogg");

    [DataField]
    public SoundSpecifier? DenySound = new SoundPathSpecifier("/Audio/Machines/buzz-two.ogg");
}
