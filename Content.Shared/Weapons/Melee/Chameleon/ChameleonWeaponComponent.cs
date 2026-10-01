// iss14: Cybersun MIMIC-5
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.Weapons.Melee.Chameleon;

/// <summary>
///     Lets a weapon take on the sprite, in-hand visuals, name and description of another weapon prototype.
///     Purely cosmetic: the mimicked prototype's gameplay components (<see cref="MeleeWeaponComponent"/>,
///     guns, ammo, ...) are never copied. Modelled on <c>ChameleonClothingComponent</c>.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
[Access(typeof(SharedChameleonWeaponSystem))]
public sealed partial class ChameleonWeaponComponent : Component
{
    /// <summary>
    ///     The currently selected EntityPrototype ID this weapon is disguised as.
    ///     Null means the weapon shows its own sprite, name and description.
    /// </summary>
    [DataField, AutoNetworkedField]
    public EntProtoId? Selected;

    /// <summary>
    ///     Show a verb for toggling the disguise UI?
    /// </summary>
    [DataField]
    public bool ShowVerb = true;

    /// <summary>
    ///     Open the disguise UI when the weapon is used in hand (Z / activate)?
    /// </summary>
    [DataField]
    public bool OpenOnUseInHand = true;
}

[Serializable, NetSerializable]
public sealed class ChameleonWeaponBoundUserInterfaceState : BoundUserInterfaceState
{
    public readonly string? SelectedId;

    public ChameleonWeaponBoundUserInterfaceState(string? selectedId)
    {
        SelectedId = selectedId;
    }
}

[Serializable, NetSerializable]
public sealed class ChameleonWeaponPrototypeSelectedMessage : BoundUserInterfaceMessage
{
    public readonly string SelectedId;

    public ChameleonWeaponPrototypeSelectedMessage(string selectedId)
    {
        SelectedId = selectedId;
    }
}

[Serializable, NetSerializable]
public enum ChameleonWeaponUiKey : byte
{
    Key
}
