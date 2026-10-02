// iss14: Economy
using System.Linq;
using Content.Server.Cargo.Systems;
using Content.Server.Chat.Managers;
using Content.Server.Objectives.Components;
using Content.Server.Objectives;
using Content.Server.Shuttles.Systems;
using Content.Server.Station.Systems;
using Content.Shared.Cargo.Components;
using Content.Shared.CCVar;
using Content.Shared.Economy;
using Content.Shared.GameTicking;
using Content.Shared.Ghost.Components;
using Content.Shared.Inventory;
using Content.Shared.Mind;
using Content.Shared.Mobs.Systems;
using Content.Shared.Roles;
using Robust.Server.Player;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;

namespace Content.Server.Economy;

/// <summary>
/// Pays antagonists for what they carry off the station. At round end every antagonist mind whose living body is
/// on the evacuation shuttle is appraised: cash and items worth at least <c>economy.heist_min_item_value</c> in
/// their hands, inventory and bags. The value × <c>economy.heist_payout_factor</c> lands on their bank account;
/// the same value is drained from the station's accounts (the station lost it). Thieves additionally get
/// <c>economy.heist_thief_bonus</c> per completed steal objective.
/// </summary>
public sealed partial class HeistSystem : EntitySystem
{
    [Dependency] private EconomySystem _economy = default!;
    [Dependency] private PricingSystem _pricing = default!;
    [Dependency] private EmergencyShuttleSystem _emergency = default!;
    [Dependency] private SharedRoleSystem _roles = default!;
    [Dependency] private ObjectivesSystem _objectives = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private ServerStationSystem _station = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IChatManager _chat = default!;

    private bool _paidThisRound;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RoundEndTextAppendEvent>(OnRoundEndText);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => _paidThisRound = false);
    }

    private void OnRoundEndText(ref RoundEndTextAppendEvent ev)
    {
        if (_paidThisRound || !_cfg.GetCVar(CCVars.EconomyHeistEnabled))
            return;

        _paidThisRound = true;

        var lines = PayHeists();
        foreach (var line in lines)
        {
            ev.AddLine(line);
        }
    }

    /// <summary>Appraised loot on a mob: cash always, other items above the minimum value.</summary>
    public double AppraiseLoot(EntityUid mob, out List<(string Name, double Value)> items)
    {
        items = new List<(string, double)>();
        var min = _cfg.GetCVar(CCVars.EconomyHeistMinItemValue);
        var total = 0.0;

        foreach (var item in _inventory.GetHandOrInventoryEntities(mob))
        {
            var value = _pricing.GetPrice(item);
            if (value <= 0)
                continue;

            if (!HasComp<CashComponent>(item) && value < min)
                continue;

            items.Add((Name(item), value));
            total += value;
        }

        return total;
    }

    /// <summary>Pays all escaping antagonists. Returns the round-end summary lines.</summary>
    public List<string> PayHeists()
    {
        var result = new List<string>();
        var factor = Math.Max(0f, _cfg.GetCVar(CCVars.EconomyHeistPayoutFactor));
        var thiefBonus = Math.Max(0, _cfg.GetCVar(CCVars.EconomyHeistThiefBonus));

        var query = EntityQueryEnumerator<MindComponent>();
        while (query.MoveNext(out var mindId, out var mind))
        {
            var userId = mind.UserId ?? mind.OriginalOwnerUserId;
            if (userId == null || !_roles.MindIsAntagonist(mindId))
                continue;

            // The body: current entity unless they're ghosting, then whatever they originally owned.
            var body = mind.CurrentEntity;
            if (body == null || HasComp<GhostComponent>(body))
                body = GetEntity(mind.OriginalOwnedEntity);

            if (body is not { } mob || !Exists(mob) || _mobState.IsDead(mob) || !_emergency.IsTargetEscaping(mob))
                continue;

            var loot = AppraiseLoot(mob, out var items);
            var payout = (long) Math.Floor(loot * factor);

            var completedSteals = 0;
            foreach (var objective in mind.Objectives)
            {
                if (HasComp<StealConditionComponent>(objective) && _objectives.IsCompleted(objective, (mindId, mind)))
                    completedSteals++;
            }

            var bonus = (long) completedSteals * thiefBonus;
            if (payout + bonus <= 0)
                continue;

            var name = mind.CharacterName ?? Name(mob);

            // The station loses what was carried off.
            if (loot > 0)
            {
                var station = _station.GetOwningStation(mob) ?? _station.GetStations().Select(s => s.Owner).FirstOrDefault();
                if (station != default)
                    _economy.DrainStation(station, (long) Math.Floor(loot), $"heist: {name} escaped with {string.Join(", ", items.Select(i => $"{i.Name} ({i.Value:0})"))}");
            }

            var reason = $"heist payout: {loot:0} loot x{factor}" + (bonus > 0 ? $" + {completedSteals} steal objectives" : "");
            _economy.Pay(EconomyParty.Player(userId.Value), payout + bonus, reason);

            if (_players.TryGetSessionById(userId.Value, out var session) && session.Status != SessionStatus.Disconnected)
            {
                _chat.DispatchServerMessage(session, Loc.GetString("heist-notice",
                    ("credits", payout + bonus), ("loot", (long) Math.Floor(loot)), ("bonus", bonus)));
            }

            result.Add(Loc.GetString("heist-round-end-line", ("name", name), ("credits", payout + bonus), ("loot", (long) Math.Floor(loot))));
        }

        if (result.Count > 0)
            result.Insert(0, Loc.GetString("heist-round-end-header"));

        return result;
    }
}
