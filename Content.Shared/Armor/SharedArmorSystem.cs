using Content.Shared.Clothing.Components;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.Examine;
using Content.Shared.Inventory;
using Content.Shared.Silicons.Borgs;
using Content.Shared.Verbs;
using Robust.Shared.Utility;

// Shitmed Change
using System.Linq;
using Content.Shared.Body.Part;
using Content.Shared.Body.Systems;
using Content.Shared.Localizations;

namespace Content.Shared.Armor;

/// <summary>
///     This handles logic relating to <see cref="ArmorComponent" />
/// </summary>
public abstract partial class SharedArmorSystem : EntitySystem
{
    [Dependency] private ExamineSystemShared _examine = default!;
    [Dependency] private SharedBodySystem _body = default!; // Shitmed Change

    /// <inheritdoc />
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ArmorComponent, InventoryRelayedEvent<CoefficientQueryEvent>>(OnCoefficientQuery);
        SubscribeLocalEvent<ArmorComponent, InventoryRelayedEvent<DamageModifyEvent>>(OnDamageModify);
        SubscribeLocalEvent<ArmorComponent, BorgModuleRelayedEvent<DamageModifyEvent>>(OnBorgDamageModify);
        SubscribeLocalEvent<ArmorComponent, GetVerbsEvent<ExamineVerb>>(OnArmorVerbExamine);
    }

    /// <summary>
    /// Get the total Damage reduction value of all equipment caught by the relay.
    /// </summary>
    /// <param name="ent">The item that's being relayed to</param>
    /// <param name="args">The event, contains the running count of armor percentage as a coefficient</param>
    private void OnCoefficientQuery(Entity<ArmorComponent> ent, ref InventoryRelayedEvent<CoefficientQueryEvent> args)
    {
        if (TryComp<MaskComponent>(ent, out var mask) && mask.IsToggled)
            return;

        foreach (var armorCoefficient in ent.Comp.Modifiers.Coefficients)
        {
            args.Args.DamageModifiers.Coefficients[armorCoefficient.Key] = args.Args.DamageModifiers.Coefficients.TryGetValue(armorCoefficient.Key, out var coefficient) ? coefficient * armorCoefficient.Value : armorCoefficient.Value;
        }
    }

    private void OnDamageModify(EntityUid uid, ArmorComponent component, InventoryRelayedEvent<DamageModifyEvent> args)
    {
        if (TryComp<MaskComponent>(uid, out var mask) && mask.IsToggled)
            return;

        // Shitmed Change Start - armor only protects the body parts it covers.
        // Armor with no coverage configured protects all parts (upstream clothing yaml sets no
        // coverage), and non-part damage (mobs without complex bodies, e.g. animals wearing
        // armor) is always protected. Goob semantics: armor penetration weakens the modifiers.
        var modifiers = DamageSpecifier.PenetrateArmor(component.Modifiers, args.Args.Damage.ArmorPenetration);

        if (args.Args.TargetPart is not { } targetPart)
        {
            args.Args.Damage = DamageSpecifier.ApplyModifierSet(args.Args.Damage, modifiers);
            return;
        }

        var (partType, _) = _body.ConvertTargetBodyPart(targetPart);

        if (component.CoversPart(partType))
            args.Args.Damage = DamageSpecifier.ApplyModifierSet(args.Args.Damage, modifiers);
        // Shitmed Change End
    }

    private void OnBorgDamageModify(EntityUid uid, ArmorComponent component,
        ref BorgModuleRelayedEvent<DamageModifyEvent> args)
    {
        if (TryComp<MaskComponent>(uid, out var mask) && mask.IsToggled)
            return;

        // Goob semantics: armor penetration applies to borg module armor too.
        args.Args.Damage = DamageSpecifier.ApplyModifierSet(args.Args.Damage,
            DamageSpecifier.PenetrateArmor(component.Modifiers, args.Args.Damage.ArmorPenetration));
    }

    private void OnArmorVerbExamine(EntityUid uid, ArmorComponent component, GetVerbsEvent<ExamineVerb> args)
    {
        if (!args.CanInteract || !args.CanAccess || !component.ShowArmorOnExamine)
            return;

        // Shitmed Change Start
        if (component is { ArmourCoverageHidden: true, ArmourModifiersHidden: true })
            return;

        if (!component.Modifiers.Coefficients.Any() && !component.Modifiers.FlatReductions.Any())
            return;

        var examineMarkup = GetArmorExamine(component);
        // Shitmed Change End

        var ev = new ArmorExamineEvent(examineMarkup);
        RaiseLocalEvent(uid, ref ev);

        _examine.AddDetailedExamineVerb(args, component, examineMarkup,
            Loc.GetString("armor-examinable-verb-text"), "/Textures/Interface/VerbIcons/dot.svg.192dpi.png",
            Loc.GetString("armor-examinable-verb-message"));
    }

    // Shitmed Change: Mostly changed.
    private FormattedMessage GetArmorExamine(ArmorComponent component)
    {
        var msg = new FormattedMessage();
        msg.AddMarkupOrThrow(Loc.GetString("armor-examine"));

        var coverage = component.ArmorCoverage;
        var armorModifiers = component.Modifiers;

        if (!component.ArmourCoverageHidden && coverage.Count > 0) // iss14: empty coverage = protects everywhere, no line to show
        {
            var coveredParts = coverage.Where(coveragePart => coveragePart != BodyPartType.Other).ToList();
            List<string> coverageText = [];
            foreach (var part in coveredParts)
                coverageText.Add(Loc.GetString("armor-coverage-type-" + part.ToString().ToLower()));

            msg.PushNewline();
            msg.AddMarkupOrThrow(Loc.GetString("armor-coverage-value", ("type", ContentLocalizationManager.FormatList(coverageText))));
        }

        if (!component.ArmourModifiersHidden)
        {
            foreach (var coefficientArmor in armorModifiers.Coefficients)
            {
                msg.PushNewline();

                // TODO: probably make these prototype fields or have a test that they all exist
                var armorType = Loc.GetString("armor-damage-type-" + coefficientArmor.Key.Id.ToLower());
                msg.AddMarkupOrThrow(Loc.GetString("armor-coefficient-value",
                    ("type", armorType),
                    ("value", MathF.Round((1f - coefficientArmor.Value) * 100, 1))
                ));
            }

            foreach (var flatArmor in armorModifiers.FlatReductions)
            {
                msg.PushNewline();

                var armorType = Loc.GetString("armor-damage-type-" + flatArmor.Key.Id.ToLower());
                msg.AddMarkupOrThrow(Loc.GetString("armor-reduction-value",
                    ("type", armorType),
                    ("value", flatArmor.Value)
                ));
            }
        }

        return msg;
    }
}
