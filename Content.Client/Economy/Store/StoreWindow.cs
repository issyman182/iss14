// iss14: Economy
using System.Linq;
using Content.Client.UserInterface.Controls;
using Content.Shared.Economy.Store;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Prototypes;

namespace Content.Client.Economy.Store;

/// <summary>Lobby store: buy titles and loadout unlocks with the persistent balance, pick the worn title.</summary>
public sealed partial class StoreWindow : FancyWindow
{
    [Dependency] private ClientStoreManager _store = default!;
    [Dependency] private IPrototypeManager _proto = default!;

    private readonly Label _balance;
    private readonly Button _noTitle;
    private readonly Label _status;
    private readonly BoxContainer _list;

    public StoreWindow()
    {
        IoCManager.InjectDependencies(this);

        Title = Loc.GetString("store-window-title");
        MinSize = new System.Numerics.Vector2(460, 380);
        SetSize = new System.Numerics.Vector2(520, 560);
        Resizable = true;

        var root = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, Margin = new Thickness(8), VerticalExpand = true };

        var header = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        _balance = new Label { StyleClasses = { "LabelHeading" }, HorizontalExpand = true, VerticalAlignment = VAlignment.Center };
        _noTitle = new Button { Text = Loc.GetString("store-no-title"), ToolTip = Loc.GetString("store-no-title-tooltip") };
        _noTitle.OnPressed += _ => _store.SetTitle(null);
        header.AddChild(_balance);
        header.AddChild(_noTitle);
        root.AddChild(header);
        root.AddChild(new Label { Text = Loc.GetString("store-hint"), StyleClasses = { "LabelSubText" } });

        var scroll = new ScrollContainer { VerticalExpand = true, HScrollEnabled = false, Margin = new Thickness(0, 6, 0, 0) };
        _list = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, HorizontalExpand = true };
        scroll.AddChild(_list);
        root.AddChild(scroll);

        _status = new Label { Margin = new Thickness(0, 6, 0, 0) };
        root.AddChild(_status);

        // FancyWindow: content goes below the title bar, not over it.
        ContentsContainer.AddChild(root);

        _store.Updated += Rebuild;
        OnClose += () => _store.Updated -= Rebuild;
        Rebuild();
        _store.Request();
    }

    private void Rebuild()
    {
        _balance.Text = Loc.GetString("store-balance", ("balance", _store.Balance));
        _noTitle.Disabled = _store.Title == null;
        _status.Text = _store.Status;
        _list.RemoveAllChildren();

        var items = _proto.EnumeratePrototypes<StoreItemPrototype>()
            .Where(i => !i.Hidden || _store.Owned.Contains(i.ID))
            .Select(i => (Item: i, Category: Loc.GetString(i.Category), Name: Loc.GetString(i.Name)))
            .OrderBy(i => i.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Item.Price)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (items.Count == 0)
        {
            _list.AddChild(new Label { Text = Loc.GetString("store-empty"), StyleClasses = { "LabelSubText" } });
            return;
        }

        string? category = null;
        foreach (var (item, cat, name) in items)
        {
            if (cat != category)
            {
                category = cat;
                _list.AddChild(new Label { Text = cat, StyleClasses = { "LabelHeading" }, Margin = new Thickness(0, 8, 0, 2) });
            }

            var owned = _store.Owned.Contains(item.ID);
            var worn = owned && item.Kind == StoreItemKind.Title && _store.Title == item.ID;

            var row = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            var text = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, HorizontalExpand = true };
            var label = item.Kind == StoreItemKind.Title && !string.IsNullOrEmpty(item.TitleText)
                ? Loc.GetString("store-item-title", ("name", name), ("title", item.TitleText))
                : name;
            text.AddChild(new Label { Text = label });
            if (item.Description != null)
                text.AddChild(new Label { Text = Loc.GetString(item.Description), StyleClasses = { "LabelSubText" } });

            var requirementMissing = false;
            if (!owned && item.RequiresUnlock is { } required)
            {
                requirementMissing = !_store.Owned.Contains(required);
                var reqName = _proto.TryIndex(required, out var reqProto) ? Loc.GetString(reqProto.Name) : required.Id;
                text.AddChild(new Label
                {
                    Text = Loc.GetString("store-requires-item", ("item", reqName)),
                    StyleClasses = { "LabelSubText" },
                    FontColorOverride = requirementMissing ? Color.FromHex("#d0a04a") : null,
                });
            }
            row.AddChild(text);

            if (!owned)
            {
                var buy = new Button
                {
                    Text = Loc.GetString("store-buy", ("price", item.Price)),
                    Disabled = _store.Balance < item.Price || requirementMissing,
                    MinWidth = 110,
                };
                var id = item.ID;
                buy.OnPressed += _ => _store.Buy(id);
                row.AddChild(buy);
            }
            else if (item.Kind == StoreItemKind.Title)
            {
                var wear = new Button
                {
                    Text = Loc.GetString(worn ? "store-worn" : "store-wear"),
                    Disabled = worn,
                    MinWidth = 110,
                };
                var id = item.ID;
                wear.OnPressed += _ => _store.SetTitle(id);
                row.AddChild(wear);
            }
            else
            {
                row.AddChild(new Label { Text = Loc.GetString("store-owned"), MinWidth = 110, Align = Label.AlignMode.Center });
            }

            _list.AddChild(row);
        }
    }
}
