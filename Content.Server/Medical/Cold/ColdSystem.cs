using System.Linq;
using Content.Server.Chat.Systems;
using Content.Server.StationEvents.Events;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Damage.Systems;
using Content.Shared.GameTicking.Components;
using Content.Shared.GameTicking.Rules;
using Robust.Server.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.Enums;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server.Medical.Cold;

/// <summary>
/// Runs the Cold infection: periodic damage, coughing after damage, and proximity spread.
/// </summary>
public sealed partial class ColdSystem : EntitySystem
{
    private static readonly ProtoId<DamageTypePrototype> PoisonDamage = "Poison";

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private DamageableSystem _damageable = default!;
    [Dependency] private ChatSystem _chat = default!;

    private readonly HashSet<EntityUid> _nearbyPlayers = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ColdInfectionComponent, DamageChangedEvent>(OnDamaged);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<ColdInfectionComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var infection, out var transform))
        {
            if (now >= infection.EndTime)
            {
                RemCompDeferred<ColdInfectionComponent>(uid);
                continue;
            }

            if (now >= infection.NextDamageTime)
            {
                _damageable.TryChangeDamage(uid,
                    new DamageSpecifier(ProtoMan.Index(PoisonDamage), 0.3f),
                    interruptsDoAfters: false);
                infection.NextDamageTime = now + TimeSpan.FromMinutes(1);
            }

            if (now < infection.NextSpreadTime)
                continue;

            infection.NextSpreadTime = now + TimeSpan.FromSeconds(5);
            _lookup.GetEntitiesInRange(transform.Coordinates, 2f, _nearbyPlayers);

            foreach (var candidate in _nearbyPlayers)
            {
                if (candidate == uid || !IsInGamePlayer(candidate) || HasComp<ColdInfectionComponent>(candidate))
                    continue;

                Infect(candidate);
            }
        }
    }

    /// <summary>
    /// Applies a cold for twenty minutes, unless the target already has one.
    /// </summary>
    public void Infect(EntityUid uid)
    {
        if (!IsInGamePlayer(uid) || HasComp<ColdInfectionComponent>(uid))
            return;

        var now = _timing.CurTime;
        AddComp(uid, new ColdInfectionComponent
        {
            NextDamageTime = now + TimeSpan.FromMinutes(1),
            NextSpreadTime = now + TimeSpan.FromSeconds(5),
            EndTime = now + TimeSpan.FromMinutes(20)
        });
    }

    private void OnDamaged(EntityUid uid, ColdInfectionComponent component, DamageChangedEvent args)
    {
        if (!args.DamageIncreased)
            return;

        // The emote system supplies the appropriate voice sound for the entity.
        _chat.TryEmoteWithChat(uid, "Cough");
    }

    private bool IsInGamePlayer(EntityUid uid)
    {
        foreach (var session in _players.Sessions)
        {
            if (session.Status == SessionStatus.InGame && session.AttachedEntity == uid)
                return true;
        }

        return false;
    }
}

/// <summary>
/// Starts a cold on one random in-game player when the Cold station event fires.
/// </summary>
public sealed partial class ColdRuleSystem : StationEventSystem<ColdRuleComponent>
{
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private ColdSystem _cold = default!;

    protected override void Started(Entity<ColdRuleComponent, GameRuleComponent> ent, ref GameRuleStartedEvent args)
    {
        base.Started(ent, ref args);

        var players = _players.Sessions
            .Where(session => session.Status == SessionStatus.InGame && session.AttachedEntity is { } entity && entity.Valid)
            .Select(session => session.AttachedEntity!.Value)
            .ToList();

        if (players.Count > 0)
            _cold.Infect(_random.Pick(players));
    }
}
