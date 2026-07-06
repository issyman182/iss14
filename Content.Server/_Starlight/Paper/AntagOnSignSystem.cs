using System.Linq;
using Content.Server.Antag;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Rules.Components;
using Content.Shared._Starlight.Paper;
using Content.Shared.Fax.Components;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Content.Shared.Whitelist; // Starlight

namespace Content.Server._Starlight.Paper;

public sealed partial class AntagOnSignSystem : EntitySystem
{
    [Dependency] private AntagSelectionSystem _antag = default!;
    [Dependency] private GameTicker _gameTicker = default!;
    [Dependency] private IComponentFactory _componentFactory = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private EntityWhitelistSystem _whitelist = default!;

    private static readonly EntProtoId ParadoxCloneRuleId = "ParadoxCloneSpawn";

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<AntagOnSignComponent, PaperSignedEvent>(OnPaperSigned, before: [typeof(ObjectiveOnSignSystem)]);
        SubscribeLocalEvent<AntagOnSignComponent, MapInitEvent>(OnMapInit);
    }

    private void OnMapInit(EntityUid uid, AntagOnSignComponent comp, MapInitEvent init)
    {
        if (comp.KeepFaxable)
            return;
        RemComp<FaxableObjectComponent>(uid); //cause this breaks shit like infinite antags
    }

    private void OnPaperSigned(EntityUid uid, AntagOnSignComponent component, ref PaperSignedEvent args)
    {
        if (_whitelist.IsWhitelistPass(component.Blacklist, args.Signer))
            return; // Starlight - prevent blacklisted entities from becoming antag
        if (component.ChargesRemaining <= 0)
            return;
        var signer = args.Signer;
        if (!TryComp(signer, out ActorComponent? actor))
            return;
        if (component.SignedEntityUids.Contains(signer))
            return;
        component.ChargesRemaining--;
        component.SignedEntityUids.Add(signer);

        if (_random.NextFloat() > component.Chance)
            return;

        var session = actor.PlayerSession;
        foreach (var antag in component.Antags)
        {
            var targetComp = _componentFactory.GetComponent(antag.TargetComponent);

            // iss14: ForceMakeAntag has multiple overloads on current wizden master, so
            // GetMethod(name) would throw AmbiguousMatchException — pick the two-arg overload.
            var fmakeantag = typeof(AntagSelectionSystem)
                .GetMethods()
                .FirstOrDefault(m => m.Name == nameof(AntagSelectionSystem.ForceMakeAntag) && m.GetParameters().Length == 2);
            if (fmakeantag == null)
            {
                Log.Error("Failed to reflect \"ForceMakeAntag\" method from AntagSelectionSystem for genericization");
                continue;
            }
            var generic = fmakeantag.MakeGenericMethod(targetComp.GetType());
            generic.Invoke(_antag, [session, antag.Antag]);
        }

        if (component.ParadoxClone)
        {
            var ruleEnt = _gameTicker.AddGameRule(ParadoxCloneRuleId);

            if (!TryComp<ParadoxCloneRuleComponent>(ruleEnt, out var paradoxCloneRuleComp))
                return;

            paradoxCloneRuleComp.OriginalBody = args.Signer; // override the target player

            _gameTicker.StartGameRule(ruleEnt);
        }
    }
}
