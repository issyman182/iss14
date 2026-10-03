// iss14: Economy
using System.Linq;
using Content.Server.Administration.Logs;
using Content.Server.Cargo.Systems;
using Content.Server.Stack;
using Content.Server.Station.Systems;
using Content.Shared.Access.Systems;
using Content.Shared.CCVar;
using Content.Shared.Database;
using Content.Shared.Economy;
using Content.Shared.Examine;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Item;
using Content.Shared.Nutrition.Components;
using Content.Shared.Popups;
using Content.Shared.Stacks;
using Content.Shared.Tag;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.Prototypes;

namespace Content.Server.Economy;

/// <summary>
/// Counter cash register (<see cref="CashRegisterComponent"/>). Staff put goods on a draft bill — from the price
/// list of their job's catalog, from what the register detects lying around it, or by scanning the item on the
/// register — and write the bill. Customers click the register to see and pay it with the ID they carry, swipe
/// their ID/PDA on it, or hand over cash. The sale is booked to the register's department account except for
/// <c>economy.service_commission_percent</c>, which goes to the cashier.
/// </summary>
public sealed partial class CashRegisterSystem : EntitySystem
{
    [Dependency] private EconomySystem _economy = default!;
    [Dependency] private PricingSystem _pricing = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private SharedIdCardSystem _idCard = default!;
    [Dependency] private AccessReaderSystem _accessReader = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private StackSystem _stack = default!;
    [Dependency] private TagSystem _tag = default!;
    [Dependency] private ServerStationSystem _station = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IAdminLogManager _adminLog = default!;
    [Dependency] private AppearanceSystem _appearance = default!;
    [Dependency] private IComponentFactory _factory = default!;

    private readonly Dictionary<EntityUid, string> _messages = new();

    /// <summary>Price list per catalog id (prototype, name, default price), built lazily.</summary>
    private readonly Dictionary<string, List<CatalogEntry>> _catalogCache = new();

    private sealed record CatalogEntry(EntProtoId Prototype, string Name, int DefaultPrice);

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<CashRegisterComponent, ActivateInWorldEvent>(OnActivate);
        SubscribeLocalEvent<CashRegisterComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<CashRegisterComponent, CashRegisterRefreshMessage>((e, c, a) => UpdateUi((e, c), a.Actor));
        SubscribeLocalEvent<CashRegisterComponent, CashRegisterAdjustDraftMessage>(OnAdjustDraft);
        SubscribeLocalEvent<CashRegisterComponent, CashRegisterClearDraftMessage>(OnClearDraft);
        SubscribeLocalEvent<CashRegisterComponent, CashRegisterCreateBillMessage>(OnCreateBill);
        SubscribeLocalEvent<CashRegisterComponent, CashRegisterCancelBillMessage>(OnCancelBill);
        SubscribeLocalEvent<CashRegisterComponent, CashRegisterSetPriceMessage>(OnSetPrice);
        SubscribeLocalEvent<CashRegisterComponent, CashRegisterPayMessage>(OnPayMessage);
        SubscribeLocalEvent<CashRegisterComponent, InteractUsingEvent>(OnInteractUsing);
        SubscribeLocalEvent<CashRegisterComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(_ => _catalogCache.Clear());

        _cfg.OnValueChanged(CCVars.EconomyServicePriceMultiplier, _ => _catalogCache.Clear());
    }

    #region Opening

    private bool IsStaff(Entity<CashRegisterComponent> ent, EntityUid user) => _accessReader.IsAllowed(user, ent.Owner);

    /// <summary>Left click: staff get the cashier UI, everyone else the customer view.</summary>
    private void OnActivate(Entity<CashRegisterComponent> ent, ref ActivateInWorldEvent args)
    {
        if (args.Handled)
            return;

        args.Handled = true;
        var key = IsStaff(ent, args.User) ? CashRegisterUiKey.Key : CashRegisterUiKey.Customer;
        _ui.OpenUi(ent.Owner, key, args.User);
    }

    private void OnUiOpened(Entity<CashRegisterComponent> ent, ref BoundUIOpenedEvent args)
    {
        if (args.UiKey is CashRegisterUiKey.Key && !IsStaff(ent, args.Actor))
        {
            _ui.CloseUi(ent.Owner, CashRegisterUiKey.Key, args.Actor);
            return;
        }

        UpdateUi(ent, args.Actor);
    }

    #endregion

    #region Catalogs

    /// <summary>Catalog prototypes of the register, ordered.</summary>
    private List<CashRegisterCatalogPrototype> GetCatalogs(Entity<CashRegisterComponent> ent)
    {
        var result = new List<CashRegisterCatalogPrototype>();
        foreach (var id in ent.Comp.Catalogs)
        {
            if (ProtoMan.TryIndex(id, out var catalog))
                result.Add(catalog);
        }

        result.Sort((a, b) => a.Priority != b.Priority ? a.Priority.CompareTo(b.Priority) : string.CompareOrdinal(a.ID, b.ID));
        return result;
    }

    /// <summary>
    /// Catalogs the given cashier sells from: those matching one of their access levels, or all of the register's
    /// when none match (heads, Captain, or a register without catalogs).
    /// </summary>
    public List<CashRegisterCatalogPrototype> GetCatalogsFor(Entity<CashRegisterComponent> ent, EntityUid? user)
    {
        var all = GetCatalogs(ent);
        if (user == null || all.Count == 0)
            return all;

        var tags = _accessReader.FindAccessTags(user.Value);
        var matching = all.Where(c => c.Access.Count > 0 && c.Access.Any(tags.Contains)).ToList();
        return matching.Count > 0 ? matching : all;
    }

    private bool HasComponentNamed(EntityUid uid, string name)
        => _factory.TryGetRegistration(name, out var reg) && HasComp(uid, reg.Type);

    private bool HasPositiveFilter(CashRegisterCatalogPrototype catalog)
        => catalog.Components.Count > 0 || catalog.Tags.Count > 0 || catalog.Edible != null;

    /// <summary>Whether a live item belongs to the catalog.</summary>
    public bool Matches(CashRegisterCatalogPrototype catalog, EntityUid uid)
    {
        foreach (var blocked in catalog.BlacklistComponents)
        {
            if (HasComponentNamed(uid, blocked))
                return false;
        }

        if (!HasPositiveFilter(catalog))
            return true;

        foreach (var name in catalog.Components)
        {
            if (HasComponentNamed(uid, name))
                return true;
        }

        if (catalog.Tags.Count > 0 && _tag.HasAnyTag(uid, catalog.Tags.ToHashSet()))
            return true;

        if (catalog.Edible is { } edible && TryComp<EdibleComponent>(uid, out var edibleComp)
            && edibleComp.Edible == edible && (catalog.EdibleSolution == null || edibleComp.Solution == catalog.EdibleSolution))
            return true;

        return false;
    }

    /// <summary>Whether a prototype belongs to the catalog.</summary>
    public bool Matches(CashRegisterCatalogPrototype catalog, EntityPrototype proto)
    {
        foreach (var blocked in catalog.BlacklistComponents)
        {
            if (proto.Components.ContainsKey(blocked))
                return false;
        }

        if (!HasPositiveFilter(catalog))
            return true;

        foreach (var name in catalog.Components)
        {
            if (proto.Components.ContainsKey(name))
                return true;
        }

        if (catalog.Tags.Count > 0 && proto.TryComp<TagComponent>(out var tagComp, _factory)
            && catalog.Tags.Any(tagComp.Tags.Contains))
            return true;

        if (catalog.Edible is { } edible && proto.TryComp<EdibleComponent>(out var edibleComp, _factory)
            && edibleComp.Edible == edible && (catalog.EdibleSolution == null || edibleComp.Solution == catalog.EdibleSolution))
            return true;

        return false;
    }

    /// <summary>Whether the register sells this item for the given cashier at all.</summary>
    public bool IsSellable(Entity<CashRegisterComponent> ent, EntityUid? cashier, EntityUid item)
    {
        if (!HasComp<ItemComponent>(item) || IsCash(item) || _idCard.TryFindIdCard(item, out _))
            return false;

        var catalogs = GetCatalogsFor(ent, cashier);
        if (catalogs.Count == 0)
            return true;

        return catalogs.Any(c => Matches(c, item));
    }

    private List<CatalogEntry> GetCatalogEntries(CashRegisterCatalogPrototype catalog)
    {
        if (_catalogCache.TryGetValue(catalog.ID, out var cached))
            return cached;

        var multiplier = _cfg.GetCVar(CCVars.EconomyServicePriceMultiplier);
        var entries = new List<CatalogEntry>();

        // A catalog without a filter would be "every item in the game"; such catalogs only list what lies around.
        if (HasPositiveFilter(catalog))
        {
            foreach (var proto in ProtoMan.EnumeratePrototypes<EntityPrototype>())
            {
                if (proto.Abstract || proto.HideSpawnMenu || !proto.Components.ContainsKey("Item"))
                    continue;

                if (!Matches(catalog, proto))
                    continue;

                var price = EconomyPricing.VendPrice(_pricing.GetEstimatedPrice(proto), multiplier);
                if (price <= 0)
                    continue;

                entries.Add(new CatalogEntry(proto.ID, proto.Name, price));
            }
        }

        entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        _catalogCache[catalog.ID] = entries;
        return entries;
    }

    #endregion

    #region Pricing & items

    /// <summary>Default price of an item: appraised value × multiplier, at least 1 (0 if it has no value).</summary>
    public int GetDefaultPrice(EntityUid item)
    {
        var appraised = _pricing.GetPrice(item);
        return EconomyPricing.VendPrice(appraised, _cfg.GetCVar(CCVars.EconomyServicePriceMultiplier));
    }

    public int GetDefaultPrice(EntityPrototype proto)
        => EconomyPricing.VendPrice(_pricing.GetEstimatedPrice(proto), _cfg.GetCVar(CCVars.EconomyServicePriceMultiplier));

    /// <summary>Price of an item for the register: the override, else the default.</summary>
    public int GetPrice(Entity<CashRegisterComponent> ent, EntityUid item)
    {
        if (MetaData(item).EntityPrototype is { } proto && ent.Comp.Prices.TryGetValue(proto.ID, out var price))
            return price;

        return GetDefaultPrice(item);
    }

    /// <summary>
    /// The cashier's price list: every item of their catalogs, plus the sellable items lying around the register
    /// (with counts), plus prototypes with a price override. Catalog entries carry the catalog's name.
    /// </summary>
    public List<CashRegisterItem> GetItems(Entity<CashRegisterComponent> ent, EntityUid? cashier)
    {
        var catalogs = GetCatalogsFor(ent, cashier);
        var multiplier = _cfg.GetCVar(CCVars.EconomyServicePriceMultiplier);

        // prototype -> (name, catalog name, default price)
        var known = new Dictionary<string, (string Name, string Catalog, int DefaultPrice)>();
        foreach (var catalog in catalogs)
        {
            var catalogName = Loc.GetString(catalog.Name);
            foreach (var entry in GetCatalogEntries(catalog))
            {
                known.TryAdd(entry.Prototype.Id, (entry.Name, catalogName, entry.DefaultPrice));
            }
        }

        // What lies around the register.
        var counts = new Dictionary<string, int>();
        var xform = Transform(ent);
        foreach (var uid in _lookup.GetEntitiesInRange(xform.Coordinates, ent.Comp.Range, LookupFlags.Dynamic | LookupFlags.Sundries))
        {
            if (uid == ent.Owner || Transform(uid).Anchored)
                continue;

            if (MetaData(uid).EntityPrototype is not { } proto)
                continue;

            var overridden = ent.Comp.Prices.ContainsKey(proto.ID);
            if (!overridden && !IsSellable(ent, cashier, uid))
                continue;

            var count = TryComp<StackComponent>(uid, out var stack) ? stack.Count : 1;
            counts[proto.ID] = counts.GetValueOrDefault(proto.ID) + count;

            if (!known.ContainsKey(proto.ID))
            {
                var defaultPrice = GetDefaultPrice(uid);
                if (defaultPrice <= 0 && !overridden)
                {
                    counts.Remove(proto.ID);
                    continue;
                }

                var catalogName = catalogs.FirstOrDefault(c => Matches(c, uid)) is { } match
                    ? Loc.GetString(match.Name)
                    : Loc.GetString("cash-register-catalog-nearby");
                known[proto.ID] = (Name(uid), catalogName, defaultPrice);
            }
        }

        // Overridden prototypes that are in no catalog and not on the counter, so prices can still be managed.
        foreach (var (proto, _) in ent.Comp.Prices)
        {
            if (known.ContainsKey(proto.Id) || !ProtoMan.TryIndex(proto, out var protoDef))
                continue;

            known[proto.Id] = (protoDef.Name, Loc.GetString("cash-register-catalog-custom"),
                EconomyPricing.VendPrice(_pricing.GetEstimatedPrice(protoDef), multiplier));
        }

        var result = new List<CashRegisterItem>(known.Count);
        foreach (var (proto, info) in known)
        {
            var overridden = ent.Comp.Prices.TryGetValue(proto, out var price);
            if (!overridden)
                price = info.DefaultPrice;

            result.Add(new CashRegisterItem(proto, info.Name, counts.GetValueOrDefault(proto), price, info.DefaultPrice, overridden, info.Catalog));
        }

        result.Sort((a, b) =>
        {
            var byCatalog = string.Compare(a.Catalog, b.Catalog, StringComparison.OrdinalIgnoreCase);
            return byCatalog != 0 ? byCatalog : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        return result;
    }

    #endregion

    #region UI

    private void UpdateUi(Entity<CashRegisterComponent> ent, EntityUid? actor)
    {
        var station = _station.GetOwningStation(ent);
        var department = _economy.Describe(EconomyParty.Department(station ?? EntityUid.Invalid, ent.Comp.Account));
        var message = _messages.GetValueOrDefault(ent.Owner, string.Empty);

        if (_ui.IsUiOpen(ent.Owner, CashRegisterUiKey.Key))
        {
            var balance = station != null ? (int) _economy.GetBalance(EconomyParty.Department(station.Value, ent.Comp.Account)) : 0;
            // The price list depends on the cashier's job; use the acting cashier, else whoever has it open.
            EntityUid? cashier = actor;
            if (cashier == null || !_ui.IsUiOpen(ent.Owner, CashRegisterUiKey.Key, cashier.Value))
            {
                var first = _ui.GetActors(ent.Owner, CashRegisterUiKey.Key).FirstOrDefault();
                cashier = first == default ? null : first;
            }

            var state = new CashRegisterBuiState(
                GetItems(ent, cashier),
                ent.Comp.Draft,
                ent.Comp.Bill,
                department,
                balance,
                _cfg.GetCVar(CCVars.EconomyServiceCommissionPercent),
                message);

            _ui.SetUiState(ent.Owner, CashRegisterUiKey.Key, state);
        }

        if (_ui.IsUiOpen(ent.Owner, CashRegisterUiKey.Customer))
            _ui.SetUiState(ent.Owner, CashRegisterUiKey.Customer, new CashRegisterCustomerBuiState(ent.Comp.Bill, department, message));
    }

    private void SetMessage(Entity<CashRegisterComponent> ent, EntityUid? actor, string message)
    {
        _messages[ent.Owner] = message;
        UpdateUi(ent, actor);
    }

    private void AddToDraft(Entity<CashRegisterComponent> ent, EntProtoId proto, string name, int count, int price)
    {
        var line = ent.Comp.Draft.Lines.FirstOrDefault(l => l.Prototype == proto);
        if (line == null)
        {
            if (count <= 0)
                return;

            ent.Comp.Draft.Lines.Add(new CashRegisterLine { Prototype = proto, Name = name, Count = count, Price = price });
        }
        else
        {
            line.Count += count;
            line.Price = price;
            if (line.Count <= 0)
                ent.Comp.Draft.Lines.Remove(line);
        }

        Dirty(ent);
    }

    private void OnAdjustDraft(Entity<CashRegisterComponent> ent, ref CashRegisterAdjustDraftMessage args)
    {
        if (!IsStaff(ent, args.Actor) || args.Count == 0 || Math.Abs(args.Count) > 1000)
            return;

        if (!ProtoMan.TryIndex<EntityPrototype>(args.Prototype, out var proto))
            return;

        // Only things the cashier may sell: their catalogs, the counter, or an explicit price.
        // Copy out of the ref parameter: it can't be captured by the lambda below.
        var protoId = args.Prototype;
        var items = GetItems(ent, args.Actor);
        var item = items.FirstOrDefault(i => i.Prototype == protoId);
        if (item == null)
        {
            SetMessage(ent, args.Actor, Loc.GetString("cash-register-not-sellable", ("item", proto.Name)));
            return;
        }

        AddToDraft(ent, proto.ID, item.Name, args.Count, item.Price);
        UpdateUi(ent, args.Actor);
    }

    private void OnClearDraft(Entity<CashRegisterComponent> ent, ref CashRegisterClearDraftMessage args)
    {
        if (!IsStaff(ent, args.Actor))
            return;

        ent.Comp.Draft = new CashRegisterBill();
        Dirty(ent);
        UpdateUi(ent, args.Actor);
    }

    private void OnCreateBill(Entity<CashRegisterComponent> ent, ref CashRegisterCreateBillMessage args)
    {
        if (!IsStaff(ent, args.Actor))
            return;

        if (ent.Comp.Bill != null)
        {
            SetMessage(ent, args.Actor, Loc.GetString("cash-register-bill-already-open"));
            return;
        }

        var bill = ent.Comp.Draft;
        bill.Lines.RemoveAll(l => l.Count <= 0);
        if (bill.Lines.Count == 0 || bill.Total <= 0)
        {
            SetMessage(ent, args.Actor, Loc.GetString("cash-register-empty-bill"));
            return;
        }

        bill.Cashier = Name(args.Actor);
        ent.Comp.Bill = bill;
        ent.Comp.Draft = new CashRegisterBill();
        ent.Comp.CashierUserId = _players.TryGetSessionByEntity(args.Actor, out var session) ? session.UserId : null;
        Dirty(ent);
        _appearance.SetData(ent, CashRegisterVisuals.HasBill, true);
        SetMessage(ent, args.Actor, Loc.GetString("cash-register-bill-created", ("total", bill.Total)));
        _popup.PopupEntity(Loc.GetString("cash-register-bill-popup", ("total", bill.Total)), ent, PopupType.Medium);
    }

    private void OnCancelBill(Entity<CashRegisterComponent> ent, ref CashRegisterCancelBillMessage args)
    {
        if (!IsStaff(ent, args.Actor))
            return;

        ent.Comp.Bill = null;
        ent.Comp.CashierUserId = null;
        Dirty(ent);
        _appearance.SetData(ent, CashRegisterVisuals.HasBill, false);
        SetMessage(ent, args.Actor, Loc.GetString("cash-register-bill-cancelled"));
    }

    private void OnSetPrice(Entity<CashRegisterComponent> ent, ref CashRegisterSetPriceMessage args)
    {
        if (!IsStaff(ent, args.Actor) || !ProtoMan.HasIndex<EntityPrototype>(args.Prototype))
            return;

        if (args.Price is { } price && price >= 0)
            ent.Comp.Prices[args.Prototype] = Math.Min(price, 1_000_000);
        else
            ent.Comp.Prices.Remove(args.Prototype);

        _adminLog.Add(LogType.Economy, LogImpact.Low,
            $"{ToPrettyString(args.Actor):player} set cash register {ToPrettyString(ent)} price of {args.Prototype} to {(args.Price?.ToString() ?? "default")}");
        UpdateUi(ent, args.Actor);
    }

    #endregion

    #region Scanning & payment

    private void OnInteractUsing(Entity<CashRegisterComponent> ent, ref InteractUsingEvent args)
    {
        if (args.Handled)
            return;

        // Payment attempts: cash, or an ID card / PDA with an ID in it.
        if (IsCash(args.Used))
        {
            args.Handled = true;
            if (ent.Comp.Bill == null)
                Deny(ent, args.User, Loc.GetString("cash-register-no-bill"));
            else
                PayWithCash(ent, args.User, args.Used);
            return;
        }

        if (_idCard.TryFindIdCard(args.Used, out var card))
        {
            args.Handled = true;
            if (ent.Comp.Bill == null)
                Deny(ent, args.User, Loc.GetString("cash-register-no-bill"));
            else
                PayWithCard(ent, args.User, card);
            return;
        }

        // Staff scanning goods: adds them to the draft bill, like a supermarket scanner.
        if (!IsStaff(ent, args.User))
            return;

        args.Handled = true;
        Scan(ent, args.User, args.Used);
    }

    private void Scan(Entity<CashRegisterComponent> ent, EntityUid cashier, EntityUid item)
    {
        if (MetaData(item).EntityPrototype is not { } proto)
            return;

        var overridden = ent.Comp.Prices.ContainsKey(proto.ID);
        if (!overridden && !IsSellable(ent, cashier, item))
        {
            Deny(ent, cashier, Loc.GetString("cash-register-not-sellable", ("item", Name(item))));
            return;
        }

        var price = GetPrice(ent, item);
        if (price <= 0)
        {
            Deny(ent, cashier, Loc.GetString("cash-register-worthless", ("item", Name(item))));
            return;
        }

        var count = TryComp<StackComponent>(item, out var stack) ? stack.Count : 1;
        AddToDraft(ent, proto.ID, Name(item), count, price);
        _audio.PlayPvs(ent.Comp.ScanSound, ent);
        _popup.PopupEntity(Loc.GetString("cash-register-scanned", ("count", count), ("item", Name(item)), ("price", price), ("total", ent.Comp.Draft.Total)), ent, cashier);
        UpdateUi(ent, cashier);
    }

    private bool IsCash(EntityUid uid)
        => TryComp<StackComponent>(uid, out var stack) && stack.StackTypeId == "Credit" && stack.Count > 0;

    /// <summary>Customer UI: pay with the ID card the customer carries (hand, ID slot or PDA).</summary>
    private void OnPayMessage(Entity<CashRegisterComponent> ent, ref CashRegisterPayMessage args)
    {
        if (ent.Comp.Bill == null)
        {
            Deny(ent, args.Actor, Loc.GetString("cash-register-no-bill"));
            return;
        }

        if (!_idCard.TryFindIdCard(args.Actor, out var card))
        {
            Deny(ent, args.Actor, Loc.GetString("cash-register-no-card"));
            return;
        }

        PayWithCard(ent, args.Actor, card);
    }

    private void PayWithCard(Entity<CashRegisterComponent> ent, EntityUid user, EntityUid card)
    {
        var bill = ent.Comp.Bill!;

        // Fingerprint check (economy.atm_require_fingerprint) happens in here, as everywhere else.
        if (!_economy.TryGetCardPayer(card, user, out var payer, out var error))
        {
            Deny(ent, user, _economy.ErrorText(error));
            return;
        }

        if (_station.GetOwningStation(ent) is not { } station)
        {
            Deny(ent, user, Loc.GetString("cash-register-no-station"));
            return;
        }

        var total = bill.Total;
        if (_economy.GetBalance(payer) < total)
        {
            Deny(ent, user, _economy.ErrorText(EconomyPaymentError.InsufficientFunds));
            return;
        }

        var (commission, departmentShare) = Split(ent, total);
        var reason = $"cash register {ToPrettyString(ent)} sale by {bill.Cashier}: {Describe(bill)}";

        if (!_economy.TryTransfer(payer, EconomyParty.Department(station, ent.Comp.Account), departmentShare, reason, out error))
        {
            Deny(ent, user, _economy.ErrorText(error));
            return;
        }

        if (commission > 0)
        {
            // Cashier offline or unknown: the department keeps it.
            var cashier = ent.Comp.CashierUserId is { } id ? EconomyParty.Player(id) : EconomyParty.Department(station, ent.Comp.Account);
            if (!_economy.TryTransfer(payer, cashier, commission, reason + " (commission)", out _))
                _economy.TryTransfer(payer, EconomyParty.Department(station, ent.Comp.Account), commission, reason + " (commission fallback)", out _);
        }

        Complete(ent, user, Loc.GetString("cash-register-paid-card", ("total", total)));
    }

    private void PayWithCash(Entity<CashRegisterComponent> ent, EntityUid user, EntityUid cash)
    {
        var bill = ent.Comp.Bill!;
        var stack = Comp<StackComponent>(cash);
        var total = bill.Total;

        if (stack.Count < total)
        {
            Deny(ent, user, Loc.GetString("cash-register-not-enough-cash", ("total", total), ("given", stack.Count)));
            return;
        }

        if (_station.GetOwningStation(ent) is not { } station)
        {
            Deny(ent, user, Loc.GetString("cash-register-no-station"));
            return;
        }

        var change = stack.Count - total;
        QueueDel(cash);
        if (change > 0)
        {
            var back = Spawn(ent.Comp.CashPrototype, Transform(ent).Coordinates);
            _stack.SetCount((back, null), change);
            _hands.PickupOrDrop(user, back, animateUser: false, dropNear: true);
        }

        var (commission, departmentShare) = Split(ent, total);
        var reason = $"cash register {ToPrettyString(ent)} cash sale by {bill.Cashier}: {Describe(bill)}";

        // Physical cash is destroyed and turned into account credit.
        _economy.TryTransfer(EconomyParty.External, EconomyParty.Department(station, ent.Comp.Account), departmentShare, reason, out _);
        if (commission > 0)
        {
            if (ent.Comp.CashierUserId is { } id)
                _economy.Pay(EconomyParty.Player(id), commission, reason + " (commission)");
            else
                _economy.TryTransfer(EconomyParty.External, EconomyParty.Department(station, ent.Comp.Account), commission, reason + " (commission fallback)", out _);
        }

        Complete(ent, user, Loc.GetString("cash-register-paid-cash", ("total", total), ("change", change)));
    }

    private (int Commission, int Department) Split(Entity<CashRegisterComponent> ent, int total)
    {
        var percent = Math.Clamp(_cfg.GetCVar(CCVars.EconomyServiceCommissionPercent), 0f, 100f);
        var commission = (int) Math.Floor(total * percent / 100.0);
        return (commission, total - commission);
    }

    private static string Describe(CashRegisterBill bill)
        => string.Join(", ", bill.Lines.Select(l => $"{l.Count}x {l.Name} @ {l.Price}"));

    private void Complete(Entity<CashRegisterComponent> ent, EntityUid user, string message)
    {
        ent.Comp.Bill = null;
        ent.Comp.CashierUserId = null;
        Dirty(ent);
        _appearance.SetData(ent, CashRegisterVisuals.HasBill, false);
        _audio.PlayPvs(ent.Comp.PaySound, ent);
        _popup.PopupEntity(message, ent, user);
        SetMessage(ent, user, message);
    }

    private void Deny(Entity<CashRegisterComponent> ent, EntityUid user, string message)
    {
        _audio.PlayPvs(ent.Comp.DenySound, ent);
        _popup.PopupEntity(message, ent, user, PopupType.SmallCaution);
        SetMessage(ent, user, message);
    }

    private void OnExamined(Entity<CashRegisterComponent> ent, ref ExaminedEvent args)
    {
        if (ent.Comp.Bill is { } bill)
            args.PushMarkup(Loc.GetString("cash-register-examine-bill", ("total", bill.Total), ("cashier", bill.Cashier)));
        else
            args.PushMarkup(Loc.GetString("cash-register-examine-idle"));
    }

    #endregion
}
