// iss14: Cybersun MIMIC-5
using Content.Client.Clothing.UI;
using Content.Shared.Weapons.Melee.Chameleon;
using JetBrains.Annotations;
using Robust.Client.UserInterface;

namespace Content.Client.Weapons.Melee.Chameleon;

/// <summary>
///     Disguise picker for the chameleon weapon. Reuses the chameleon clothing <see cref="ChameleonMenu"/>
///     (search bar + sprite grid) so it looks and feels identical; only the list of prototypes differs.
/// </summary>
[UsedImplicitly]
public sealed partial class ChameleonWeaponBoundUserInterface : BoundUserInterface
{
    private readonly ChameleonWeaponSystem _chameleon;

    [ViewVariables]
    private ChameleonMenu? _menu;

    public ChameleonWeaponBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
        _chameleon = EntMan.System<ChameleonWeaponSystem>();
    }

    protected override void Open()
    {
        base.Open();

        _menu = this.CreateWindow<ChameleonMenu>();
        _menu.Title = Loc.GetString("chameleon-weapon-ui-window-name");
        _menu.SetSearchPlaceholder(Loc.GetString("chameleon-weapon-ui-search-placeholder"));
        _menu.OnIdSelected += OnIdSelected;
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is not ChameleonWeaponBoundUserInterfaceState st)
            return;

        _menu?.UpdateState(_chameleon.GetValidTargets(), st.SelectedId);
    }

    private void OnIdSelected(string selectedId)
    {
        // Only the id travels to the server; it decides whether the disguise is valid.
        SendMessage(new ChameleonWeaponPrototypeSelectedMessage(selectedId));
    }
}
