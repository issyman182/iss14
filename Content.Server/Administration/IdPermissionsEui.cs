using System.Linq;
using Content.Server.Access.Systems;
using Content.Server.Administration.Managers;
using Content.Server.EUI;
using Content.Shared.StationRecords.Components;
using Content.Shared.StationRecords.Systems;
using Content.Shared.Access;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Shared.Administration;
using Content.Shared.Administration.Logs;
using Content.Shared.CCVar;
using Content.Shared.Database;
using Content.Shared.Eui;
using Content.Shared.Roles;
using Content.Shared.StationRecords;
using Content.Shared.StatusIcon;
using Robust.Shared.Configuration;
using Robust.Shared.Prototypes;

namespace Content.Server.Administration;

/// <summary>
/// Server side of the admin ID permissions editor: a privileged version of the ID card console bound to a
/// specific ID card entity. No privileged-ID or console access-level restrictions apply.
/// </summary>
public sealed partial class IdPermissionsEui : BaseEui
{
    [Dependency] private IAdminManager _admins = default!;
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IPrototypeManager _proto = default!;
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private ISharedAdminLogManager _adminLogger = default!;

    private readonly EntityUid _target;

    public IdPermissionsEui(EntityUid target)
    {
        _target = target;
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

    private IdPermissionsEuiState _state = new(false, false, "", null, null, "", "", new());

    public void BuildState()
    {
        if (!CanEdit())
        {
            Close();
            return;
        }

        if (!_entities.EntityExists(_target) || !_entities.TryGetComponent(_target, out IdCardComponent? idCard))
        {
            _state = new IdPermissionsEuiState(true, false, "", null, null, "", "", new());
            StateDirty();
            return;
        }

        var access = _entities.TryGetComponent(_target, out AccessComponent? accessComp)
            ? accessComp.Tags.ToList()
            : new List<ProtoId<AccessLevelPrototype>>();

        // Prefer the station record's job, like the ID card console does.
        var jobProto = idCard.JobPrototype?.Id ?? string.Empty;
        var records = _entities.System<StationRecordsSystem>();
        if (_entities.TryGetComponent(_target, out StationRecordKeyStorageComponent? keyStorage)
            && keyStorage.Key is { } key
            && records.TryGetRecord<GeneralStationRecord>(key, out var record))
        {
            jobProto = record.JobPrototype;
        }

        _state = new IdPermissionsEuiState(
            true,
            true,
            _entities.GetComponent<MetaDataComponent>(_target).EntityName,
            idCard.FullName,
            idCard.LocalizedJobTitle,
            jobProto,
            idCard.JobIcon.Id,
            access);

        StateDirty();
    }

    public override void HandleMessage(EuiMessageBase msg)
    {
        base.HandleMessage(msg);

        if (msg is not IdPermissionsWriteMessage write)
            return;

        if (!CanEdit())
            return;

        if (!_entities.EntityExists(_target) || !_entities.HasComponent<IdCardComponent>(_target))
        {
            BuildState();
            return;
        }

        var idCardSystem = _entities.System<IdCardSystem>();
        var accessSystem = _entities.System<AccessSystem>();

        var player = Player.AttachedEntity;

        // Clamp like the ID card console.
        var fullName = write.FullName;
        var jobTitle = write.JobTitle;
        var maxNameLength = _cfg.GetCVar(CCVars.MaxNameLength);
        var maxIdJobLength = _cfg.GetCVar(CCVars.MaxIdJobLength);

        if (fullName.Length > maxNameLength)
            fullName = fullName[..maxNameLength];

        if (jobTitle.Length > maxIdJobLength)
            jobTitle = jobTitle[..maxIdJobLength];

        idCardSystem.TryChangeFullName(_target, fullName, player: player);
        idCardSystem.TryChangeJobTitle(_target, jobTitle, player: player);

        JobPrototype? job = null;
        if (write.JobProto != null
            && _proto.TryIndex<JobPrototype>(write.JobProto, out job)
            && _proto.Resolve(job.Icon, out var jobIcon))
        {
            idCardSystem.TryChangeJobIcon(_target, jobIcon, player: player);
            idCardSystem.TryChangeJobDepartment(_target, job);
        }

        // Explicitly picked icon wins over the job preset's icon. This allows icons that have no
        // job prototype at all (syndicate, nukies, ERT, CBURN, death squad, ...).
        JobIconPrototype? explicitIcon = null;
        if (write.JobIcon != null && _proto.TryIndex<JobIconPrototype>(write.JobIcon, out explicitIcon))
            idCardSystem.TryChangeJobIcon(_target, explicitIcon, player: player);

        // Keep the station record in sync, like the ID card console.
        var records = _entities.System<StationRecordsSystem>();
        var hasRecord = false;
        if (_entities.TryGetComponent(_target, out StationRecordKeyStorageComponent? keyStorage)
            && keyStorage.Key is { } key
            && records.TryGetRecord<GeneralStationRecord>(key, out var record))
        {
            hasRecord = true;
            record.Name = fullName;
            record.JobTitle = jobTitle;

            if (job != null)
            {
                record.JobPrototype = job.ID;
                record.JobIcon = job.Icon;
            }

            if (explicitIcon != null)
                record.JobIcon = explicitIcon.ID;

            records.Synchronize(key);
        }

        if (!hasRecord && job != null && _entities.TryGetComponent(_target, out IdCardComponent? idCard))
            idCard.JobPrototype = job.ID;

        // Access: admin edit, so only validate that the prototypes exist — no console whitelist,
        // no privileged-ID subset check.
        var newAccess = write.Access.Where(tag => _proto.HasIndex(tag)).ToList();
        var oldTags = accessSystem.TryGetTags(_target)?.ToList() ?? new List<ProtoId<AccessLevelPrototype>>();

        if (!oldTags.OrderBy(t => t.Id).SequenceEqual(newAccess.OrderBy(t => t.Id)))
        {
            var addedTags = newAccess.Except(oldTags).Select(tag => "+" + tag).ToList();
            var removedTags = oldTags.Except(newAccess).Select(tag => "-" + tag).ToList();
            accessSystem.TrySetTags(_target, newAccess);

            _adminLogger.Add(LogType.Action, LogImpact.High,
                $"{Player:user} (admin ID panel) modified {_entities.ToPrettyString(_target):entity} accesses: [{string.Join(", ", addedTags.Union(removedTags))}] -> [{string.Join(", ", newAccess)}]");
        }

        BuildState();
    }
}
