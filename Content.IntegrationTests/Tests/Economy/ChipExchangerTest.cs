// iss14: Economy
#nullable enable
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server.Economy;
using Content.Shared.CCVar;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Economy;
using Content.Shared.Stacks;
using NUnit.Framework;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests.Economy;

/// <summary>Chip exchanger: cash becomes chips and chips become cash at the configured rate, nothing is duplicated.</summary>
public sealed class ChipExchangerTest : InteractionTest
{
    [SidedDependency(Side.Server)] private readonly ChipExchangerSystem _exchanger = null!;
    [SidedDependency(Side.Server)] private readonly ItemSlotsSystem _slots = null!;

    [Test]
    public async Task BothDirections()
    {
        await OverrideCVar(Side.Server, CCVars.EconomyChipRate, 2);

        await SpawnTarget("ChipExchanger");
        var machine = STarget!.Value;
        await SpawnEntity("APCBasic", SEntMan.GetCoordinates(TargetCoords));
        await RunTicks(1);

        var ent = (machine, SEntMan.GetComponent<ChipExchangerComponent>(machine));

        // 25 credits at 2 per chip -> 12 chips, 1 credit left in the cash slot.
        EntityUid cash = default;
        var ok = false;
        await Server.WaitPost(() =>
        {
            cash = SEntMan.SpawnAtPosition("SpaceCash", SEntMan.GetCoordinates(TargetCoords));
            Stack.SetCount((cash, null), 25);
            Assert.That(_slots.TryGetSlot(machine, ChipExchangerComponent.CashSlotId, out var cashSlot));
            Assert.That(_slots.TryInsert(machine, cashSlot!, cash, SPlayer));
            ok = _exchanger.TryCashToChips(ent, SPlayer);
        });
        await RunTicks(3);

        Assert.That(ok, Is.True);
        Assert.That(_slots.TryGetSlot(machine, ChipExchangerComponent.ChipSlotId, out var chipSlot));
        Assert.That(chipSlot!.Item, Is.Not.Null, "chips should be in the chip slot");
        var chips = SEntMan.GetComponent<StackComponent>(chipSlot.Item!.Value);
        Assert.Multiple(() =>
        {
            Assert.That(chips.StackTypeId.Id, Is.EqualTo("PokerChips"));
            Assert.That(chips.Count, Is.EqualTo(12));
            Assert.That(SEntMan.GetComponent<StackComponent>(cash).Count, Is.EqualTo(1), "remainder stays as cash");
        });

        // Clear the cash slot, then convert the 12 chips back -> 24 credits, chips destroyed.
        var chipEnt = chipSlot.Item.Value;
        await Server.WaitPost(() =>
        {
            Assert.That(_slots.TryGetSlot(machine, ChipExchangerComponent.CashSlotId, out var cashSlot));
            Assert.That(_slots.TryEject(machine, cashSlot!, SPlayer, out _));
            SEntMan.DeleteEntity(cash);
            ok = _exchanger.TryChipsToCash(ent, SPlayer);
        });
        await RunTicks(3);

        Assert.That(ok, Is.True);
        Assert.That(_slots.TryGetSlot(machine, ChipExchangerComponent.CashSlotId, out var outSlot));
        Assert.That(outSlot!.Item, Is.Not.Null, "cash should be in the cash slot");
        var payout = SEntMan.GetComponent<StackComponent>(outSlot.Item!.Value);
        Assert.Multiple(() =>
        {
            Assert.That(payout.StackTypeId.Id, Is.EqualTo("Credit"));
            Assert.That(payout.Count, Is.EqualTo(24));
            Assert.That(SEntMan.Deleted(chipEnt), "exchanged chips are destroyed");
        });

        // Empty slots: refused.
        await Server.WaitPost(() => ok = _exchanger.TryChipsToCash(ent, SPlayer));
        Assert.That(ok, Is.False);
    }
}
