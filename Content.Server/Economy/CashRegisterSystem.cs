// iss14: Economy
using System.Linq;
using System.Text.Json;
using Content.Server.Administration.Logs;
using Content.Server.Cargo.Systems;
using Content.Server.Power.EntitySystems;
using Content.Server.Stack;
using Content.Server.Station.Systems;
using Content.Shared.Access.Systems;
using Content.Shared.CCVar;
using Content.Shared.Cargo.Prototypes;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Database;
using Content.Shared.Economy;
using Content.Shared.Examine;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Item;
using Content.Shared.Nutrition.Components;
using Content.Shared.Nutrition.EntitySystems;
using Content.Shared.Popups;
using Content.Shared.Power;
using Content.Shared.Stacks;
using Content.Shared.Tag;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.ContentPack;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Server.Economy;

/// <summary>
/// Counter cash register (<see cref="CashRegisterComponent"/>). One register type for everyone: the cashier's ID
/// decides what they sell (catalogs by access) and which department account gets the money. Prices have three
/// layers: appraised value × multiplier, the global default prices admins set in the money config, and this
/// register's own overrides. Drinks are priced by what is in them, not by the glass.
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
    [Dependency] private PowerReceiverSystem _power = default!;
    [Dependency] private SharedSolutionContainerSystem _solution = default!;
    [Dependency] private ServerStationSystem _station = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IAdminLogManager _adminLog = default!;
    [Dependency] private AppearanceSystem _appearance = default!;
    [Dependency] private IComponentFactory _factory = default!;
    [Dependency] private IResourceManager _res = default!;

    private readonly Dictionary<EntityUid, string> _messages = new();

    /// <summary>Price list per catalog id, built lazily.</summary>
    private readonly Dictionary<string, List<CatalogEntry>> _catalogCache = new();

    /// <summary>Reagent id -> price per unit, derived from the catalogs' single-reagent drinks. Built lazily.</summary>
    private Dictionary<string, double>? _reagentUnitPrices;

    /// <summary>A catalog entry: the prototype shown for it and the appraised default price.</summary>
    public sealed record CatalogEntry(EntProtoId Prototype, string Name, int AppraisedPrice);

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<CashRegisterComponent, ActivateInWorldEvent>(OnActivate);
        SubscribeLocalEvent<CashRegisterComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<CashRegisterComponent, PowerChangedEvent>(OnPowerChanged);
        SubscribeLocalEvent<CashRegisterComponent, CashRegisterRefreshMessage>((e, c, a) => UpdateUi((e, c), a.Actor));
        SubscribeLocalEvent<CashRegisterComponent, CashRegisterAdjustDraftMessage>(OnAdjustDraft);
        SubscribeLocalEvent<CashRegisterComponent, CashRegisterAddManualMessage>(OnAddManual);
        SubscribeLocalEvent<CashRegisterComponent, CashRegisterRemoveLineMessage>(OnRemoveLine);
        SubscribeLocalEvent<CashRegisterComponent, CashRegisterSetAccountMessage>(OnSetAccount);
        SubscribeLocalEvent<CashRegisterComponent, CashRegisterClearDraftMessage>(OnClearDraft);
        SubscribeLocalEvent<CashRegisterComponent, CashRegisterCreateBillMessage>(OnCreateBill);
        SubscribeLocalEvent<CashRegisterComponent, CashRegisterCancelBillMessage>(OnCancelBill);
        SubscribeLocalEvent<CashRegisterComponent, CashRegisterSetPriceMessage>(OnSetPrice);
        SubscribeLocalEvent<CashRegisterComponent, CashRegisterPayMessage>(OnPayMessage);
        // After AnchorableSystem so a wrench moves the register instead of being rung up.
        SubscribeLocalEvent<CashRegisterComponent, InteractUsingEvent>(OnInteractUsing,
            after: new[] { typeof(Content.Shared.Construction.EntitySystems.AnchorableSystem) });
        SubscribeLocalEvent<CashRegisterComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(_ => InvalidateCaches());

        _cfg.OnValueChanged(CCVars.EconomyServicePriceMultiplier, _ => InvalidateCaches());
        LoadGlobalPrices();
    }

    private void InvalidateCaches()
    {
        _catalogCache.Clear();
        _reagentUnitPrices = null;
    }

    #region Opening & power

    private bool IsPowered(Entity<CashRegisterComponent> ent) => _power.IsPowered(ent.Owner);

    /// <summary>Whether this person may use the register as a cashier: they have at least one catalog.</summary>
    public bool IsStaff(Entity<CashRegisterComponent> ent, EntityUid user) => GetCatalogsFor(user).Count > 0;

    /// <summary>Left click: staff get the cashier UI, everyone else the customer view. Nothing without power.</summary>
    private void OnActivate(Entity<CashRegisterComponent> ent, ref ActivateInWorldEvent args)
    {
        if (args.Handled)
            return;

        args.Handled = true;
        if (!IsPowered(ent))
        {
            _popup.PopupEntity(Loc.GetString("cash-register-no-power"), ent, args.User, PopupType.SmallCaution);
            return;
        }

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

        // A fresh draft books to the cashier's own department.
        if (args.UiKey is CashRegisterUiKey.Key && ent.Comp.Draft.Lines.Count == 0)
        {
            var accounts = GetAccountsFor(ent, args.Actor);
            if (accounts.Count > 0 && !accounts.Contains(ent.Comp.Draft.Account))
            {
                ent.Comp.Draft.Account = accounts[0];
                Dirty(ent);
            }
        }

        UpdateUi(ent, args.Actor);
    }

    private void OnPowerChanged(Entity<CashRegisterComponent> ent, ref PowerChangedEvent args)
    {
        if (args.Powered)
            return;

        _ui.CloseUis(ent.Owner);
    }

    #endregion

    #region Catalogs & access

    private List<CashRegisterCatalogPrototype> AllCatalogs()
    {
        var result = ProtoMan.EnumeratePrototypes<CashRegisterCatalogPrototype>().ToList();
        result.Sort((a, b) => a.Priority != b.Priority ? a.Priority.CompareTo(b.Priority) : string.CompareOrdinal(a.ID, b.ID));
        return result;
    }

    /// <summary>
    /// Catalogs the given person sells from: those matching one of their access levels; the Captain gets all.
    /// Empty means they are a customer.
    /// </summary>
    public List<CashRegisterCatalogPrototype> GetCatalogsFor(EntityUid? user)
    {
        var all = AllCatalogs();
        if (user == null)
            return all;

        var tags = _accessReader.FindAccessTags(user.Value);
        if (tags.Contains("Captain"))
            return all;

        return all.Where(c => c.Access.Any(tags.Contains)).ToList();
    }

    /// <summary>Department accounts the cashier may book sales to (their departments; the Captain all).</summary>
    public List<ProtoId<CargoAccountPrototype>> GetAccountsFor(Entity<CashRegisterComponent> ent, EntityUid user)
    {
        var result = new List<ProtoId<CargoAccountPrototype>>();
        foreach (var department in ProtoMan.EnumeratePrototypes<EconomyDepartmentPrototype>().OrderBy(d => d.ID))
        {
            if (_economy.IsDepartmentMember(user, department.ID))
                result.Add(department.ID);
        }

        return result;
    }

    private bool HasComponentNamed(EntityUid uid, string name)
        => _factory.TryGetRegistration(name, out var reg) && HasComp(uid, reg.Type);

    private static bool HasPositiveFilter(CashRegisterCatalogPrototype catalog)
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
    public bool IsSellable(EntityUid cashier, EntityUid item)
    {
        if (!HasComp<ItemComponent>(item) || IsCash(item) || _idCard.TryFindIdCard(item, out _))
            return false;

        return GetCatalogsFor(cashier).Any(c => Matches(c, item));
    }

    /// <summary>An edible prototype whose solution starts empty: an empty glass, mug or plate. Nobody sells those.</summary>
    private bool IsEmptyEdible(EntityPrototype proto)
    {
        if (!proto.TryComp<EdibleComponent>(out var edible, _factory))
            return false;

        return !_solution.TryGetSolution(proto, edible.Solution, out var solution) || solution.Volume <= 0;
    }

    /// <summary>
    /// The catalog's price list. Prototypes with the same name and the same price (colour variants, the different
    /// jugs, ...) are listed once; empty containers are left out.
    /// </summary>
    public List<CatalogEntry> GetCatalogEntries(CashRegisterCatalogPrototype catalog)
    {
        if (_catalogCache.TryGetValue(catalog.ID, out var cached))
            return cached;

        var entries = new List<CatalogEntry>();

        // A catalog without a filter would be "every item in the game"; such catalogs only list what lies around.
        if (HasPositiveFilter(catalog))
        {
            var seen = new HashSet<(string Name, int Price)>();
            foreach (var proto in ProtoMan.EnumeratePrototypes<EntityPrototype>().OrderBy(p => p.ID, StringComparer.Ordinal))
            {
                if (proto.Abstract || proto.HideSpawnMenu || !proto.Components.ContainsKey("Item"))
                    continue;

                if (!Matches(catalog, proto) || IsEmptyEdible(proto))
                    continue;

                var price = GetAppraisedPrice(proto);
                if (price <= 0)
                    continue;

                if (!seen.Add((proto.Name, price)))
                    continue;

                entries.Add(new CatalogEntry(proto.ID, proto.Name, price));
            }
        }

        entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        _catalogCache[catalog.ID] = entries;
        return entries;
    }

    /// <summary>Every catalog entry of every catalog, with its catalog name (for the money config).</summary>
    public IEnumerable<(CashRegisterCatalogPrototype Catalog, CatalogEntry Entry)> AllCatalogEntries()
    {
        foreach (var catalog in AllCatalogs())
        {
            foreach (var entry in GetCatalogEntries(catalog))
                yield return (catalog, entry);
        }
    }

    #endregion

    #region Global default prices (money config)

    private static readonly ResPath GlobalPricesFile = new("/economy/cash_register_prices.json");

    /// <summary>Admin-set default prices (prototype id -> price), used by every register unless it overrides them.</summary>
    private readonly Dictionary<string, int> _globalPrices = new();

    public IReadOnlyDictionary<string, int> GlobalPrices => _globalPrices;

    public bool TryGetGlobalPrice(string prototype, out int price) => _globalPrices.TryGetValue(prototype, out price);

    /// <summary>Sets (or with null clears) the global default price of a prototype.</summary>
    public void SetGlobalPrice(string prototype, int? price)
    {
        if (price is { } p)
            _globalPrices[prototype] = Math.Clamp(p, 0, 1_000_000);
        else if (!_globalPrices.Remove(prototype))
            return;

        _reagentUnitPrices = null;
        SaveGlobalPrices();
    }

    private void LoadGlobalPrices()
    {
        try
        {
            if (!_res.UserData.Exists(GlobalPricesFile))
                return;

            using var reader = _res.UserData.OpenText(GlobalPricesFile);
            var data = JsonSerializer.Deserialize<Dictionary<string, int>>(reader.ReadToEnd());
            _globalPrices.Clear();
            if (data != null)
            {
                foreach (var (k, v) in data)
                    _globalPrices[k] = v;
            }
        }
        catch (Exception e)
        {
            Log.Error($"Failed to read cash register prices: {e}");
        }
    }

    private void SaveGlobalPrices()
    {
        try
        {
            _res.UserData.CreateDir(GlobalPricesFile.Directory);
            using var writer = _res.UserData.OpenWriteText(GlobalPricesFile);
            writer.Write(JsonSerializer.Serialize(_globalPrices, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            Log.Error($"Failed to write cash register prices: {e}");
        }
    }

    #endregion

    #region Pricing

    private float Multiplier => _cfg.GetCVar(CCVars.EconomyServicePriceMultiplier);

    /// <summary>Appraised value × multiplier, at least 1 (0 if it has no value). The bottom price layer.</summary>
    public int GetAppraisedPrice(EntityPrototype proto) => EconomyPricing.VendPrice(_pricing.GetEstimatedPrice(proto), Multiplier);

    /// <summary>The station-wide default price of a prototype: the admin default if set, else the appraised price.</summary>
    public int GetDefaultPrice(EntityPrototype proto)
        => _globalPrices.TryGetValue(proto.ID, out var global) ? global : GetAppraisedPrice(proto);

    /// <summary>The price a register charges per piece of a prototype: its own override, else the default.</summary>
    public int GetPrice(Entity<CashRegisterComponent> ent, EntityPrototype proto)
        => ent.Comp.Prices.TryGetValue(proto.ID, out var price) ? price : GetDefaultPrice(proto);

    private bool IsDrink(EntityUid item, out string solutionName)
    {
        solutionName = string.Empty;
        if (!TryComp<EdibleComponent>(item, out var edible) || edible.Edible != IngestionSystem.Drink)
            return false;

        solutionName = edible.Solution;
        return true;
    }

    /// <summary>
    /// Price per unit of each reagent, learned from the catalogs: a drink that consists of one reagent prices that
    /// reagent (price ÷ units; the biggest serving wins). Reagents no drink teaches fall back to their appraisal price.
    /// </summary>
    private Dictionary<string, double> GetReagentUnitPrices()
    {
        if (_reagentUnitPrices != null)
            return _reagentUnitPrices;

        var best = new Dictionary<string, (double Units, double Price)>();
        foreach (var (_, entry) in AllCatalogEntries())
        {
            if (!ProtoMan.TryIndex(entry.Prototype, out var proto) || !proto.TryComp<EdibleComponent>(out var edible, _factory))
                continue;

            if (!_solution.TryGetSolution(proto, edible.Solution, out var solution) || solution.Contents.Count != 1)
                continue;

            var (reagent, quantity) = solution.Contents[0];
            var units = (double) quantity;
            if (units <= 0)
                continue;

            if (!best.TryGetValue(reagent.Prototype, out var current) || current.Units < units)
                best[reagent.Prototype] = (units, GetDefaultPrice(proto));
        }

        _reagentUnitPrices = best.ToDictionary(kv => kv.Key, kv => kv.Value.Price / kv.Value.Units);
        return _reagentUnitPrices;
    }

    /// <summary>What is in the glass is worth: Σ units × unit price. 0 for an empty container.</summary>
    public int GetDrinkPrice(EntityUid item, string solutionName)
    {
        if (!_solution.TryGetSolution(item, solutionName, out _, out var solution) || solution.Volume <= 0)
            return 0;

        var unitPrices = GetReagentUnitPrices();
        var total = 0.0;
        foreach (var (reagent, quantity) in solution.Contents)
        {
            if (unitPrices.TryGetValue(reagent.Prototype, out var unitPrice))
                total += (double) quantity * unitPrice;
            else if (ProtoMan.TryIndex<ReagentPrototype>(reagent.Prototype, out var reagentProto))
                total += (double) quantity * reagentProto.PricePerUnit * Multiplier;
        }

        return total <= 0 ? 0 : Math.Max(1, (int) Math.Round(total, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// Price of a live item for this register. An explicit price (register or global) always wins; otherwise drinks
    /// are priced by their contents, whatever glass they are in, and everything else by its appraisal.
    /// </summary>
    public int GetPrice(Entity<CashRegisterComponent> ent, EntityUid item)
    {
        var proto = MetaData(item).EntityPrototype;
        if (proto != null && (ent.Comp.Prices.TryGetValue(proto.ID, out var price) || _globalPrices.TryGetValue(proto.ID, out price)))
            return price;

        if (IsDrink(item, out var solutionName))
            return GetDrinkPrice(item, solutionName);

        return EconomyPricing.VendPrice(_pricing.GetPrice(item), Multiplier);
    }

    #endregion

    #region Item list

    /// <summary>
    /// The cashier's price list: every item of their catalogs, plus the sellable items lying around the register
    /// (with counts), plus prototypes with a price override. Entries carry their catalog's name.
    /// </summary>
    public List<CashRegisterItem> GetItems(Entity<CashRegisterComponent> ent, EntityUid? cashier)
    {
        var catalogs = GetCatalogsFor(cashier);

        // prototype -> (name, catalog name, default price)
        var known = new Dictionary<string, (string Name, string Catalog, int DefaultPrice)>();
        foreach (var catalog in catalogs)
        {
            var catalogName = Loc.GetString(catalog.Name);
            foreach (var entry in GetCatalogEntries(catalog))
            {
                known.TryAdd(entry.Prototype.Id, (entry.Name, catalogName, GetDefaultPrice(ProtoMan.Index(entry.Prototype))));
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
            if (!overridden && (cashier == null || !IsSellable(cashier.Value, uid)))
                continue;

            // Empty glasses and plates are not goods.
            if (IsDrink(uid, out var solutionName) && GetDrinkPrice(uid, solutionName) <= 0 && !overridden)
                continue;

            var count = TryComp<StackComponent>(uid, out var stack) ? stack.Count : 1;
            counts[proto.ID] = counts.GetValueOrDefault(proto.ID) + count;

            if (!known.ContainsKey(proto.ID))
            {
                var defaultPrice = GetPrice(ent, uid);
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

            known[proto.Id] = (protoDef.Name, Loc.GetString("cash-register-catalog-custom"), GetDefaultPrice(protoDef));
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
        var message = _messages.GetValueOrDefault(ent.Owner, string.Empty);

        if (_ui.IsUiOpen(ent.Owner, CashRegisterUiKey.Key))
        {
            // The price list depends on the cashier's job; use the acting cashier, else whoever has it open.
            EntityUid? cashier = actor;
            if (cashier == null || !_ui.IsUiOpen(ent.Owner, CashRegisterUiKey.Key, cashier.Value))
            {
                var first = _ui.GetActors(ent.Owner, CashRegisterUiKey.Key).FirstOrDefault();
                cashier = first == default ? null : first;
            }

            var account = ent.Comp.Draft.Account;
            var balance = station != null ? (int) _economy.GetBalance(EconomyParty.Department(station.Value, account)) : 0;

            var accounts = new List<CashRegisterAccount>();
            if (cashier != null)
            {
                foreach (var id in GetAccountsFor(ent, cashier.Value))
                    accounts.Add(new CashRegisterAccount(id, _economy.Describe(EconomyParty.Department(station ?? EntityUid.Invalid, id))));
            }

            var state = new CashRegisterBuiState(
                GetItems(ent, cashier),
                GetCatalogsFor(cashier).Select(c => Loc.GetString(c.Name)).ToList(),
                accounts,
                ent.Comp.Draft,
                ent.Comp.Bill,
                _economy.Describe(EconomyParty.Department(station ?? EntityUid.Invalid, account)),
                balance,
                _cfg.GetCVar(CCVars.EconomyServiceCommissionPercent),
                message);

            _ui.SetUiState(ent.Owner, CashRegisterUiKey.Key, state);
        }

        if (_ui.IsUiOpen(ent.Owner, CashRegisterUiKey.Customer))
        {
            var billAccount = ent.Comp.Bill?.Account ?? ent.Comp.Draft.Account;
            var department = _economy.Describe(EconomyParty.Department(station ?? EntityUid.Invalid, billAccount));
            _ui.SetUiState(ent.Owner, CashRegisterUiKey.Customer, new CashRegisterCustomerBuiState(ent.Comp.Bill, department, message));
        }
    }

    private void SetMessage(Entity<CashRegisterComponent> ent, EntityUid? actor, string message)
    {
        _messages[ent.Owner] = message;
        UpdateUi(ent, actor);
    }

    /// <summary>Adds to the draft; lines with the same name and price are merged.</summary>
    private void AddToDraft(Entity<CashRegisterComponent> ent, EntProtoId? proto, string name, int count, int price)
    {
        var line = ent.Comp.Draft.Lines.FirstOrDefault(l => l.Name == name && l.Price == price);
        if (line == null)
        {
            if (count <= 0)
                return;

            ent.Comp.Draft.Lines.Add(new CashRegisterLine { Prototype = proto, Name = name, Count = count, Price = price });
        }
        else
        {
            line.Count += count;
            line.Prototype ??= proto;
            if (line.Count <= 0)
                ent.Comp.Draft.Lines.Remove(line);
        }

        Dirty(ent);
    }

    private bool CheckStaff(Entity<CashRegisterComponent> ent, EntityUid actor)
    {
        if (!IsPowered(ent))
            return false;

        if (IsStaff(ent, actor))
            return true;

        _ui.CloseUi(ent.Owner, CashRegisterUiKey.Key, actor);
        return false;
    }

    private void OnAdjustDraft(Entity<CashRegisterComponent> ent, ref CashRegisterAdjustDraftMessage args)
    {
        if (!CheckStaff(ent, args.Actor) || args.Count == 0 || Math.Abs(args.Count) > 1000)
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

    private void OnAddManual(Entity<CashRegisterComponent> ent, ref CashRegisterAddManualMessage args)
    {
        if (!CheckStaff(ent, args.Actor))
            return;

        var name = args.Name.Trim();
        if (name.Length == 0 || name.Length > 60 || args.Price <= 0 || args.Price > 1_000_000 || args.Count <= 0 || args.Count > 1000)
        {
            SetMessage(ent, args.Actor, Loc.GetString("cash-register-manual-invalid"));
            return;
        }

        AddToDraft(ent, null, name, args.Count, args.Price);
        _adminLog.Add(LogType.Economy, LogImpact.Low,
            $"{ToPrettyString(args.Actor):player} added manual line {args.Count}x '{name}' @ {args.Price} to cash register {ToPrettyString(ent)}");
        UpdateUi(ent, args.Actor);
    }

    private void OnRemoveLine(Entity<CashRegisterComponent> ent, ref CashRegisterRemoveLineMessage args)
    {
        if (!CheckStaff(ent, args.Actor) || args.Index < 0 || args.Index >= ent.Comp.Draft.Lines.Count)
            return;

        ent.Comp.Draft.Lines.RemoveAt(args.Index);
        Dirty(ent);
        UpdateUi(ent, args.Actor);
    }

    private void OnSetAccount(Entity<CashRegisterComponent> ent, ref CashRegisterSetAccountMessage args)
    {
        if (!CheckStaff(ent, args.Actor))
            return;

        var account = new ProtoId<CargoAccountPrototype>(args.Account);
        if (!GetAccountsFor(ent, args.Actor).Contains(account))
            return;

        ent.Comp.Draft.Account = account;
        Dirty(ent);
        UpdateUi(ent, args.Actor);
    }

    private void OnClearDraft(Entity<CashRegisterComponent> ent, ref CashRegisterClearDraftMessage args)
    {
        if (!CheckStaff(ent, args.Actor))
            return;

        ent.Comp.Draft = new CashRegisterBill { Account = ent.Comp.Draft.Account };
        Dirty(ent);
        UpdateUi(ent, args.Actor);
    }

    private void OnCreateBill(Entity<CashRegisterComponent> ent, ref CashRegisterCreateBillMessage args)
    {
        if (!CheckStaff(ent, args.Actor))
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

        // The sale goes to one of the cashier's departments, never elsewhere.
        var accounts = GetAccountsFor(ent, args.Actor);
        if (accounts.Count == 0)
            return;
        if (!accounts.Contains(bill.Account))
            bill.Account = accounts[0];

        bill.Cashier = Name(args.Actor);
        ent.Comp.Bill = bill;
        ent.Comp.Draft = new CashRegisterBill { Account = bill.Account };
        ent.Comp.CashierUserId = _players.TryGetSessionByEntity(args.Actor, out var session) ? session.UserId : null;
        Dirty(ent);
        _appearance.SetData(ent, CashRegisterVisuals.HasBill, true);
        SetMessage(ent, args.Actor, Loc.GetString("cash-register-bill-created", ("total", bill.Total)));
        _popup.PopupEntity(Loc.GetString("cash-register-bill-popup", ("total", bill.Total)), ent, PopupType.Medium);
    }

    private void OnCancelBill(Entity<CashRegisterComponent> ent, ref CashRegisterCancelBillMessage args)
    {
        if (!CheckStaff(ent, args.Actor))
            return;

        ent.Comp.Bill = null;
        ent.Comp.CashierUserId = null;
        Dirty(ent);
        _appearance.SetData(ent, CashRegisterVisuals.HasBill, false);
        SetMessage(ent, args.Actor, Loc.GetString("cash-register-bill-cancelled"));
    }

    private void OnSetPrice(Entity<CashRegisterComponent> ent, ref CashRegisterSetPriceMessage args)
    {
        if (!CheckStaff(ent, args.Actor) || !ProtoMan.HasIndex<EntityPrototype>(args.Prototype))
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

        // Tools work on the machine, they are not goods (the wrench is handled by AnchorableSystem before us).
        if (TryComp<Content.Shared.Tools.Components.ToolComponent>(args.Used, out var tool) && tool.Qualities.Count > 0)
            return;

        if (!IsPowered(ent))
        {
            args.Handled = true;
            _popup.PopupEntity(Loc.GetString("cash-register-no-power"), ent, args.User, PopupType.SmallCaution);
            return;
        }

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

        var overridden = ent.Comp.Prices.ContainsKey(proto.ID) || _globalPrices.ContainsKey(proto.ID);
        if (!overridden && !IsSellable(cashier, item))
        {
            Deny(ent, cashier, Loc.GetString("cash-register-not-sellable", ("item", Name(item))));
            return;
        }

        var price = GetPrice(ent, item);
        if (price <= 0)
        {
            Deny(ent, cashier, Loc.GetString(IsDrink(item, out _) ? "cash-register-empty-drink" : "cash-register-worthless", ("item", Name(item))));
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
        if (!IsPowered(ent))
            return;

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

        var department = EconomyParty.Department(station, bill.Account);
        var (commission, departmentShare) = Split(total);
        var reason = $"cash register {ToPrettyString(ent)} sale by {bill.Cashier}: {Describe(bill)}";

        if (!_economy.TryTransfer(payer, department, departmentShare, reason, out error))
        {
            Deny(ent, user, _economy.ErrorText(error));
            return;
        }

        if (commission > 0)
        {
            // Cashier offline or unknown: the department keeps it.
            var cashier = ent.Comp.CashierUserId is { } id ? EconomyParty.Player(id) : department;
            if (!_economy.TryTransfer(payer, cashier, commission, reason + " (commission)", out _))
                _economy.TryTransfer(payer, department, commission, reason + " (commission fallback)", out _);
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

        var department = EconomyParty.Department(station, bill.Account);
        var (commission, departmentShare) = Split(total);
        var reason = $"cash register {ToPrettyString(ent)} cash sale by {bill.Cashier}: {Describe(bill)}";

        // Physical cash is destroyed and turned into account credit.
        _economy.TryTransfer(EconomyParty.External, department, departmentShare, reason, out _);
        if (commission > 0)
        {
            if (ent.Comp.CashierUserId is { } id)
                _economy.Pay(EconomyParty.Player(id), commission, reason + " (commission)");
            else
                _economy.TryTransfer(EconomyParty.External, department, commission, reason + " (commission fallback)", out _);
        }

        Complete(ent, user, Loc.GetString("cash-register-paid-cash", ("total", total), ("change", change)));
    }

    private (int Commission, int Department) Split(int total)
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
        if (!IsPowered(ent))
            args.PushMarkup(Loc.GetString("cash-register-examine-unpowered"));
        else if (ent.Comp.Bill is { } bill)
            args.PushMarkup(Loc.GetString("cash-register-examine-bill", ("total", bill.Total), ("cashier", bill.Cashier)));
        else
            args.PushMarkup(Loc.GetString("cash-register-examine-idle"));
    }

    #endregion
}
