// iss14: Economy
using System.Globalization;
using System.Linq;
using Content.Server.Administration.Managers;
using Content.Server.Economy;
using Content.Server.EUI;
using Content.Shared.Administration;
using Content.Shared.CCVar;
using Content.Shared.Eui;
using Content.Shared.Roles;
using Content.Shared.Roles.Jobs;
using Robust.Shared.Configuration;
using Robust.Shared.Prototypes;

namespace Content.Server.Administration;

/// <summary>
/// Server side of the economy config admin window (<c>moneyconfig</c>). Reads/writes the <c>economy.*</c> CVars
/// live (ARCHIVE CVars, same persistence path as the GIF/TTS panels) and the per-job salary overrides.
/// </summary>
public sealed partial class MoneyConfigEui : BaseEui
{
    [Dependency] private IAdminManager _admins = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IPrototypeManager _proto = default!;
    [Dependency] private IEntityManager _entMan = default!;

    private string _status = string.Empty;

    private MoneyConfigEuiState _state = new(false, new(), new(), "");

    /// <summary>Everything the panel can edit, in display order.</summary>
    private static readonly (string Section, string Label, MoneyConfigKind Kind, object CVar, double Min, double Max)[] Fields =
    {
        ("money-config-bank", "money-config-max-balance", MoneyConfigKind.Integer, CCVars.EconomyMaxBalance, 0, 1e12),

        ("money-config-salary", "money-config-salary-enabled", MoneyConfigKind.Toggle, CCVars.EconomySalaryEnabled, 0, 1),
        ("money-config-salary", "money-config-salary-default", MoneyConfigKind.Integer, CCVars.EconomySalaryDefault, 0, 1e6),
        ("money-config-salary", "money-config-salary-multiplier", MoneyConfigKind.Decimal, CCVars.EconomySalaryMultiplier, 0, 1000),
        ("money-config-salary", "money-config-salary-min-factor", MoneyConfigKind.Decimal, CCVars.EconomySalaryMinFactor, 0, 1),
        ("money-config-salary", "money-config-salary-full-fraction", MoneyConfigKind.Decimal, CCVars.EconomySalaryFullTimeFraction, 0.01, 1),
        ("money-config-salary", "money-config-salary-max-factor", MoneyConfigKind.Decimal, CCVars.EconomySalaryMaxFactor, 1, 100),
        ("money-config-salary", "money-config-fines-from-salary", MoneyConfigKind.Toggle, CCVars.EconomyFinesFromSalary, 0, 1),

        ("money-config-atm", "money-config-atm-fingerprint", MoneyConfigKind.Toggle, CCVars.EconomyAtmRequireFingerprint, 0, 1),
        ("money-config-atm", "money-config-atm-limit", MoneyConfigKind.Integer, CCVars.EconomyAtmWithdrawLimitPerRound, 0, 1e9),
        ("money-config-atm", "money-config-atm-fee", MoneyConfigKind.Percent, CCVars.EconomyAtmFeePercent, 0, 100),

        ("money-config-vending", "money-config-vending-prices", MoneyConfigKind.Toggle, CCVars.EconomyVendingPrices, 0, 1),
        ("money-config-vending", "money-config-vending-multiplier", MoneyConfigKind.Decimal, CCVars.EconomyVendingPriceMultiplier, 0, 1000),

        ("money-config-service", "money-config-service-multiplier", MoneyConfigKind.Decimal, CCVars.EconomyServicePriceMultiplier, 0, 1000),
        ("money-config-service", "money-config-service-commission", MoneyConfigKind.Percent, CCVars.EconomyServiceCommissionPercent, 0, 100),

        ("money-config-chips", "money-config-chip-rate", MoneyConfigKind.Integer, CCVars.EconomyChipRate, 1, 1e6),
        ("money-config-chips", "money-config-chip-fee", MoneyConfigKind.Percent, CCVars.EconomyChipExchangeFeePercent, 0, 100),
        ("money-config-chips", "money-config-chip-limit", MoneyConfigKind.Integer, CCVars.EconomyChipPayoutLimitPerRound, 0, 1e9),

        ("money-config-cargo", "money-config-cargo-private", MoneyConfigKind.Toggle, CCVars.EconomyCargoPrivateOrders, 0, 1),
        ("money-config-cargo", "money-config-cargo-markup", MoneyConfigKind.Decimal, CCVars.EconomyCargoPrivateMarkup, 0, 1000),
        ("money-config-cargo", "money-config-cargo-blocklist", MoneyConfigKind.Toggle, CCVars.EconomyCargoPrivateBlocklist, 0, 1),

        ("money-config-legal", "money-config-lawyer-fee", MoneyConfigKind.Percent, CCVars.EconomyLawyerFeePercent, 0, 100),

        ("money-config-heist", "money-config-heist-enabled", MoneyConfigKind.Toggle, CCVars.EconomyHeistEnabled, 0, 1),
        ("money-config-heist", "money-config-heist-factor", MoneyConfigKind.Decimal, CCVars.EconomyHeistPayoutFactor, 0, 1000),
        ("money-config-heist", "money-config-heist-min-value", MoneyConfigKind.Integer, CCVars.EconomyHeistMinItemValue, 0, 1e9),
        ("money-config-heist", "money-config-heist-thief-bonus", MoneyConfigKind.Integer, CCVars.EconomyHeistThiefBonus, 0, 1e9),

        ("money-config-store", "money-config-store-enabled", MoneyConfigKind.Toggle, CCVars.EconomyStoreEnabled, 0, 1),

        ("money-config-tasks", "money-config-tasks-enabled", MoneyConfigKind.Toggle, CCVars.EconomyTasksEnabled, 0, 1),
        ("money-config-tasks", "money-config-task-multiplier", MoneyConfigKind.Decimal, CCVars.EconomyTaskRewardMultiplier, 0, 1000),
        ("money-config-tasks", "money-config-task-interval", MoneyConfigKind.Integer, CCVars.EconomyTaskIntervalSeconds, 30, 86400),
    };

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

    private static string CVarName(object cvar) => cvar switch
    {
        CVarDef<bool> b => b.Name,
        CVarDef<int> i => i.Name,
        CVarDef<long> l => l.Name,
        CVarDef<float> f => f.Name,
        _ => throw new ArgumentException("unsupported cvar type"),
    };

    private string Format(object cvar) => cvar switch
    {
        CVarDef<bool> b => _cfg.GetCVar(b).ToString(),
        CVarDef<int> i => _cfg.GetCVar(i).ToString(CultureInfo.InvariantCulture),
        CVarDef<long> l => _cfg.GetCVar(l).ToString(CultureInfo.InvariantCulture),
        CVarDef<float> f => _cfg.GetCVar(f).ToString("0.###", CultureInfo.InvariantCulture),
        _ => string.Empty,
    };

    public void BuildState()
    {
        if (!CanEdit())
        {
            Close();
            return;
        }

        var entries = Fields
            .Select(f => new MoneyConfigEntry(CVarName(f.CVar), Loc.GetString(f.Section), Loc.GetString(f.Label), f.Kind, Format(f.CVar)))
            .ToList();

        var salary = _entMan.System<SalarySystem>();
        var jobs = _entMan.System<SharedJobSystem>();
        var rows = new List<MoneySalaryRow>();
        foreach (var job in _proto.EnumeratePrototypes<JobPrototype>())
        {
            if (!job.SetPreference)
                continue;

            var department = jobs.TryGetDepartment(job.ID, out var dept) ? Loc.GetString(dept.Name) : string.Empty;
            rows.Add(new MoneySalaryRow(job.ID, job.LocalizedName, department, salary.GetBaseSalary(job.ID), salary.GetPrototypeSalary(job.ID), salary.HasOverride(job.ID)));
        }

        rows.Sort((a, b) =>
        {
            var c = string.Compare(a.Department, b.Department, StringComparison.Ordinal);
            return c != 0 ? c : string.Compare(a.JobName, b.JobName, StringComparison.Ordinal);
        });

        _state = new MoneyConfigEuiState(true, entries, rows, _status);
        StateDirty();
    }

    public override void HandleMessage(EuiMessageBase msg)
    {
        base.HandleMessage(msg);

        if (!CanEdit())
            return;

        switch (msg)
        {
            case MoneyConfigSetMessage m:
                _status = string.Empty;
                ApplySet(m);
                BuildState();
                break;
            case MoneyConfigSetSalaryMessage s:
                _status = string.Empty;
                ApplySalary(s);
                BuildState();
                break;
        }
    }

    private void ApplySet(MoneyConfigSetMessage m)
    {
        var field = Fields.FirstOrDefault(f => CVarName(f.CVar) == m.Key);
        if (field.CVar == null)
            return;

        switch (field.CVar)
        {
            case CVarDef<bool> b:
                _cfg.SetCVar(b, bool.TryParse(m.Value, out var v) && v);
                break;
            case CVarDef<int> i:
                if (int.TryParse(m.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iv))
                    _cfg.SetCVar(i, (int) Math.Clamp(iv, field.Min, field.Max));
                else
                    _status = Loc.GetString("money-config-invalid");
                break;
            case CVarDef<long> l:
                if (long.TryParse(m.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lv))
                    _cfg.SetCVar(l, (long) Math.Clamp(lv, field.Min, field.Max));
                else
                    _status = Loc.GetString("money-config-invalid");
                break;
            case CVarDef<float> f:
                if (float.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fv))
                    _cfg.SetCVar(f, (float) Math.Clamp(fv, field.Min, field.Max));
                else
                    _status = Loc.GetString("money-config-invalid");
                break;
        }
    }

    private void ApplySalary(MoneyConfigSetSalaryMessage s)
    {
        if (!_proto.HasIndex<JobPrototype>(s.JobId))
            return;

        var salary = _entMan.System<SalarySystem>();
        if (string.IsNullOrWhiteSpace(s.Value))
        {
            salary.SetOverride(s.JobId, null);
            return;
        }

        if (!int.TryParse(s.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount) || amount < 0)
        {
            _status = Loc.GetString("money-config-invalid");
            return;
        }

        salary.SetOverride(s.JobId, Math.Min(amount, 1_000_000));
    }
}
