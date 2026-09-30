using Content.Shared.Body.Components;
using Content.Shared.Body.Systems; // Shitmed Change
using Content.Shared.Database;
using Content.Shared.Gibbing.Components; // iss14 fix
using Content.Shared.Gibbing.Events; // Shitmed Change
using Content.Shared.Gibbing.Systems; // iss14 fix
using JetBrains.Annotations;

namespace Content.Server.Destructible.Thresholds.Behaviors
{
    [UsedImplicitly]
    [DataDefinition]
    public sealed partial class GibBehavior : IThresholdBehavior
    {
        [DataField] public GibType GibType = GibType.Gib; // Shitmed Change
        [DataField] public GibContentsOption GibContents = GibContentsOption.Drop; // Shitmed Change
        [DataField("recursive")] private bool _recursive = true;

        public LogImpact Impact => LogImpact.Extreme;

        public void Execute(EntityUid owner, DestructibleSystem system, EntityUid? cause = null)
        {
            // Shitmed Change - route gibbing through the body system so body parts are handled
            if (system.EntityManager.TryGetComponent(owner, out BodyComponent? body))
            {
                system.EntityManager.System<SharedBodySystem>().GibBody(owner, _recursive, body, gib: GibType, contents: GibContents);
                return;
            }

            // iss14 fix: entities without a body (upstream gibs anything) must still be gibbed/destroyed,
            // otherwise a "gib" threshold on e.g. a simple mob or item silently did nothing. Gibbable
            // entities go through the gibbing system (giblets, sound, contents); anything else is
            // destroyed the way upstream's Gib() did via DestroyEntity.
            if (system.EntityManager.HasComponent<GibbableComponent>(owner))
            {
                system.EntityManager.System<GibbingSystem>().TryGibEntity(owner, owner, GibType, GibContents, out _);
                if (GibType == GibType.Gib)
                    return;
            }

            system.DestroyEntity(owner);
        }
    }
}
