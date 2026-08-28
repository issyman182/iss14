using Content.Shared.Camera;
using Content.Shared.Eye.Blinding.Components;
using Content.Shared.Inventory;
using Content.Shared.Rejuvenate;
using JetBrains.Annotations;

// Shitmed Change
using Content.Shared.Body.Systems;
using Content.Shared.Body.Components;
using Content.Shared._Shitmed.Body.Organ;
using Content.Shared._Shitmed.Medical.Surgery.Traumas.Systems;

namespace Content.Shared.Eye.Blinding.Systems;

public sealed partial class BlindableSystem : EntitySystem
{
    [Dependency] private BlurryVisionSystem _blurriness = default!;
    [Dependency] private EyeClosingSystem _eyelids = default!;
    [Dependency] private SharedBodySystem _body = default!; // Shitmed Change
    [Dependency] private TraumaSystem _trauma = default!; // Shitmed Change

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<BlindableComponent, RejuvenateEvent>(OnRejuvenate);
        SubscribeLocalEvent<BlindableComponent, EyeDamageChangedEvent>(OnDamageChanged);
        SubscribeLocalEvent<BlindableComponent, GetEyePvsScaleAttemptEvent>(OnGetEyePvsScaleAttemptEvent);
        SubscribeLocalEvent<BlindableComponent, GetEyeOffsetAttemptEvent>(OnGetEyeOffsetAttemptEvent);
    }

    private void OnRejuvenate(Entity<BlindableComponent> ent, ref RejuvenateEvent args)
    {
        AdjustEyeDamage((ent.Owner, ent.Comp), -ent.Comp.EyeDamage);
    }

    private void OnDamageChanged(Entity<BlindableComponent> ent, ref EyeDamageChangedEvent args)
    {
        _blurriness.UpdateBlurMagnitude((ent.Owner, ent.Comp));
        _eyelids.UpdateEyesClosable((ent.Owner, ent.Comp));
    }

    private void OnGetEyePvsScaleAttemptEvent(Entity<BlindableComponent> ent, ref GetEyePvsScaleAttemptEvent args)
    {
        if (ent.Comp.IsBlind)
            args.Cancelled = true;
    }

    private void OnGetEyeOffsetAttemptEvent(Entity<BlindableComponent> ent, ref GetEyeOffsetAttemptEvent args)
    {
        if (ent.Comp.IsBlind)
            args.Cancelled = true;
    }

    /// <summary>
    /// Checks and updates a blindable is blind state according to damage and other components to the entity.
    /// </summary>
    /// <param name="blindable">The entity to update.</param>
    [PublicAPI]
    public void UpdateIsBlind(Entity<BlindableComponent?> blindable)
    {
        if (!Resolve(blindable, ref blindable.Comp, false))
            return;

        var old = blindable.Comp.IsBlind;

        // Don't bother raising an event if the eye is too damaged.
        if (blindable.Comp.EyeDamage >= blindable.Comp.MaxDamage)
        {
            blindable.Comp.IsBlind = true;
        }
        else
        {
            var ev = new CanSeeAttemptEvent();
            RaiseLocalEvent(blindable.Owner, ev);
            blindable.Comp.IsBlind = ev.Blind;
        }

        if (old == blindable.Comp.IsBlind)
            return;

        var changeEv = new BlindnessChangedEvent(blindable.Comp.IsBlind);
        RaiseLocalEvent(blindable.Owner, ref changeEv);
        Dirty(blindable);
    }

    // Shitmed Change Start
    /// <summary>
    /// Adjust eye damage and updates the relevant sub components.
    /// </summary>
    /// <param name="blindable">Entity to adjust for.</param>
    /// <param name="amount">How much to change the eye damage. Can be positive and negative.</param>
    public void AdjustEyeDamage(Entity<BlindableComponent?> blindable, int amount)
    {
        if (!Resolve(blindable, ref blindable.Comp, false) || amount == 0)
            return;

        blindable.Comp.EyeDamage += amount;
        UpdateEyeDamage(blindable, true);

        // If the entity has eye organs, then we also damage those.
        if (!TryComp(blindable, out BodyComponent? body)
            || !_body.TryGetBodyOrganEntityComps<EyesComponent>((blindable, body), out var eyes))
            return;

        // iss14: mirror the current eye damage into the organ modifier instead of only creating it
        // once - repeated adjustments (and heals, e.g. oculine) never updated the old modifier.
        foreach (var eye in eyes)
        {
            if (blindable.Comp.EyeDamage <= 0)
                _trauma.TryRemoveOrganDamageModifier(eye.Owner, blindable.Owner, "BlindableDamage", eye.Comp2);
            else
                _trauma.TrySetOrganDamageModifier(eye.Owner, blindable.Comp.EyeDamage, blindable.Owner, "BlindableDamage", eye.Comp2);
        }
    }

    // Alternative version of the method intended to be used with Eye Organs, so that you can just pass in
    // the severity and set that.
    public void SetEyeDamage(Entity<BlindableComponent?> blindable, int amount)
    {
        if (!Resolve(blindable, ref blindable.Comp, false))
            return;
        blindable.Comp.EyeDamage = amount;
        UpdateEyeDamage(blindable, true);
    }
    // Shitmed Change End


    private void UpdateEyeDamage(Entity<BlindableComponent?> blindable, bool isDamageChanged)
    {
        if (!Resolve(blindable, ref blindable.Comp, false))
            return;

        var previousDamage = blindable.Comp.EyeDamage;
        blindable.Comp.EyeDamage = Math.Clamp(blindable.Comp.EyeDamage, blindable.Comp.MinDamage, blindable.Comp.MaxDamage);
        Dirty(blindable);
        if (!isDamageChanged && previousDamage == blindable.Comp.EyeDamage)
            return;

        UpdateIsBlind(blindable);
        var ev = new EyeDamageChangedEvent(blindable.Comp.EyeDamage);
        RaiseLocalEvent(blindable.Owner, ref ev);
    }

    /// <summary>
    /// Sets the minimum damage to an eye.
    /// Updates also sub components.
    /// </summary>
    /// <param name="blindable">The entity to update.</param>
    /// <param name="amount">The minimum amount the entity can be blinded.</param>
    public void SetMinDamage(Entity<BlindableComponent?> blindable, int amount)
    {
        if (!Resolve(blindable, ref blindable.Comp, false))
            return;

        blindable.Comp.MinDamage = amount;
        UpdateEyeDamage(blindable, false);
    }
}

/// <summary>
///     This event is raised when an entity's blindness changes
/// </summary>
[ByRefEvent]
public record struct BlindnessChangedEvent(bool Blind);

/// <summary>
///     This event is raised when an entity's eye damage changes
/// </summary>
[ByRefEvent]
public record struct EyeDamageChangedEvent(int Damage);

/// <summary>
///     Raised directed at an entity to see whether the entity is currently blind or not.
/// </summary>
public sealed class CanSeeAttemptEvent : CancellableEntityEventArgs, IInventoryRelayEvent
{
    public bool Blind => Cancelled;
    public SlotFlags TargetSlots => SlotFlags.EYES | SlotFlags.MASK | SlotFlags.HEAD;
}

public sealed class GetEyeProtectionEvent : EntityEventArgs, IInventoryRelayEvent
{
    /// <summary>
    ///     Time to subtract from any temporary blindness sources.
    /// </summary>
    public TimeSpan Protection;

    public SlotFlags TargetSlots => SlotFlags.EYES | SlotFlags.MASK | SlotFlags.HEAD;
}
