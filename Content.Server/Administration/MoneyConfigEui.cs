// iss14: Economy
using System.Globalization;
using Content.Server.Administration.Managers;
using Content.Server.EUI;
using Content.Shared.Administration;
using Content.Shared.CCVar;
using Content.Shared.Eui;
using Robust.Shared.Configuration;

namespace Content.Server.Administration;

/// <summary>
/// Server side of the economy config admin window (<c>moneyconfig</c>). Reads/writes the <c>economy.*</c> CVars
/// live (ARCHIVE CVars, same persistence path as the GIF/TTS panels).
/// </summary>
public sealed partial class MoneyConfigEui : BaseEui
{
    [Dependency] private IAdminManager _admins = default!;
    [Dependency] private IConfigurationManager _cfg = default!;

    private string _status = string.Empty;

    private MoneyConfigEuiState _state = new(false, 0, false, 0, 0f, false, 0, 0f, false, 0f, 0, "");

    public MoneyConfigEui()
    {
        IoCManager.InjectDependencies(this);
    }

    public override void Opened()
    {
        base.Opened();
        _admins.OnPermsChanged += OnPermsChanged;
    }

    public override void Closed()
    {
        base.Closed();
        _admins.OnPermsChanged -= OnPermsChanged;
    }

    private void OnPermsChanged(AdminPermsChangedEventArgs args)
    {
        if (args.Player == Player)
            BuildState();
    }

    private bool CanEdit() => _admins.HasAdminFlag(Player, AdminFlags.Admin);

    public override EuiStateBase GetNewState() => _state;

    public void BuildState()
    {
        if (!CanEdit())
        {
            Close();
            return;
        }

        _state = new MoneyConfigEuiState(
            true,
            _cfg.GetCVar(CCVars.EconomyMaxBalance),
            _cfg.GetCVar(CCVars.EconomySalaryEnabled),
            _cfg.GetCVar(CCVars.EconomySalaryDefaultPerHour),
            _cfg.GetCVar(CCVars.EconomySalaryMultiplier),
            _cfg.GetCVar(CCVars.EconomyAtmRequireFingerprint),
            _cfg.GetCVar(CCVars.EconomyAtmWithdrawLimitPerRound),
            _cfg.GetCVar(CCVars.EconomyAtmFeePercent),
            _cfg.GetCVar(CCVars.EconomyVendingPrices),
            _cfg.GetCVar(CCVars.EconomyVendingPriceMultiplier),
            _cfg.GetCVar(CCVars.EconomyChipRate),
            _status);

        StateDirty();
    }

    public override void HandleMessage(EuiMessageBase msg)
    {
        base.HandleMessage(msg);

        if (!CanEdit())
            return;

        if (msg is MoneyConfigSetMessage m)
        {
            _status = string.Empty;
            ApplySet(m);
            BuildState();
        }
    }

    private void ApplySet(MoneyConfigSetMessage m)
    {
        switch (m.Field)
        {
            case MoneyConfigField.MaxBalance:
                if (TryLong(m.Value, out var max))
                    _cfg.SetCVar(CCVars.EconomyMaxBalance, Math.Clamp(max, 0, 1_000_000_000_000L));
                break;
            case MoneyConfigField.SalaryEnabled:
                _cfg.SetCVar(CCVars.EconomySalaryEnabled, bool.TryParse(m.Value, out var se) && se);
                break;
            case MoneyConfigField.SalaryDefaultPerHour:
                if (TryInt(m.Value, out var rate))
                    _cfg.SetCVar(CCVars.EconomySalaryDefaultPerHour, Math.Clamp(rate, 0, 1_000_000));
                break;
            case MoneyConfigField.SalaryMultiplier:
                if (TryFloat(m.Value, out var mult))
                    _cfg.SetCVar(CCVars.EconomySalaryMultiplier, Math.Clamp(mult, 0f, 1000f));
                break;
            case MoneyConfigField.AtmRequireFingerprint:
                _cfg.SetCVar(CCVars.EconomyAtmRequireFingerprint, bool.TryParse(m.Value, out var fp) && fp);
                break;
            case MoneyConfigField.AtmWithdrawLimit:
                if (TryInt(m.Value, out var limit))
                    _cfg.SetCVar(CCVars.EconomyAtmWithdrawLimitPerRound, Math.Clamp(limit, 0, 1_000_000_000));
                break;
            case MoneyConfigField.AtmFeePercent:
                if (TryFloat(m.Value, out var fee))
                    _cfg.SetCVar(CCVars.EconomyAtmFeePercent, Math.Clamp(fee, 0f, 100f));
                break;
            case MoneyConfigField.VendingPrices:
                _cfg.SetCVar(CCVars.EconomyVendingPrices, bool.TryParse(m.Value, out var vp) && vp);
                break;
            case MoneyConfigField.VendingPriceMultiplier:
                if (TryFloat(m.Value, out var vm))
                    _cfg.SetCVar(CCVars.EconomyVendingPriceMultiplier, Math.Clamp(vm, 0f, 1000f));
                break;
            case MoneyConfigField.ChipRate:
                if (TryInt(m.Value, out var chip))
                    _cfg.SetCVar(CCVars.EconomyChipRate, Math.Clamp(chip, 1, 1_000_000));
                break;
        }
    }

    private static bool TryInt(string value, out int result)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    private static bool TryLong(string value, out long result)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    private static bool TryFloat(string value, out float result)
        => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
}
