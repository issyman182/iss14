using Content.Shared._Starlight.Paper;
using Content.Shared.Database;
using Content.Shared.Paper;
using Content.Shared.Popups;
using Content.Shared.Tag;
using Content.Shared.Verbs;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Content.Shared.Administration.Logs;
using Robust.Shared.Audio.Systems;

namespace Content.Server._Starlight.Paper;

/// <summary>
///     Lets players sign papers with a pen via an alt-click verb, stamping their name onto the paper
///     and raising <see cref="PaperSignedEvent"/> for the on-sign systems.
/// </summary>
/// <remarks>
///     iss14: in Starlight this is implemented inside their modified vanilla PaperSystem
///     (Umbra additions). Reimplemented here as a standalone server system on top of vanilla
///     <see cref="PaperSystem.TryStamp"/> so no vanilla files need to change.
/// </remarks>
public sealed partial class PaperSigningSystem : EntitySystem
{
    [Dependency] private ISharedAdminLogManager _adminLogger = default!;
    [Dependency] private PaperSystem _paper = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private TagSystem _tag = default!;

    private static readonly ProtoId<TagPrototype> WriteTag = "Write";

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PaperComponent, GetVerbsEvent<AlternativeVerb>>(AddSignVerb);
    }

    private void AddSignVerb(Entity<PaperComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract)
            return;

        // Pens have a `Write` tag.
        if (!args.Using.HasValue || !_tag.HasTag(args.Using.Value, WriteTag))
            return;

        var user = args.User;

        AlternativeVerb verb = new()
        {
            Act = () =>
            {
                TrySign(ent, user);
            },
            Text = Loc.GetString("paper-component-verb-sign"),
            Priority = 4,
        };
        args.Verbs.Add(verb);
    }

    public bool TrySign(Entity<PaperComponent> paper, EntityUid signer)
    {
        // The signature is represented as a stamp bearing the signer's name.
        var info = new StampDisplayInfo
        {
            StampedName = Name(signer),
            StampedColor = Color.FromHex("#333333"),
        };

        if (!_paper.TryStamp(paper, info, "paper_stamp-generic"))
            return false;

        _popup.PopupEntity(
            Loc.GetString("paper-component-action-signed-self", ("target", paper)),
            signer,
            signer);

        _popup.PopupEntity(
            Loc.GetString("paper-component-action-signed-other", ("user", signer), ("target", paper)),
            paper,
            Filter.PvsExcept(signer, entityManager: EntityManager),
            true);

        _audio.PlayPvs(paper.Comp.Sound, paper);

        _adminLogger.Add(LogType.Verb,
            LogImpact.Low,
            $"{ToPrettyString(signer):player} has signed {ToPrettyString(paper):paper}.");

        // Refresh the open paper UI; SetContent with the unchanged content dirties the
        // component and re-sends the UI state (PaperSystem.UpdateUserInterface is private).
        _paper.SetContent(paper, paper.Comp.Content);

        var ev = new PaperSignedEvent(signer);
        RaiseLocalEvent(paper, ref ev);

        return true;
    }
}
