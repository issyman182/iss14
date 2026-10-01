// SPDX-License-Identifier: AGPL-3.0-or-later
// Ported from Goob-Station (originally DeltaV): Chitinid species.

using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Events;
using Robust.Shared.Prototypes;

namespace Content.Shared._DV.Chemistry;

/// <summary>
/// iss14: cancels needle injections into entities with <see cref="BlockInjectionComponent"/>.
/// Injector modes with <c>injectOnUse</c> (hyposprays, jet injectors) bypass it.
/// </summary>
public sealed partial class BlockInjectionSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _proto = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<BlockInjectionComponent, TargetBeforeInjectEvent>(OnBeforeInject);
    }

    private void OnBeforeInject(Entity<BlockInjectionComponent> ent, ref TargetBeforeInjectEvent args)
    {
        if (args.Cancelled)
            return;

        if (!TryComp<InjectorComponent>(args.UsedInjector, out var injector)
            || !_proto.Resolve(injector.ActiveModeProtoId, out var mode)
            || mode.InjectOnUse)
            return;

        args.Cancel();
    }
}
