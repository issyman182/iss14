// iss14: Cybersun MIMIC-5
using Content.Shared.Weapons.Melee.Chameleon;
using Robust.Shared.Prototypes;

namespace Content.Server.Weapons.Melee.Chameleon;

/// <summary>
///     Server side of the chameleon weapon: the only place a disguise selection is accepted.
///     Validates the requested prototype id and stores it in the networked component; visuals follow from that.
/// </summary>
public sealed partial class ChameleonWeaponSystem : SharedChameleonWeaponSystem
{
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ChameleonWeaponComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<ChameleonWeaponComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<ChameleonWeaponComponent, ChameleonWeaponPrototypeSelectedMessage>(OnSelected);
    }

    private void OnMapInit(Entity<ChameleonWeaponComponent> ent, ref MapInitEvent args)
    {
        // Apply a disguise that was preset in the prototype / map data.
        if (ent.Comp.Selected != null)
            SetSelectedPrototype(ent.Owner, ent.Comp.Selected, forceUpdate: true, component: ent.Comp);

        UpdateUi(ent.Owner, ent.Comp);
    }

    private void OnUiOpened(Entity<ChameleonWeaponComponent> ent, ref BoundUIOpenedEvent args)
    {
        UpdateUi(ent.Owner, ent.Comp);
    }

    private void OnSelected(Entity<ChameleonWeaponComponent> ent, ref ChameleonWeaponPrototypeSelectedMessage args)
    {
        // The client only sends an id; everything about it is validated here.
        SetSelectedPrototype(ent.Owner, args.SelectedId, component: ent.Comp);
    }

    private void UpdateUi(EntityUid uid, ChameleonWeaponComponent component)
    {
        UI.SetUiState(uid, ChameleonWeaponUiKey.Key, new ChameleonWeaponBoundUserInterfaceState(component.Selected));
    }

    public override bool SetSelectedPrototype(EntityUid uid,
        string? protoId,
        bool forceUpdate = false,
        bool validate = true,
        ChameleonWeaponComponent? component = null)
    {
        if (!Resolve(uid, ref component, false))
            return false;

        // check that wasn't already selected
        // forceUpdate on component init ignores this check
        if (component.Selected == protoId && !forceUpdate)
            return false;

        // make sure that it is a valid change
        if (string.IsNullOrEmpty(protoId) || !ProtoMan.TryIndex(protoId, out EntityPrototype? proto))
            return false;

        if (validate && !IsValidTarget(proto))
            return false;

        component.Selected = protoId;

        UpdateVisuals(uid, component);
        UpdateUi(uid, component);
        Dirty(uid, component);
        return true;
    }
}
