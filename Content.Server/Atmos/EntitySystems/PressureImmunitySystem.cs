using Content.Server.Atmos.Components;
using Content.Shared.Atmos;

namespace Content.Server.Atmos.EntitySystems;

/// <summary>
/// Grants pressure immunity to anything carrying <see cref="PressureImmunityComponent"/>.
/// </summary>
/// <remarks>
/// iss14: upstream moved pressure immunity to a status effect, but Shitmed organs grant it as a
/// plain component through their <c>onAdd</c> list, which cannot apply a status effect.
/// </remarks>
public sealed class PressureImmunitySystem : EntitySystem
{
    [Dependency] private readonly BarotraumaSystem _barotrauma = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<PressureImmunityComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<PressureImmunityComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<PressureImmunityComponent, RefreshPressureImmunityEvent>(OnRefresh);
    }

    private void OnStartup(Entity<PressureImmunityComponent> ent, ref ComponentStartup args)
    {
        _barotrauma.RefreshPressureImmunity(ent);
    }

    private void OnShutdown(Entity<PressureImmunityComponent> ent, ref ComponentShutdown args)
    {
        _barotrauma.RefreshPressureImmunity(ent);
    }

    private void OnRefresh(Entity<PressureImmunityComponent> ent, ref RefreshPressureImmunityEvent args)
    {
        args.IsImmune = true;
    }
}
