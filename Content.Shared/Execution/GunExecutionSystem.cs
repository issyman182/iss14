using System.Linq;
using System.Numerics;
using Content.Shared.Chat;
using Content.Shared.CombatMode;
using Content.Shared.CombatMode.Pacification;
using Content.Shared.Camera;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Database;
using Content.Shared.DoAfter;
using Content.Shared.Projectiles;
using Content.Shared.Suicide;
using Content.Shared.Verbs;
using Content.Shared.Weapons.Hitscan.Components;
using Content.Shared.Weapons.Ranged;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Shared.Execution;

/// <summary>
///     Verb for executing (and suiciding with) guns. Ported from Goob-Station.
///     Mirrors the melee execution in <see cref="SharedExecutionSystem"/>: targeting an
///     incapacitated creature - or yourself - with a gun in hand offers an "Execute" verb
///     that, after a do-after, fires a single point-blank lethal shot.
/// </summary>
public sealed partial class GunExecutionSystem : EntitySystem
{
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private SharedDoAfterSystem _doAfter = default!;
    [Dependency] private SharedSuicideSystem _suicide = default!;
    [Dependency] private SharedCombatModeSystem _combat = default!;
    [Dependency] private SharedExecutionSystem _execution = default!;
    [Dependency] private SharedGunSystem _gunSystem = default!;
    [Dependency] private IComponentFactory _componentFactory = default!;
    [Dependency] private IPrototypeManager _prototypeManager = default!;
    [Dependency] private SharedAppearanceSystem _appearanceSystem = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedCameraRecoilSystem _recoil = default!;
    [Dependency] private INetManager _net = default!;

    private const float GunExecutionTime = 4.0f;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<GunComponent, GetVerbsEvent<UtilityVerb>>(OnGetInteractionVerbsGun);
        SubscribeLocalEvent<GunComponent, ExecutionDoAfterEvent>(OnDoafterGun);
    }

    private void OnGetInteractionVerbsGun(EntityUid uid, GunComponent component, GetVerbsEvent<UtilityVerb> args)
    {
        if (args.Hands == null || args.Using == null || !args.CanAccess || !args.CanInteract)
            return;

        var attacker = args.User;
        var weapon = args.Using.Value;
        var victim = args.Target;

        if (HasComp<PacifismAllowedGunComponent>(weapon)
            || !CanExecuteWithGun(weapon, victim, attacker))
            return;

        UtilityVerb verb = new()
        {
            Act = () => TryStartGunExecutionDoafter(weapon, victim, attacker),
            Impact = LogImpact.High,
            Text = Loc.GetString("execution-verb-name"),
            Message = Loc.GetString("execution-verb-message"),
        };

        args.Verbs.Add(verb);
    }

    private bool CanExecuteWithGun(EntityUid weapon, EntityUid victim, EntityUid user)
    {
        if (!_execution.CanBeExecuted(victim, user)
            || TryComp<GunComponent>(weapon, out var gun)
            && !_gunSystem.CanShoot(gun))
            return false;

        return true;
    }

    private void TryStartGunExecutionDoafter(EntityUid weapon, EntityUid victim, EntityUid attacker)
    {
        if (!CanExecuteWithGun(weapon, victim, attacker))
            return;

        if (attacker == victim)
        {
            _execution.ShowExecutionInternalPopup("suicide-popup-gun-initial-internal", attacker, victim, weapon);
            _execution.ShowExecutionExternalPopup("suicide-popup-gun-initial-external", attacker, victim, weapon);
        }
        else
        {
            _execution.ShowExecutionInternalPopup("execution-popup-gun-initial-internal", attacker, victim, weapon);
            _execution.ShowExecutionExternalPopup("execution-popup-gun-initial-external", attacker, victim, weapon);
        }

        var doAfter =
            new DoAfterArgs(EntityManager, attacker, GunExecutionTime, new ExecutionDoAfterEvent(), weapon, target: victim, used: weapon)
            {
                BreakOnMove = true,
                BreakOnDamage = true,
                NeedHand = true,
            };

        _doAfter.TryStartDoAfter(doAfter);
    }

    private string GetDamage(DamageSpecifier damage, string? mainDamageType)
    {
        // Default fallback if nothing valid found
        mainDamageType ??= "Blunt";

        if (damage == null || damage.DamageDict.Count == 0)
            return mainDamageType;

        var filtered = damage.DamageDict
            .Where(kv => !string.Equals(kv.Key, "Structural", StringComparison.OrdinalIgnoreCase));

        if (filtered.Any())
            mainDamageType = filtered.Aggregate((a, b) => a.Value > b.Value ? a : b).Key;

        return mainDamageType ?? "Blunt";
    }

    private void OnDoafterGun(EntityUid uid, GunComponent component, ExecutionDoAfterEvent args)
    {
        if (args.Handled
            || args.Cancelled
            || args.Used == null
            || args.Target == null
            || !_timing.IsFirstTimePredicted
            || !TryComp<GunComponent>(uid, out _))
            return;

        var attacker = args.User;
        var victim = args.Target.Value;
        var weapon = args.Used.Value;

        // Get the direction for the recoil
        var direction = Vector2.Zero;
        var attackerXform = Transform(attacker);
        var victimXform = Transform(victim);
        var diff = victimXform.WorldPosition - attackerXform.WorldPosition;
        if (diff != Vector2.Zero)
            direction = -diff.Normalized(); // recoil opposite of shot

        if (!CanExecuteWithGun(weapon, victim, attacker)
            || !TryComp<DamageableComponent>(victim, out var damageableComponent))
            return;

        // Take some ammunition for the shot (one bullet)
        var fromCoordinates = Transform(attacker).Coordinates;
        var ev = new TakeAmmoEvent(1, new List<(EntityUid? Entity, IShootable Shootable)>(), fromCoordinates, attacker);
        RaiseLocalEvent(weapon, ev);

        // Check if there's any ammo left
        if (ev.Ammo.Count <= 0)
        {
            _audio.PlayPredicted(component.SoundEmpty, uid, attacker);
            _execution.ShowExecutionInternalPopup("execution-popup-gun-empty", attacker, victim, weapon);
            _execution.ShowExecutionExternalPopup("execution-popup-gun-empty", attacker, victim, weapon);
            return;
        }

        var damage = new DamageSpecifier();
        string? mainDamageType = null;
        // Get some information from IShootable
        var ammoUid = ev.Ammo[0].Entity;

        switch (ev.Ammo[0].Shootable)
        {
            case CartridgeAmmoComponent cartridge:
            {
                if (cartridge.Spent) // cant use a spent cartridge
                {
                    _audio.PlayPredicted(component.SoundEmpty, uid, attacker);
                    _execution.ShowExecutionInternalPopup("execution-popup-gun-empty", attacker, victim, weapon);
                    _execution.ShowExecutionExternalPopup("execution-popup-gun-empty", attacker, victim, weapon);
                    return;
                }

                var prototype = _prototypeManager.Index<EntityPrototype>(cartridge.Prototype);

                if (prototype.TryGetComponent<ProjectileComponent>(out var projectileA, _componentFactory))
                    mainDamageType = GetDamage(projectileA.Damage, mainDamageType);
                else if (prototype.TryGetComponent<HitscanBasicDamageComponent>(out var hitscanA, _componentFactory))
                    mainDamageType = GetDamage(hitscanA.Damage, mainDamageType);

                cartridge.Spent = true; // Expend the cartridge
                _appearanceSystem.SetData(ammoUid!.Value, AmmoVisuals.Spent, true);
                Dirty(ammoUid.Value, cartridge);
                break;
            }
            case AmmoComponent: // This stops revolvers from hitting the user while executing someone, somehow
                if (TryComp<ProjectileComponent>(ammoUid, out var projectileB))
                    mainDamageType = GetDamage(projectileB.Damage, mainDamageType);
                else if (TryComp<HitscanBasicDamageComponent>(ammoUid, out var hitscanB))
                    mainDamageType = GetDamage(hitscanB.Damage, mainDamageType);

                if (ammoUid != null)
                    Del(ammoUid.Value);
                break;
        }

        var prev = _combat.IsInCombatMode(attacker);
        _combat.SetInCombatMode(attacker, true);

        if (attacker == victim)
        {
            _execution.ShowExecutionInternalPopup("suicide-popup-gun-complete-internal", attacker, victim, weapon);
            _execution.ShowExecutionExternalPopup("suicide-popup-gun-complete-external", attacker, victim, weapon);
        }
        else
        {
            if (_net.IsClient && direction != Vector2.Zero && _timing.IsFirstTimePredicted) // Just apply recoil for the client
                _recoil.KickCamera(attacker, direction);
            _execution.ShowExecutionInternalPopup("execution-popup-gun-complete-internal", attacker, victim, weapon);
            _execution.ShowExecutionExternalPopup("execution-popup-gun-complete-external", attacker, victim, weapon);
        }

        _audio.PlayPredicted(component.SoundGunshot, uid, attacker);
        _suicide.ApplyLethalDamage((victim, damageableComponent), mainDamageType);

        _combat.SetInCombatMode(attacker, prev);
        args.Handled = true;
    }
}
