// iss14: Cybersun MIMIC-5
#nullable enable
using System.Linq;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server.Weapons.Melee.Chameleon;
using Content.Shared.Damage.Prototypes;
using Content.Shared.FixedPoint;
using Content.Shared.Store;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Melee.Chameleon;
using Content.Shared.Weapons.Ranged;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Client.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.Weapons;

/// <summary>
/// iss14: the Cybersun MIMIC-5 chameleon weapon must only ever change its looks.
/// </summary>
[TestOf(typeof(ChameleonWeaponComponent))]
public sealed class ChameleonWeaponTest : InteractionTest
{
    protected override string PlayerPrototype => "MobHuman";

    private const string Mimic = "WeaponChameleonMimic";
    private const string Shotgun = "WeaponShotgunKammerer";
    private const string Listing = "UplinkChameleonMimic";

    private static readonly ProtoId<DamageTypePrototype> Blunt = "Blunt";
    private static readonly ProtoId<StoreCategoryPrototype> DeceptionCategory = "UplinkDeception";

    private static readonly string[] InvalidTargets =
    {
        Mimic, // itself
        "MobHuman", // a mob
        "WallSolid", // a structure
        "Paper", // an item that is not a weapon
        "DefinitelyNotARealPrototypeId",
    };

    private void AssertIsPlainMimic(EntityUid uid, string context)
    {
        Assert.Multiple(() =>
        {
            Assert.That(SEntMan.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID, Is.EqualTo(Mimic),
                $"{context}: entity prototype identity changed");

            Assert.That(SEntMan.TryGetComponent(uid, out MeleeWeaponComponent? melee), $"{context}: lost MeleeWeapon");
            Assert.That(melee!.Damage.DamageDict.Keys.Select(k => k.Id), Is.EquivalentTo(new[] { Blunt.Id }),
                $"{context}: damage types changed");
            Assert.That(melee.Damage.DamageDict[Blunt], Is.EqualTo(FixedPoint2.New(5)),
                $"{context}: blunt damage is not 5");

            Assert.That(SEntMan.HasComponent<GunComponent>(uid), Is.False, $"{context}: gained Gun");
            Assert.That(SEntMan.HasComponent<BallisticAmmoProviderComponent>(uid), Is.False,
                $"{context}: gained BallisticAmmoProvider");
            Assert.That(SEntMan.HasComponent<MagazineAmmoProviderComponent>(uid), Is.False,
                $"{context}: gained MagazineAmmoProvider");
            Assert.That(SEntMan.HasComponent<ChamberMagazineAmmoProviderComponent>(uid), Is.False,
                $"{context}: gained ChamberMagazineAmmoProvider");
            Assert.That(SEntMan.HasComponent<GunRequiresWieldComponent>(uid), Is.False,
                $"{context}: gained GunRequiresWield");
            Assert.That(SEntMan.HasComponent<ChameleonWeaponComponent>(uid), $"{context}: lost ChameleonWeapon");
        });
    }

    /// <summary>
    /// (a) The MIMIC-5 prototype deals exactly 5 blunt damage and nothing else.
    /// </summary>
    [Test]
    public async Task PrototypeDealsFiveBlunt()
    {
        var mimic = await PlaceInHands(Mimic);
        var uid = ToServer(mimic);

        await Server.WaitAssertion(() =>
        {
            AssertIsPlainMimic(uid, "fresh spawn");
            Assert.That(SComp<ChameleonWeaponComponent>(uid).Selected, Is.Null, "fresh MIMIC-5 should not be disguised");
        });
    }

    /// <summary>
    /// (b) Disguising as a shotgun changes only the networked selection and the cosmetic data;
    /// no gun/ammo components appear, damage stays 5 blunt and the holder cannot fire it.
    /// </summary>
    [Test]
    public async Task ShotgunDisguiseIsCosmeticOnly()
    {
        var mimic = await PlaceInHands(Mimic);
        var uid = ToServer(mimic);
        var system = SEntMan.System<ChameleonWeaponSystem>();

        await Server.WaitAssertion(() =>
        {
            var shotgunProto = SProtoMan.Index<EntityPrototype>(Shotgun);
            Assert.That(system.IsValidTarget(shotgunProto), $"{Shotgun} should be a valid disguise");
            Assert.That(system.SetSelectedPrototype(uid, Shotgun), "selecting a shotgun disguise was rejected");

            var comp = SComp<ChameleonWeaponComponent>(uid);
            Assert.That(comp.Selected, Is.EqualTo(new EntProtoId(Shotgun)));

            var meta = SEntMan.GetComponent<MetaDataComponent>(uid);
            Assert.That(meta.EntityName, Is.EqualTo(shotgunProto.Name), "name was not copied from the disguise");
            Assert.That(meta.EntityDescription, Is.EqualTo(shotgunProto.Description), "description was not copied");

            AssertIsPlainMimic(uid, "disguised as shotgun");
        });

        await RunTicks(5);

        await Server.WaitAssertion(() =>
        {
            AssertIsPlainMimic(uid, "disguised as shotgun (later)");
            Assert.That(SGun.TryGetGun(SPlayer, out _), Is.False, "the disguised MIMIC-5 counts as a gun");
        });
    }

    /// <summary>
    /// (c) Mobs, walls, non-weapons, unknown ids and the MIMIC-5 itself are rejected by the server.
    /// </summary>
    [Test]
    public async Task InvalidDisguisesAreRejected()
    {
        var mimic = await PlaceInHands(Mimic);
        var uid = ToServer(mimic);
        var system = SEntMan.System<ChameleonWeaponSystem>();

        await Server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                foreach (var id in InvalidTargets)
                {
                    if (SProtoMan.TryIndex<EntityPrototype>(id, out var proto))
                        Assert.That(system.IsValidTarget(proto), Is.False, $"{id} should not be a valid disguise");

                    Assert.That(system.SetSelectedPrototype(uid, id), Is.False, $"{id} was accepted as a disguise");
                    Assert.That(SComp<ChameleonWeaponComponent>(uid).Selected, Is.Null, $"{id} changed the selection");
                }

                // the cached list must agree with the validator and never contain the mimic
                var targets = system.GetValidTargets();
                Assert.That(targets, Is.Not.Empty, "no valid disguises at all");
                Assert.That(targets.Select(t => t.Id), Does.Not.Contain(Mimic));
                foreach (var target in targets)
                {
                    Assert.That(system.IsValidTarget(SProtoMan.Index(target)), $"{target} is cached but not valid");
                }
            });

            AssertIsPlainMimic(uid, "after rejected disguises");
        });
    }

    /// <summary>
    /// (d) The connected client receives the selected id and derives the disguise sprite from it.
    /// </summary>
    [Test]
    public async Task ClientSeesDisguise()
    {
        var mimic = await PlaceInHands(Mimic);
        var uid = ToServer(mimic);
        var system = SEntMan.System<ChameleonWeaponSystem>();

        await Server.WaitAssertion(() =>
        {
            Assert.That(system.SetSelectedPrototype(uid, Shotgun));
        });

        await RunTicks(10);

        var cuid = ToClient(mimic);
        await Client.WaitAssertion(() =>
        {
            var comp = CComp<ChameleonWeaponComponent>(cuid);
            Assert.That(comp.Selected, Is.EqualTo(new EntProtoId(Shotgun)), "client did not receive the selection");

            var shotgunProto = CProtoMan.Index<EntityPrototype>(Shotgun);
            Assert.That(shotgunProto.TryComp(out SpriteComponent? protoSprite, CEntMan.ComponentFactory));
            var sprite = CComp<SpriteComponent>(cuid);
            Assert.That(sprite.BaseRSI, Is.Not.Null, "disguised sprite has no base RSI");
            Assert.That(sprite.BaseRSI!.Path, Is.EqualTo(protoSprite!.BaseRSI!.Path),
                "client sprite does not use the disguise's RSI");

            var meta = CEntMan.GetComponent<MetaDataComponent>(cuid);
            Assert.That(meta.EntityName, Is.EqualTo(shotgunProto.Name), "client name was not updated");
            Assert.That(meta.EntityPrototype?.ID, Is.EqualTo(Mimic), "client prototype identity changed");

            Assert.That(CEntMan.HasComponent<GunComponent>(cuid), Is.False, "client entity gained Gun");
        });
    }

    /// <summary>
    /// (e) The uplink has the listing in the deception category and it spawns a MIMIC-5.
    /// </summary>
    [Test]
    public async Task UplinkListingSpawnsMimic()
    {
        await Server.WaitAssertion(() =>
        {
            Assert.That(SProtoMan.TryIndex<ListingPrototype>(Listing, out var listing), $"missing listing {Listing}");
            Assert.That(listing!.Categories, Does.Contain(DeceptionCategory));
            Assert.That(listing.ProductEntity, Is.EqualTo(new EntProtoId(Mimic)));
            Assert.That(listing.Cost.Values.Sum(v => v.Double()), Is.GreaterThan(0), "listing is free");

            var product = SSpawn(listing.ProductEntity!.Value.Id);
            Assert.That(SEntMan.HasComponent<ChameleonWeaponComponent>(product));
            AssertIsPlainMimic(product, "uplink product");
        });
    }
}
