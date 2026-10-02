// iss14: Economy
using System.Linq;
using Content.Client.UserInterface.Controls;
using Content.Shared.Cargo.Prototypes;
using Content.Shared.Economy;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Client.Cargo.UI;

/// <summary>
/// Editor for the per-round private-order blocklist (<see cref="CargoPrivateOrderPolicyComponent"/>). Reads the
/// policy straight from the networked station component; edits go through the cargo console BUI.
/// </summary>
public sealed class CargoPrivatePolicyWindow : FancyWindow
{
    public event Action<string, bool>? OnSetBlocked;
    public event Action? OnToggle;

    private readonly IEntityManager _entMan;
    private readonly IPrototypeManager _proto;

    private readonly Button _toggle;
    private readonly Label _status;
    private readonly LineEdit _search;
    private readonly BoxContainer _list;

    private EntityUid? _station;
    private readonly Dictionary<string, CheckBox> _checks = new();
    private bool _updating;

    public CargoPrivatePolicyWindow(IEntityManager entMan, IPrototypeManager proto)
    {
        _entMan = entMan;
        _proto = proto;

        Title = Loc.GetString("cargo-private-policy-title");
        MinSize = new System.Numerics.Vector2(420, 400);
        SetSize = new System.Numerics.Vector2(480, 560);
        Resizable = true;

        var root = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, Margin = new Thickness(8), VerticalExpand = true };
        root.AddChild(new Label { Text = Loc.GetString("cargo-private-policy-hint"), StyleClasses = { "LabelSubText" } });

        var header = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
        _status = new Label { HorizontalExpand = true };
        _toggle = new Button();
        _toggle.OnPressed += _ => OnToggle?.Invoke();
        header.AddChild(_status);
        header.AddChild(_toggle);
        root.AddChild(header);

        _search = new LineEdit { PlaceHolder = Loc.GetString("cargo-private-policy-search") };
        _search.OnTextChanged += _ => Rebuild();
        root.AddChild(_search);

        var scroll = new ScrollContainer { VerticalExpand = true, HScrollEnabled = false, Margin = new Thickness(0, 6, 0, 0) };
        _list = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, HorizontalExpand = true };
        scroll.AddChild(_list);
        root.AddChild(scroll);

        AddChild(root);
    }

    public void SetStation(EntityUid station)
    {
        _station = station;
        Rebuild();
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        // Policy is a networked component; just mirror its current values.
        Refresh();
    }

    private CargoPrivateOrderPolicyComponent? Policy
        => _station is { } station && _entMan.TryGetComponent<CargoPrivateOrderPolicyComponent>(station, out var policy) ? policy : null;

    private void Rebuild()
    {
        _list.RemoveAllChildren();
        _checks.Clear();

        var filter = _search.Text.Trim();
        var products = _proto.EnumeratePrototypes<CargoProductPrototype>()
            .Where(p => !p.Abstract)
            .Select(p => (Product: p, Name: p.Name, Category: Loc.GetString(p.Category)))
            .Where(p => filter.Length == 0 || p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || p.Category.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string? category = null;
        foreach (var (product, name, cat) in products)
        {
            if (cat != category)
            {
                category = cat;
                _list.AddChild(new Label { Text = cat, StyleClasses = { "LabelHeading" }, Margin = new Thickness(0, 6, 0, 0) });
            }

            var check = new CheckBox { Text = name };
            var id = product.ID;
            check.OnToggled += a =>
            {
                if (!_updating)
                    OnSetBlocked?.Invoke(id, a.Pressed);
            };
            _list.AddChild(check);
            _checks[product.ID] = check;
        }

        Refresh();
    }

    private void Refresh()
    {
        var policy = Policy;
        _updating = true;

        if (policy == null)
        {
            _status.Text = Loc.GetString("cargo-private-policy-no-station");
            _toggle.Text = Loc.GetString("cargo-private-policy-enable");
            _updating = false;
            return;
        }

        _status.Text = Loc.GetString(policy.BlocklistEnabled ? "cargo-private-policy-status-on" : "cargo-private-policy-status-off",
            ("count", policy.Blocked.Count));
        _toggle.Text = Loc.GetString(policy.BlocklistEnabled ? "cargo-private-policy-disable" : "cargo-private-policy-enable");

        foreach (var (id, check) in _checks)
        {
            var blocked = policy.Blocked.Contains(id);
            if (check.Pressed != blocked)
                check.Pressed = blocked;
        }

        _updating = false;
    }
}
