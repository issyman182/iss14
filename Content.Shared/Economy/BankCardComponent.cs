// iss14: Economy
using Robust.Shared.Network;

namespace Content.Shared.Economy;

/// <summary>
/// Binds an ID card to a player's bank account. Stamped onto the card when the character spawns.
/// Whoever inserts the card into an ATM gets access to the account, unless the ATM is configured to
/// verify the holder's fingerprint (<c>economy.atm_require_fingerprint</c>).
/// </summary>
[RegisterComponent]
public sealed partial class BankCardComponent : Component
{
    /// <summary>The player (user id) whose account this card is bound to.</summary>
    [ViewVariables]
    public NetUserId? OwnerUserId;

    /// <summary>Fingerprint of the character the card was issued to, if the mob had one.</summary>
    [DataField]
    public string? OwnerFingerprint;

    /// <summary>Character name the card was issued to, for display in ATM UIs.</summary>
    [DataField]
    public string? OwnerName;
}
