// iss14: Economy
using System.Linq;
using Content.Server.Administration.Logs;
using Content.Server.Cargo.Systems;
using Content.Server.Stack;
using Content.Server.Station.Systems;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Shared.CCVar;
using Content.Shared.Database;
using Content.Shared.Economy;
using Content.Shared.Examine;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Stacks;
using Content.Shared.UserInterface;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;

namespace Content.Server.Economy;

/// <summary>
/// Counter cash register (<see cref="CashRegisterComponent"/>). Staff write a bill from the items lying around the
/// register; customers pay by swiping their ID card on it or handing it cash. The sale is booked to the register's
/// department account except for <c>economy.service_commission_percent</c>, which goes to the cashier.
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
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private StackSystem _stack = default!;
    [Dependency] private ServerStationSystem _station = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IAdminLogManager _adminLog = default!;
    [Dependency] private AppearanceSystem _appearance = default!;

    private readonly Dictionary<EntityUid, string> _messages = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<CashRegisterComponent, BeforeActivatableUIOpenEvent>((e, c, _) => UpdateUi((e, c)));
        SubscribeLocalEvent<CashRegisterComponent, CashRegisterRefreshMessage>((e, c, _) => UpdateUi((e, c)));
        SubscribeLocalEvent<CashRegisterComponent, CashRegisterCreateBillMessage>(OnCreateBill);
        SubscribeLocalEvent<CashRegisterComponent, CashRegisterCancelBillMessage>(OnCancelBill);
        SubscribeLocalEvent<CashRegisterComponent, CashRegisterSetPriceMessage>(OnSetPrice);
        SubscribeLocalEvent<CashRegisterComponent, InteractUsingEvent>(OnInteractUsing);
        SubscribeLocalEvent<CashRegisterComponent, ExaminedEvent>(OnExamined);
    }

    #region Pricing & nearby items

    /// <summary>Default price of an item: appraised value × multiplier, at least 1 (0 if it has no value).</summary>
    public int GetDefaultPrice(EntityUid item)
    {
        var appraised = _pricing.GetPrice(item);
        return EconomyPricing.VendPrice(appraised, _cfg.GetCVar(CCVars.EconomyServicePriceMultiplier));
    }

    public int GetPrice(Entity<CashRegisterComponent> ent, EntProtoId prototype, EntityUid sample)
        => ent.Comp.Prices.TryGetValue(prototype, out var price) ? price : GetDefaultPrice(sample);

    /// <summary>Sellable items around the register, grouped by prototype.</summary>
    public List<CashRegisterItem> GetNearbyItems(Entity<CashRegisterComponent> ent)
    {
        var groups = new Dictionary<EntProtoId, (string Name, int Count, EntityUid Sample)>();
        var xform = Transform(ent);

        foreach (var uid in _lookup.GetEntitiesInRange(xform.Coordinates, ent.Comp.Range, LookupFlags.Dynamic | LookupFlags.Sundries))
        {
            if (uid == ent.Owner || Transform(uid).Anchored)
                continue;

            if (MetaData(uid).EntityPrototype is not { } proto)
                continue;

            // Only things one could actually hand over: items (not mobs, not the counter itself).
            if (!HasComp<Content.Shared.Item.ItemComponent>(uid))
                continue;

            var count = TryComp<StackComponent>(uid, out var stack) ? stack.Count : 1;
            if (groups.TryGetValue(proto.ID, out var existing))
                groups[proto.ID] = (existing.Name, existing.Count + count, existing.Sample);
            else
                groups[proto.ID] = (Name(uid), count, uid);
        }

        var result = new List<CashRegisterItem>();
        foreach (var (proto, group) in groups)
        {
            var defaultPrice = GetDefaultPrice(group.Sample);
            var overridden = ent.Comp.Prices.TryGetValue(proto, out var price);
            if (!overridden)
                price = defaultPrice;

            if (price <= 0 && !overridden)
                continue;

            result.Add(new CashRegisterItem(proto, group.Name, group.Count, price, defaultPrice, overridden));
        }

        // Also list overridden prototypes that aren't on the counter right now, so prices can be managed.
        foreach (var (proto, price) in ent.Comp.Prices)
        {
            if (groups.ContainsKey(proto) || !ProtoMan.TryIndex(proto, out var protoDef))
                continue;

            result.Add(new CashRegisterItem(proto, protoDef.Name, 0, price, 0, true));
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    #endregion

    #region UI

    private void UpdateUi(Entity<CashRegisterComponent> ent)
    {
        var station = _station.GetOwningStation(ent);
        var balance = station != null ? (int) _economy.GetBalance(EconomyParty.Department(station.Value, ent.Comp.Account)) : 0;
        var department = _economy.Describe(EconomyParty.Department(station ?? EntityUid.Invalid, ent.Comp.Account));

        var state = new CashRegisterBuiState(
            GetNearbyItems(ent),
            ent.Comp.Bill,
            department,
            balance,
            _cfg.GetCVar(CCVars.EconomyServiceCommissionPercent),
            _messages.GetValueOrDefault(ent.Owner, string.Empty));

        _ui.SetUiState(ent.Owner, CashRegisterUiKey.Key, state);
    }

    private void SetMessage(Entity<CashRegisterComponent> ent, string message)
    {
        _messages[ent.Owner] = message;
        UpdateUi(ent);
    }

    private void OnCreateBill(Entity<CashRegisterComponent> ent, ref CashRegisterCreateBillMessage args)
    {
        var nearby = GetNearbyItems(ent).ToDictionary(i => i.Prototype);
        var bill = new CashRegisterBill { Cashier = Name(args.Actor) };

        foreach (var (proto, count) in args.Items)
        {
            if (count <= 0 || !nearby.TryGetValue(proto, out var item))
                continue;

            bill.Lines.Add(new CashRegisterLine
            {
                Prototype = proto,
                Name = item.Name,
                Count = Math.Min(count, Math.Max(1, item.Count)),
                Price = item.Price,
            });
        }

        if (bill.Lines.Count == 0 || bill.Total <= 0)
        {
            SetMessage(ent, Loc.GetString("cash-register-empty-bill"));
            return;
        }

        ent.Comp.Bill = bill;
        ent.Comp.CashierUserId = _players.TryGetSessionByEntity(args.Actor, out var session) ? session.UserId : null;
        Dirty(ent);
        _appearance.SetData(ent, CashRegisterVisuals.HasBill, true);
        SetMessage(ent, Loc.GetString("cash-register-bill-created", ("total", bill.Total)));
        _popup.PopupEntity(Loc.GetString("cash-register-bill-popup", ("total", bill.Total)), ent, PopupType.Medium);
    }

    private void OnCancelBill(Entity<CashRegisterComponent> ent, ref CashRegisterCancelBillMessage args)
    {
        ent.Comp.Bill = null;
        ent.Comp.CashierUserId = null;
        Dirty(ent);
        _appearance.SetData(ent, CashRegisterVisuals.HasBill, false);
        SetMessage(ent, Loc.GetString("cash-register-bill-cancelled"));
    }

    private void OnSetPrice(Entity<CashRegisterComponent> ent, ref CashRegisterSetPriceMessage args)
    {
        if (!ProtoMan.HasIndex<EntityPrototype>(args.Prototype))
            return;

        if (args.Price is { } price && price >= 0)
            ent.Comp.Prices[args.Prototype] = Math.Min(price, 1_000_000);
        else
            ent.Comp.Prices.Remove(args.Prototype);

        _adminLog.Add(LogType.Economy, LogImpact.Low,
            $"{ToPrettyString(args.Actor):player} set cash register {ToPrettyString(ent)} price of {args.Prototype} to {(args.Price?.ToString() ?? "default")}");
        UpdateUi(ent);
    }

    #endregion

    #region Payment

    private void OnInteractUsing(Entity<CashRegisterComponent> ent, ref InteractUsingEvent args)
    {
        if (args.Handled)
            return;

        if (ent.Comp.Bill == null)
        {
            // Only react to things that are obviously payment attempts.
            if (IsCash(args.Used) || _idCard.TryFindIdCard(args.Used, out _))
            {
                args.Handled = true;
                Deny(ent, args.User, Loc.GetString("cash-register-no-bill"));
            }

            return;
        }

        if (IsCash(args.Used))
        {
            args.Handled = true;
            PayWithCash(ent, args.User, args.Used);
            return;
        }

        if (_idCard.TryFindIdCard(args.Used, out var card))
        {
            args.Handled = true;
            PayWithCard(ent, args.User, card);
        }
    }

    private bool IsCash(EntityUid uid)
        => TryComp<StackComponent>(uid, out var stack) && stack.StackTypeId == "Credit" && stack.Count > 0;

    private void PayWithCard(Entity<CashRegisterComponent> ent, EntityUid user, EntityUid card)
    {
        var bill = ent.Comp.Bill!;

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

        Complete(ent, user, total, Loc.GetString("cash-register-paid-card", ("total", total)));
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

        Complete(ent, user, total, Loc.GetString("cash-register-paid-cash", ("total", total), ("change", change)));
    }

    private (int Commission, int Department) Split(Entity<CashRegisterComponent> ent, int total)
    {
        var percent = Math.Clamp(_cfg.GetCVar(CCVars.EconomyServiceCommissionPercent), 0f, 100f);
        var commission = (int) Math.Floor(total * percent / 100.0);
        return (commission, total - commission);
    }

    private static string Describe(CashRegisterBill bill)
        => string.Join(", ", bill.Lines.Select(l => $"{l.Count}x {l.Name} @ {l.Price}"));

    private void Complete(Entity<CashRegisterComponent> ent, EntityUid user, int total, string message)
    {
        ent.Comp.Bill = null;
        ent.Comp.CashierUserId = null;
        Dirty(ent);
        _appearance.SetData(ent, CashRegisterVisuals.HasBill, false);
        _audio.PlayPvs(ent.Comp.PaySound, ent);
        _popup.PopupEntity(message, ent, user);
        SetMessage(ent, message);
    }

    private void Deny(Entity<CashRegisterComponent> ent, EntityUid user, string message)
    {
        _audio.PlayPvs(ent.Comp.DenySound, ent);
        _popup.PopupEntity(message, ent, user, PopupType.SmallCaution);
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
