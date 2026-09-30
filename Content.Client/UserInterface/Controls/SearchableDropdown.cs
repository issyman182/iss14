using System.Linq;
using System.Numerics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Maths;

namespace Content.Client.UserInterface.Controls;

/// <summary>
/// A dropdown you can type into: clicking opens a popup with a search box and a filtered list of
/// items. Behaves like an <see cref="OptionButton"/> but supports searching large lists.
/// </summary>
public sealed class SearchableDropdown : Button
{
    private readonly DropdownPopup _popup;
    private readonly LineEdit _search;
    private readonly BoxContainer _list;

    private List<string> _items = new();

    public string? Selected { get; private set; }

    /// <summary>Fired when the user picks an item (not when set programmatically).</summary>
    public event Action<string>? OnItemSelected;

    public SearchableDropdown()
    {
        OnPressed += _ => Toggle();

        _popup = UserInterfaceManager.CreatePopup<DropdownPopup>();

        var panel = new PanelContainer();
        panel.StyleClasses.Add("BackgroundDark");

        var vbox = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            MinSize = new Vector2(240, 0),
        };

        _search = new LineEdit { PlaceHolder = Loc.GetString("searchable-dropdown-search") };
        _search.OnTextChanged += _ => Refilter();

        var scroll = new ScrollContainer { MinSize = new Vector2(240, 280), HScrollEnabled = false };
        _list = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, HorizontalExpand = true };
        scroll.AddChild(_list);

        vbox.AddChild(_search);
        vbox.AddChild(scroll);
        panel.AddChild(vbox);
        _popup.AddChild(panel);

        SetSelected(null);
    }

    public void SetItems(IEnumerable<string> items)
    {
        _items = items.ToList();
        Refilter();
    }

    /// <summary>Sets the current value without firing <see cref="OnItemSelected"/>.</summary>
    public void SetSelected(string? value)
    {
        Selected = string.IsNullOrEmpty(value) ? null : value;
        Text = Selected ?? Loc.GetString("searchable-dropdown-none");
    }

    private void Toggle()
    {
        if (_popup.Visible)
        {
            _popup.Close();
            return;
        }

        _search.Text = string.Empty;
        Refilter();

        var box = UIBox2.FromDimensions(
            new Vector2(GlobalPosition.X, GlobalPosition.Y + Height),
            new Vector2(MathF.Max(Width, 240), 320));
        _popup.Open(box);
        _search.GrabKeyboardFocus();
    }

    private void Refilter()
    {
        _list.RemoveAllChildren();

        var filter = _search.Text;
        foreach (var item in _items)
        {
            if (filter.Length != 0 && !item.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            var captured = item;
            var btn = new Button { Text = item, HorizontalExpand = true };
            btn.OnPressed += _ =>
            {
                SetSelected(captured);
                _popup.Close();
                OnItemSelected?.Invoke(captured);
            };
            _list.AddChild(btn);
        }
    }

    // iss14 fix: the popup lives in the modal root, not under this control, so it must be torn down explicitly.
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            _popup.Close();
            _popup.Dispose();
        }
    }

    // Must be a content type for the sandbox; CreatePopup rejects engine types like Popup itself.
    private sealed class DropdownPopup : Popup
    {
    }
}
