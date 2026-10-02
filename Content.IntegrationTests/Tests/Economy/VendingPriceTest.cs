// iss14: Economy
#nullable enable
using System.Linq;
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server.Economy;
using Content.Server.VendingMachines;
using Content.Shared.CCVar;
using Content.Shared.Economy;
using Content.Shared.VendingMachines;
using NUnit.Framework;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests.Economy;

/// <summary>
/// Paid vending: free when <c>economy.vending_prices</c> is off, charged from the held ID card's account when on,
/// refused (and nothing vended) without funds.
/// </summary>
public sealed class VendingPriceTest : InteractionTest
{
    private const string VendorProto = "EconomyTestVendingMachine";
    private const string ItemProto = "EconomyTestVendItem";
    private const int ItemPrice = 40;

    [SidedDependency(Side.Server)] private readonly IBankManager _bank = null!;
    [SidedDependency(Side.Server)] private readonly BankCardSystem _cards = null!;
    [SidedDependency(Side.Server)] private readonly VendingMachineSystem _vending = null!;

    [TestPrototypes]
    private const string TestPrototypes = $@"
- type: entity
  parent: BaseItem
  id: {ItemProto}
  name: {ItemProto}
  components:
  - type: StaticPrice
    price: 40

- type: vendingMachineInventory
  id: EconomyTestVendingInventory
  startingInventory:
    {ItemProto}: 5

- type: entity
  id: {VendorProto}
  parent: BaseVendingMachine
  components:
  - type: VendingMachine
    pack: EconomyTestVendingInventory
  - type: VendingMachineEject
    ejectDelay: 0
  - type: Sprite
    sprite: error.rsi
";

    [Test]
    public async Task FreeWhenDisabledChargedWhenEnabled()
    {
        await OverrideCVar(Side.Server, CCVars.EconomyMaxBalance, 1_000_000L);
        await OverrideCVar(Side.Server, CCVars.EconomyVendingPriceMultiplier, 1f);
        await OverrideCVar(Side.Server, CCVars.EconomyVendingPrices, false);

        var user = ServerSession!.UserId;
        await SpawnTarget(VendorProto);
        var vendor = STarget!.Value;
        await SpawnEntity("APCBasic", SEntMan.GetCoordinates(TargetCoords));
        await RunTicks(1);

        var items = _vending.GetAllInventory(vendor);
        Assert.That(items.First().Amount, Is.EqualTo(5));
        Assert.That(_vending.GetVendPrice(ItemProto), Is.EqualTo(ItemPrice));
        Assert.That(items.First().Price, Is.EqualTo(ItemPrice), "price is filled into the networked entry");

        await Server.WaitPost(() =>
        {
            if (_bank.GetBalance(user) > 0)
                _bank.TryDebit(user, _bank.GetBalance(user), "test reset", out _);
            _bank.Credit(user, 50, "test seed", out _);
        });

        // Disabled: free, no card needed.
        await Activate();
        await SendBui(VendingMachineUiKey.Key, new VendingMachineEjectMessage(InventoryType.Regular, ItemProto));
        Assert.Multiple(() =>
        {
            Assert.That(items.First().Amount, Is.EqualTo(4), "vend should succeed for free");
            Assert.That(_bank.GetBalance(user), Is.EqualTo(50));
        });

        // Enabled without a card: refused, nothing vended.
        await OverrideCVar(Side.Server, CCVars.EconomyVendingPrices, true);
        await SendBui(VendingMachineUiKey.Key, new VendingMachineEjectMessage(InventoryType.Regular, ItemProto));
        Assert.Multiple(() =>
        {
            Assert.That(items.First().Amount, Is.EqualTo(4), "no card: nothing vended");
            Assert.That(_bank.GetBalance(user), Is.EqualTo(50));
        });

        // Enabled with a bound card in hand: charged.
        var card = await PlaceInHands("PassengerIDCard");
        await Server.WaitPost(() => _cards.Bind(SEntMan.GetEntity(card), user, SPlayer));
        await SendBui(VendingMachineUiKey.Key, new VendingMachineEjectMessage(InventoryType.Regular, ItemProto));
        Assert.Multiple(() =>
        {
            Assert.That(items.First().Amount, Is.EqualTo(3), "vend should succeed when paid");
            Assert.That(_bank.GetBalance(user), Is.EqualTo(50 - ItemPrice));
        });

        // Not enough left for another one: refused.
        await SendBui(VendingMachineUiKey.Key, new VendingMachineEjectMessage(InventoryType.Regular, ItemProto));
        Assert.Multiple(() =>
        {
            Assert.That(items.First().Amount, Is.EqualTo(3), "insufficient funds: nothing vended");
            Assert.That(_bank.GetBalance(user), Is.EqualTo(50 - ItemPrice));
        });

        // Multiplier changes the price: 40 * 0.1 = 4 -> affordable.
        await OverrideCVar(Side.Server, CCVars.EconomyVendingPriceMultiplier, 0.1f);
        Assert.That(_vending.GetVendPrice(ItemProto), Is.EqualTo(EconomyPricing.VendPrice(ItemPrice, 0.1f)));
        await SendBui(VendingMachineUiKey.Key, new VendingMachineEjectMessage(InventoryType.Regular, ItemProto));
        Assert.Multiple(() =>
        {
            Assert.That(items.First().Amount, Is.EqualTo(2));
            Assert.That(_bank.GetBalance(user), Is.EqualTo(50 - ItemPrice - 4));
        });

        await _bank.FlushAsync();
    }
}
