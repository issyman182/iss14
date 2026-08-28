// iss14: organ damage from ingested/inhaled chemicals (alcohol -> liver, smoking -> lungs, ...).

using System.Linq;
using Content.Shared._Shitmed.Medical.Surgery.Traumas.Systems;
using Content.Shared.Body.Components;
using Content.Shared.Body.Organ;
using Content.Shared.Body.Systems;
using Content.Shared.FixedPoint;
using Robust.Shared.Prototypes;

namespace Content.Shared.EntityEffects.Effects;

/// <summary>
/// Damages (or with a negative amount, heals) the integrity of the body's organs in the given
/// slots. The damage accumulates in a "ChemicalDamage" trauma modifier per organ, so surgery can
/// treat it like any other organ damage; small amounts also regenerate passively over time via
/// <c>ChemicallyDamagedOrganComponent</c>.
/// </summary>
public sealed partial class ChemicalOrganDamage : EntityEffectBase<ChemicalOrganDamage>
{
    /// <summary>
    /// Organ slot ids to affect, e.g. "liver", "lungs", "heart", "kidneys".
    /// </summary>
    [DataField(required: true)]
    public List<string> Slots = new();

    /// <summary>
    /// Integrity damage applied per metabolism cycle to each matching organ.
    /// </summary>
    [DataField(required: true)]
    public FixedPoint2 Amount;

    public override string EntityEffectGuidebookText(IPrototypeManager prototype, IEntitySystemManager entSys)
        => Loc.GetString("entity-effect-guidebook-chemical-organ-damage",
            ("chance", Probability),
            ("deltasign", MathF.Sign(Amount.Float())),
            ("organs", string.Join(", ", Slots)));
}

/// <inheritdoc cref="ChemicalOrganDamage"/>
public sealed partial class ChemicalOrganDamageEffectSystem : EntityEffectSystem<BodyComponent, ChemicalOrganDamage>
{
    public const string ModifierIdentifier = "ChemicalDamage";

    [Dependency] private SharedBodySystem _body = default!;
    [Dependency] private TraumaSystem _trauma = default!;

    protected override void Effect(Entity<BodyComponent> entity, ref EntityEffectEvent<ChemicalOrganDamage> args)
    {
        var effect = args.Effect;
        var amount = effect.Amount * args.Scale;
        if (amount == FixedPoint2.Zero)
            return;

        foreach (var organ in _body.GetBodyOrgans(entity.Owner, entity.Comp))
        {
            if (!effect.Slots.Contains(organ.Component.SlotId))
                continue;

            // The organ itself owns the modifier so it survives transplants and keeps
            // per-organ bookkeeping simple.
            if (!_trauma.TryChangeOrganDamageModifier(organ.Id, amount, organ.Id, ModifierIdentifier, organ.Component))
                _trauma.TryCreateOrganDamageModifier(organ.Id, amount, organ.Id, ModifierIdentifier, organ.Component);

            // Track (or stop tracking) the organ for passive regeneration.
            if (organ.Component.IntegrityModifiers.TryGetValue((ModifierIdentifier, organ.Id), out var value)
                && value > FixedPoint2.Zero)
            {
                EnsureComp<ChemicallyDamagedOrganComponent>(organ.Id);
            }
            else
            {
                _trauma.TryRemoveOrganDamageModifier(organ.Id, organ.Id, ModifierIdentifier, organ.Component);
                RemComp<ChemicallyDamagedOrganComponent>(organ.Id);
            }
        }
    }
}
