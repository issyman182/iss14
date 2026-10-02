// iss14: Economy — privately paid cargo orders and the per-round private-order blocklist.
using System.Linq;
using Content.Server.Cargo.Components;
using Content.Server.Economy;
using Content.Shared.Access.Systems;
using Content.Shared.Cargo.Components;
using Content.Shared.Cargo.Events;
using Content.Shared.Cargo.Prototypes;
using Content.Shared.CCVar;
using Content.Shared.Database;
using Content.Shared.Economy;
using Content.Shared.Emag.Systems;
using Robust.Shared.Prototypes;

namespace Content.Server.Cargo.Systems;

public sealed partial class CargoSystem
{
    [Dependency] private EconomySystem _economy = default!;
    [Dependency] private SharedIdCardSystem _idCardSystem = default!;

    public const string DefaultPrivateBlocklist = "Default";

    private void InitializeEconomy()
    {
        SubscribeLocalEvent<CargoOrderConsoleComponent, CargoConsoleApprovePrivateOrderMessage>(OnApprovePrivateOrder);
        SubscribeLocalEvent<CargoOrderConsoleComponent, CargoConsoleSetPrivateBlockMessage>(OnSetPrivateBlock);
        SubscribeLocalEvent<CargoOrderConsoleComponent, CargoConsoleTogglePrivateBlocklistMessage>(OnTogglePrivateBlocklist);
    }

    /// <summary>The station's private-order policy, seeded from the default blocklist on first use.</summary>
    public CargoPrivateOrderPolicyComponent EnsurePrivatePolicy(EntityUid station)
    {
        var policy = EnsureComp<CargoPrivateOrderPolicyComponent>(station);
        if (policy.Seeded)
            return policy;

        policy.Seeded = true;
        if (ProtoMan.TryIndex<CargoPrivateBlocklistPrototype>(DefaultPrivateBlocklist, out var defaults))
        {
            var categories = defaults.Categories.ToHashSet();
            foreach (var product in ProtoMan.EnumeratePrototypes<CargoProductPrototype>())
            {
                if (product.Abstract)
                    continue;

                if (categories.Contains(product.Category))
                    policy.Blocked.Add(product.ID);
            }

            foreach (var product in defaults.Products)
            {
                policy.Blocked.Add(product);
            }
        }

        Dirty(station, policy);
        return policy;
    }

    /// <summary>Whether a product may currently be bought privately on this station.</summary>
    public bool IsPrivatelyBlocked(EntityUid station, ProtoId<CargoProductPrototype> product)
    {
        if (!_cfg.GetCVar(CCVars.EconomyCargoPrivateBlocklist))
            return false;

        var policy = EnsurePrivatePolicy(station);
        return policy.BlocklistEnabled && policy.Blocked.Contains(product);
    }

    /// <summary>Price of a privately paid order.</summary>
    public int GetPrivateCost(CargoProductPrototype product, int quantity)
    {
        var markup = Math.Max(0f, _cfg.GetCVar(CCVars.EconomyCargoPrivateMarkup));
        return (int) Math.Ceiling(product.Cost * quantity * markup);
    }

    private void OnApprovePrivateOrder(Entity<CargoOrderConsoleComponent> ent, ref CargoConsoleApprovePrivateOrderMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        if (!_cfg.GetCVar(CCVars.EconomyCargoPrivateOrders))
        {
            _popup.PopupCursor(Loc.GetString("cargo-console-private-disabled"), player);
            PlayDenySound(ent);
            return;
        }

        if (ent.Comp.Mode != CargoOrderConsoleMode.DirectOrder)
            return;

        var station = _station.GetOwningStation(ent.Owner);
        if (!_stationQuery.TryComp(station, out var stationData) || !TryGetOrderDatabase(station, out var orderDatabase))
        {
            _popup.PopupCursor(Loc.GetString("cargo-console-station-not-found"), player);
            PlayDenySound(ent);
            return;
        }

        var orderId = args.OrderId;
        var order = orderDatabase.Orders[ent.Comp.Account].Find(o => orderId == o.OrderId && !o.Approved);
        if (order == null || !ProtoMan.Resolve(order.Product, out var product))
            return;

        if (IsPrivatelyBlocked(station.Value, order.Product))
        {
            _popup.PopupCursor(Loc.GetString("cargo-console-private-blocked"), player);
            PlayDenySound(ent);
            return;
        }

        // The buyer pays with the ID card they carry; fingerprint policy as everywhere else.
        var error = EconomyPaymentError.None;
        if (!_idCardSystem.TryFindIdCard(player, out var card)
            || !_economy.TryGetCardPayer(card, player, out var payer, out error))
        {
            _popup.PopupCursor(error == EconomyPaymentError.None
                ? Loc.GetString("cargo-console-private-no-card")
                : _economy.ErrorText(error), player);
            PlayDenySound(ent);
            return;
        }

        var amount = GetOutstandingOrderCount((station.Value, orderDatabase), order.Account);
        var capacity = orderDatabase.Capacity;
        if (amount >= capacity)
        {
            _popup.PopupCursor(Loc.GetString("cargo-console-too-many"), player);
            PlayDenySound(ent);
            return;
        }

        var cappedAmount = Math.Min(capacity - amount, order.OrderQuantity);
        if (cappedAmount != order.OrderQuantity)
        {
            order.OrderQuantity = cappedAmount;
            _popup.PopupCursor(Loc.GetString("cargo-console-snip-snip"), player);
            PlayDenySound(ent);
        }

        var cost = GetPrivateCost(product, order.OrderQuantity);
        if (_economy.GetBalance(payer) < cost)
        {
            _popup.PopupCursor(Loc.GetString("cargo-console-private-insufficient", ("cost", cost)), player);
            PlayDenySound(ent);
            return;
        }

        var emagged = _emag.CheckFlag(ent.Owner, EmagType.Interaction);
        if (!emagged)
            order.SetApproverData(_identity.GetIdentityShortInfo(player, ent.Owner));

        order.ApprovingConsole = GetNetEntity(ent.Owner);
        order.Approved = true;

        var ev = new FulfillCargoOrderEvent((station.Value, stationData), order);
        RaiseLocalEvent(ref ev);
        ev.FulfillmentEntity ??= station.Value;

        if (!ev.Handled)
        {
            ev.FulfillmentEntity = TryFulfillOrder((station.Value, stationData), order.Account, order, orderDatabase);

            if (ev.FulfillmentEntity == null)
            {
                _popup.PopupCursor(Loc.GetString("cargo-console-unfulfilled"), player);
                PlayDenySound(ent);
                order.Approver = null;
                order.ApprovingConsole = null;
                order.Approved = false;
                return;
            }
        }

        // Charge only once the order is actually on its way. Private money leaves the station economy (CentComm sells).
        if (!_economy.TryCharge(payer, cost,
                $"private cargo order #{order.OrderId}: {order.OrderQuantity}x {order.Product} by {order.Requester}", out error))
        {
            // Should not happen (balance checked above); roll the approval back to be safe.
            _popup.PopupCursor(_economy.ErrorText(error), player);
            PlayDenySound(ent);
            order.Approver = null;
            order.ApprovingConsole = null;
            order.Approved = false;
            return;
        }

        _audio.PlayPvs(ApproveSound, ent.Owner);

        if (!emagged)
        {
            var account = ProtoMan.Index(order.Account);
            var message = Loc.GetString("cargo-console-private-approved-broadcast",
                ("productName", Loc.GetString(product.Name)),
                ("orderAmount", order.OrderQuantity),
                ("approver", order.Approver ?? string.Empty),
                ("cost", cost));
            _radio.SendRadioMessage(ent.Owner, message, account.RadioChannel, ent.Owner, escapeMarkup: false);
            if (CargoOrderConsoleComponent.BaseAnnouncementChannel != account.RadioChannel)
                _radio.SendRadioMessage(ent.Owner, message, CargoOrderConsoleComponent.BaseAnnouncementChannel, ent.Owner, escapeMarkup: false);
        }

        _popup.PopupCursor(Loc.GetString("cargo-console-private-paid", ("cost", cost)), player);

        _adminLogger.Add(LogType.Economy, LogImpact.Low,
            $"{ToPrettyString(player):user} privately paid order [orderId:{order.OrderId}, quantity:{order.OrderQuantity}, product:{order.Product}, requester:{order.Requester}] for {cost} credits");

        orderDatabase.Orders[ent.Comp.Account].Remove(order);
        UpdateOrders(station.Value);
    }

    private bool CanEditPrivatePolicy(Entity<CargoOrderConsoleComponent> ent, EntityUid actor)
    {
        if (!_accessReaderSystem.IsAllowed(actor, ent.Owner))
        {
            _popup.PopupCursor(Loc.GetString("cargo-console-order-not-allowed"), actor);
            PlayDenySound(ent);
            return false;
        }

        if (_bankQuery.TryComp(_station.GetOwningStation(ent.Owner), out var bank) && ent.Comp.Account != bank.PrimaryAccount)
        {
            _popup.PopupCursor(Loc.GetString("cargo-console-private-policy-cargo-only"), actor);
            PlayDenySound(ent);
            return false;
        }

        return true;
    }

    private void OnSetPrivateBlock(Entity<CargoOrderConsoleComponent> ent, ref CargoConsoleSetPrivateBlockMessage args)
    {
        if (!CanEditPrivatePolicy(ent, args.Actor) || _station.GetOwningStation(ent.Owner) is not { } station)
            return;

        if (!ProtoMan.HasIndex<CargoProductPrototype>(args.Product))
            return;

        var policy = EnsurePrivatePolicy(station);
        if (args.Blocked)
            policy.Blocked.Add(args.Product);
        else
            policy.Blocked.Remove(args.Product);

        Dirty(station, policy);
        _adminLogger.Add(LogType.Economy, LogImpact.Low,
            $"{ToPrettyString(args.Actor):user} {(args.Blocked ? "blocked" : "allowed")} private orders of {args.Product}");
    }

    private void OnTogglePrivateBlocklist(Entity<CargoOrderConsoleComponent> ent, ref CargoConsoleTogglePrivateBlocklistMessage args)
    {
        if (!CanEditPrivatePolicy(ent, args.Actor) || _station.GetOwningStation(ent.Owner) is not { } station)
            return;

        var policy = EnsurePrivatePolicy(station);
        policy.BlocklistEnabled = !policy.BlocklistEnabled;
        Dirty(station, policy);
        _adminLogger.Add(LogType.Economy, LogImpact.Medium,
            $"{ToPrettyString(args.Actor):user} turned the private order blocklist {(policy.BlocklistEnabled ? "on" : "off")}");
    }
}
