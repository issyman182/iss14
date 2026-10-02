// iss14: Economy
#nullable enable
using System.Threading.Tasks;
using Content.IntegrationTests.Fixtures.Attributes;
using Content.IntegrationTests.Tests.Interaction;
using Content.Server.Economy;
using Content.Shared.CCVar;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Economy;
using Content.Shared.Forensics.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Stacks;
using NUnit.Framework;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests.Economy;

/// <summary>
/// ATM: withdrawing spawns a spesos stack and debits the card's account, depositing credits it, the per-round
/// withdrawal limit is enforced, and a fingerprint mismatch is blocked when the CVar is on.
/// </summary>
public sealed class AtmTest : InteractionTest
{
    [SidedDependency(Side.Server)] private readonly IBankManager _bank = null!;
    [SidedDependency(Side.Server)] private readonly AtmSystem _atm = null!;
    [SidedDependency(Side.Server)] private readonly BankCardSystem _cards = null!;
    [SidedDependency(Side.Server)] private readonly ItemSlotsSystem _slots = null!;
    [SidedDependency(Side.Server)] private readonly SharedHandsSystem _hands = null!;

    private const string OwnerPrint = "OWNER-PRINT";

    [Test]
    public async Task WithdrawDepositLimitAndFingerprint()
    {
        await OverrideCVar(Side.Server, CCVars.EconomyMaxBalance, 1_000_000L);
        await OverrideCVar(Side.Server, CCVars.EconomyAtmFeePercent, 0f);
        await OverrideCVar(Side.Server, CCVars.EconomyAtmWithdrawLimitPerRound, 0);
        await OverrideCVar(Side.Server, CCVars.EconomyAtmRequireFingerprint, false);

        var user = ServerSession!.UserId;
        Assert.That(_bank.IsLoaded(user), Is.True);

        await SpawnTarget("ATM");
        var atm = STarget!.Value;
        await SpawnEntity("APCBasic", SEntMan.GetCoordinates(TargetCoords));
        await RunTicks(1);

        // Issue the player a card bound to their account, stamped with the player's fingerprint.
        EntityUid card = default;
        await Server.WaitPost(() =>
        {
            SEntMan.EnsureComponent<FingerprintComponent>(SPlayer).Fingerprint = OwnerPrint;
            card = SEntMan.SpawnAtPosition("PassengerIDCard", SEntMan.GetCoordinates(TargetCoords));
            _cards.Bind(card, user, SPlayer);

            Assert.That(_slots.TryGetSlot(atm, AtmComponent.IdSlotId, out var idSlot));
            Assert.That(_slots.TryInsert(atm, idSlot!, card, SPlayer), "card should fit the ID slot");

            // Known starting balance.
            if (_bank.GetBalance(user) > 0)
                _bank.TryDebit(user, _bank.GetBalance(user), "test reset", out _);
            _bank.Credit(user, 1000, "test seed", out _);
        });
        await RunTicks(1);

        var bankCard = SEntMan.GetComponent<BankCardComponent>(card);
        Assert.Multiple(() =>
        {
            Assert.That(bankCard.OwnerUserId, Is.EqualTo(user));
            Assert.That(bankCard.OwnerFingerprint, Is.EqualTo(OwnerPrint));
            Assert.That(_bank.GetBalance(user), Is.EqualTo(1000));
        });

        // Withdraw 300: cash stack of 300 in hand, balance 700.
        var withdrawn = false;
        await Server.WaitPost(() => withdrawn = _atm.TryWithdraw((atm, SEntMan.GetComponent<AtmComponent>(atm)), SPlayer, 300));
        await RunTicks(1);
        Assert.That(withdrawn, Is.True);
        Assert.That(_bank.GetBalance(user), Is.EqualTo(700));

        var held = _hands.GetActiveItem(SPlayer);
        Assert.That(held, Is.Not.Null, "withdrawn cash should land in the hand");
        var stack = SEntMan.GetComponent<StackComponent>(held!.Value);
        Assert.Multiple(() =>
        {
            Assert.That(stack.StackTypeId.Id, Is.EqualTo("Credit"));
            Assert.That(stack.Count, Is.EqualTo(300));
            Assert.That(_atm.GetWithdrawnThisRound(user), Is.EqualTo(300));
        });

        // Overdraft is refused.
        await Server.WaitPost(() => withdrawn = _atm.TryWithdraw((atm, SEntMan.GetComponent<AtmComponent>(atm)), SPlayer, 701));
        Assert.That(withdrawn, Is.False);
        Assert.That(_bank.GetBalance(user), Is.EqualTo(700));

        // Deposit the 300 back: put the stack in the cash slot, deposit, balance 1000, stack consumed.
        var deposited = false;
        await Server.WaitPost(() =>
        {
            Assert.That(_hands.TryDrop(SPlayer, held.Value));
            Assert.That(_slots.TryGetSlot(atm, AtmComponent.CashSlotId, out var cashSlot));
            Assert.That(_slots.TryInsert(atm, cashSlot!, held.Value, SPlayer), "cash should fit the cash slot");
            deposited = _atm.TryDeposit((atm, SEntMan.GetComponent<AtmComponent>(atm)), SPlayer);
        });
        await RunTicks(3);
        Assert.Multiple(() =>
        {
            Assert.That(deposited, Is.True);
            Assert.That(_bank.GetBalance(user), Is.EqualTo(1000));
            Assert.That(SEntMan.Deleted(held.Value), "deposited cash is destroyed");
        });

        // Per-round limit: 300 already withdrawn, limit 500 -> 300 more is refused, 200 is fine.
        await OverrideCVar(Side.Server, CCVars.EconomyAtmWithdrawLimitPerRound, 500);
        await Server.WaitPost(() => withdrawn = _atm.TryWithdraw((atm, SEntMan.GetComponent<AtmComponent>(atm)), SPlayer, 300));
        Assert.That(withdrawn, Is.False, "over the per-round limit");
        Assert.That(_bank.GetBalance(user), Is.EqualTo(1000));
        await Server.WaitPost(() => withdrawn = _atm.TryWithdraw((atm, SEntMan.GetComponent<AtmComponent>(atm)), SPlayer, 200));
        await RunTicks(1);
        Assert.That(withdrawn, Is.True);
        Assert.That(_bank.GetBalance(user), Is.EqualTo(800));
        Assert.That(_atm.GetWithdrawnThisRound(user), Is.EqualTo(500));
        await Server.WaitPost(() => _hands.TryDrop(SPlayer, _hands.GetActiveItem(SPlayer)!.Value));
        await OverrideCVar(Side.Server, CCVars.EconomyAtmWithdrawLimitPerRound, 0);

        // Fingerprint check: a thief with a different print is refused, the owner is not.
        await OverrideCVar(Side.Server, CCVars.EconomyAtmRequireFingerprint, true);
        await Server.WaitPost(() =>
        {
            SEntMan.GetComponent<FingerprintComponent>(SPlayer).Fingerprint = "THIEF-PRINT";
            Assert.That(_cards.VerifyHolder(card, SPlayer), Is.EqualTo(AtmVerifyResult.Mismatch));
            withdrawn = _atm.TryWithdraw((atm, SEntMan.GetComponent<AtmComponent>(atm)), SPlayer, 100);
        });
        Assert.That(withdrawn, Is.False, "fingerprint mismatch must block the withdrawal");
        Assert.That(_bank.GetBalance(user), Is.EqualTo(800));

        await Server.WaitPost(() =>
        {
            SEntMan.GetComponent<FingerprintComponent>(SPlayer).Fingerprint = OwnerPrint;
            Assert.That(_cards.VerifyHolder(card, SPlayer), Is.EqualTo(AtmVerifyResult.Match));
            withdrawn = _atm.TryWithdraw((atm, SEntMan.GetComponent<AtmComponent>(atm)), SPlayer, 100);
        });
        await RunTicks(1);
        Assert.That(withdrawn, Is.True);
        Assert.That(_bank.GetBalance(user), Is.EqualTo(700));

        // Without the CVar the mismatch only matters to "Verify holder", not to the money.
        await OverrideCVar(Side.Server, CCVars.EconomyAtmRequireFingerprint, false);
        await Server.WaitPost(() =>
        {
            _hands.TryDrop(SPlayer, _hands.GetActiveItem(SPlayer)!.Value);
            SEntMan.GetComponent<FingerprintComponent>(SPlayer).Fingerprint = "THIEF-PRINT";
            withdrawn = _atm.TryWithdraw((atm, SEntMan.GetComponent<AtmComponent>(atm)), SPlayer, 100);
        });
        await RunTicks(1);
        Assert.That(withdrawn, Is.True, "a stolen card works when fingerprints are not required");
        Assert.That(_bank.GetBalance(user), Is.EqualTo(600));

        // Flush the write queue so the pool's database is consistent for the next test.
        await _bank.FlushAsync();
    }
}
