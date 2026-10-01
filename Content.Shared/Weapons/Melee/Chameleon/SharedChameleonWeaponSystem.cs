// iss14: Cybersun MIMIC-5
using System.Linq;
using Content.Shared.Clothing.Components;
using Content.Shared.Contraband;
using Content.Shared.FixedPoint;
using Content.Shared.Interaction.Events;
using Content.Shared.Inventory;
using Content.Shared.Item;
using Content.Shared.Tag;
using Content.Shared.Verbs;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Shared.Weapons.Melee.Chameleon;

/// <summary>
///     Shared half of the chameleon weapon. Discovers which weapon prototypes are valid disguises, validates
///     selections and applies the cosmetic copy (sprite, in-hand visuals, name, description, contraband flag).
///     The server is authoritative: only <see cref="SetSelectedPrototype"/> on the server changes
///     <see cref="ChameleonWeaponComponent.Selected"/>; clients re-derive visuals from the networked id.
/// </summary>
public abstract partial class SharedChameleonWeaponSystem : EntitySystem
{
    [Dependency] private ContrabandSystem _contraband = default!;
    [Dependency] private MetaDataSystem _metaData = default!;
    [Dependency] private SharedItemSystem _itemSystem = default!;
    [Dependency] private TagSystem _tag = default!;
    [Dependency] protected SharedUserInterfaceSystem UI = default!;

    /// <summary>
    ///     Prototypes tagged with this are never offered as a disguise, even if they would otherwise qualify.
    /// </summary>
    public static readonly ProtoId<TagPrototype> BlacklistTag = "ChameleonWeaponBlacklist";

    /// <summary>
    ///     Cached list of valid disguise targets, rebuilt on prototype reload.
    /// </summary>
    private readonly List<EntProtoId> _validTargets = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ChameleonWeaponComponent, GetVerbsEvent<InteractionVerb>>(OnVerb);
        SubscribeLocalEvent<ChameleonWeaponComponent, UseInHandEvent>(OnUseInHand);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);

        foreach (var name in WeaponMarkerComponents.Concat(ExcludedComponents))
        {
            if (!Factory.TryGetRegistration(name, out _))
                Log.Warning($"Chameleon weapon filter references unknown component '{name}'");
        }

        PrepareAllVariants();
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (args.WasModified<EntityPrototype>())
            PrepareAllVariants();
    }

    private void OnVerb(Entity<ChameleonWeaponComponent> ent, ref GetVerbsEvent<InteractionVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || !ent.Comp.ShowVerb)
            return;

        // Can't pass args from a ref event inside of lambdas
        var user = args.User;

        args.Verbs.Add(new InteractionVerb
        {
            Text = Loc.GetString("chameleon-weapon-verb-text"),
            Icon = new SpriteSpecifier.Texture(new("/Textures/Interface/VerbIcons/settings.svg.192dpi.png")),
            Act = () => UI.TryToggleUi(ent.Owner, ChameleonWeaponUiKey.Key, user),
        });
    }

    private void OnUseInHand(Entity<ChameleonWeaponComponent> ent, ref UseInHandEvent args)
    {
        if (args.Handled || !ent.Comp.OpenOnUseInHand)
            return;

        args.Handled = UI.TryToggleUi(ent.Owner, ChameleonWeaponUiKey.Key, args.User);
    }

    /// <summary>
    ///     Applies the cosmetic parts of the selected prototype to the weapon: name, description, item in-hand
    ///     visuals, contraband status and (client side, via <see cref="UpdateSprite"/>) the world sprite.
    ///     Runs on the server after a selection and on every client after the component state arrives, so both
    ///     sides derive the exact same visuals from the networked id.
    /// </summary>
    protected void UpdateVisuals(EntityUid uid, ChameleonWeaponComponent component)
    {
        if (component.Selected is not { } selected ||
            !ProtoMan.Resolve(selected, out EntityPrototype? proto))
            return;

        // world sprite icon
        UpdateSprite(uid, proto);

        // name and description so examine shows the disguise
        var meta = MetaData(uid);
        _metaData.SetEntityName(uid, proto.Name, meta);
        _metaData.SetEntityDescription(uid, proto.Description, meta);

        // in-hand sprite logic (rsi path, inhand visuals, held prefix). Item size is deliberately NOT copied.
        if (TryComp(uid, out ItemComponent? item) &&
            proto.TryComp(out ItemComponent? otherItem, Factory))
        {
            _itemSystem.CopyVisuals(uid, otherItem, item);
        }

        // properly mark contraband, so examining the disguise is consistent with what it looks like
        if (proto.TryComp(out ContrabandComponent? contra, Factory))
        {
            EnsureComp<ContrabandComponent>(uid, out var current);
            _contraband.CopyDetails(uid, contra, current);
        }
        else
        {
            RemComp<ContrabandComponent>(uid);
        }
    }

    /// <summary>
    ///     Client side: copies the target prototype's sprite onto the weapon.
    /// </summary>
    protected virtual void UpdateSprite(EntityUid uid, EntityPrototype proto) { }

    /// <summary>
    ///     Melee items with at least this much base damage count as a weapon even without a weapon marker component.
    ///     Almost every item in SS14 carries a <see cref="MeleeWeaponComponent"/> for improvised hits (books, beakers,
    ///     tanks...), so the component alone says nothing; real melee weapons start around knife damage.
    /// </summary>
    public const float MinMeleeDamage = 8f;

    /// <summary>
    ///     Components that mark an item as a weapon regardless of its base melee damage:
    ///     guns, toggleable blades (energy swords, chainsaws), stun weapons and flashes.
    /// </summary>
    private static readonly string[] WeaponMarkerComponents =
    {
        "Gun",
        "ItemToggleMeleeWeapon",
        "StaminaDamageOnHit",
        "Flash",
    };

    /// <summary>
    ///     Items that technically pass the weapon checks but could not reasonably be a held weapon:
    ///     living things, bodies, borg/mech parts, containers, tanks, furniture, food, paper, instruments...
    ///     (Tool is deliberately not here: nearly every blade and bat carries a Tool quality.)
    /// </summary>
    private static readonly string[] ExcludedComponents =
    {
        "MobState",
        "Body",
        "BodyPart",
        "BorgModule",
        "MechEquipment",
        "Storage",
        "GasTank",
        "Anchorable",
        "Edible",
        "Paper",
        "Stack",
        "Bible",
        "Instrument",
        "Defibrillator",
    };

    /// <summary>
    ///     Wearable slots a weapon may have (holsters, belts, backs); anything worn elsewhere is clothing, not a weapon.
    /// </summary>
    private const SlotFlags WeaponClothingSlots = SlotFlags.BELT | SlotFlags.BACK | SlotFlags.SUITSTORAGE | SlotFlags.POCKET;

    private static readonly ProtoId<EntityCategoryPrototype> DebugCategory = "Debug";

    /// <summary>
    ///     Check if this entity prototype is a valid disguise for a chameleon weapon:
    ///     a real, spawnable, held item that is recognisably a weapon (gun, toggle/stun weapon or a melee weapon
    ///     with real damage), is not itself a chameleon, is not worn clothing/container/furniture/food and is
    ///     not explicitly blacklisted.
    /// </summary>
    public bool IsValidTarget(EntityPrototype proto)
    {
        if (proto.Abstract || proto.HideSpawnMenu)
            return false;

        // debug weapons ("bang stick 20000dmg") are admin toys, not disguises
        if (proto.Categories.Any(c => c.ID == DebugCategory.Id) ||
            proto.EditorSuffix?.Contains("DEBUG", StringComparison.OrdinalIgnoreCase) == true)
            return false;

        // must be a held item (the server never sees the client-only Sprite component, so Item is the
        // shared proxy for "has a world/in-hand look")
        if (!proto.HasComp<ItemComponent>(Factory))
            return false;

        // no chameleon-ception: the mimic itself, chameleon clothing, projectors, ...
        foreach (var name in proto.Components.Keys)
        {
            if (name.StartsWith("Chameleon", StringComparison.Ordinal))
                return false;
        }

        foreach (var name in ExcludedComponents)
        {
            if (proto.Components.ContainsKey(name))
                return false;
        }

        // wearable clothing (gloves, boots, glasses...) is not a weapon even if it hits hard
        if (proto.TryComp(out ClothingComponent? clothing, Factory) &&
            (clothing.Slots & ~WeaponClothingSlots) != SlotFlags.NONE)
            return false;

        // explicit opt-out
        if (proto.TryComp(out TagComponent? tag, Factory) && _tag.HasTag(tag, BlacklistTag))
            return false;

        // must actually be a weapon
        foreach (var name in WeaponMarkerComponents)
        {
            if (proto.Components.ContainsKey(name))
                return true;
        }

        return proto.TryComp(out MeleeWeaponComponent? melee, Factory) &&
               melee.Damage.GetTotal() >= FixedPoint2.New(MinMeleeDamage);
    }

    /// <summary>
    ///     All prototypes that are valid disguises, cached since the last prototype (re)load.
    /// </summary>
    public IReadOnlyList<EntProtoId> GetValidTargets()
    {
        return _validTargets;
    }

    protected void PrepareAllVariants()
    {
        _validTargets.Clear();

        foreach (var proto in ProtoMan.EnumeratePrototypes<EntityPrototype>())
        {
            if (IsValidTarget(proto))
                _validTargets.Add(proto.ID);
        }

        _validTargets.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
    }

    /// <summary>
    ///     Change the chameleon weapon's name, description and sprite to mimic another entity prototype.
    ///     Only the server implementation does anything; the client receives the result through the component state.
    /// </summary>
    /// <param name="uid">The weapon whose appearance to swap.</param>
    /// <param name="protoId">The target prototype id.</param>
    /// <param name="forceUpdate">Re-apply even if the same prototype is already selected.</param>
    /// <param name="validate">Whether to check the target against <see cref="IsValidTarget"/>.</param>
    /// <param name="component">The weapon's <see cref="ChameleonWeaponComponent"/>.</param>
    /// <returns>True if the selection was accepted and applied.</returns>
    public virtual bool SetSelectedPrototype(EntityUid uid,
        string? protoId,
        bool forceUpdate = false,
        bool validate = true,
        ChameleonWeaponComponent? component = null)
    {
        return false;
    }
}
