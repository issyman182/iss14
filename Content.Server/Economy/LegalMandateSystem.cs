// iss14: Economy
using Content.Shared.Access.Systems;
using Content.Shared.Economy;
using Content.Shared.Examine;
using Content.Shared.Forensics.Components;
using Content.Shared.Paper;
using Content.Shared.Popups;
using Content.Shared.Verbs;
using Robust.Server.Player;
using Robust.Shared.Player;

namespace Content.Server.Economy;

/// <summary>
/// Signing of legal representation agreements (<see cref="LegalMandateComponent"/>). A signature records the
/// signing player's session, so only the real client can authorize a lawyer; the visible part is a stamp on the
/// paper. The agreement is filed at a fine terminal (<see cref="FineSystem"/>).
/// </summary>
public sealed partial class LegalMandateSystem : EntitySystem
{
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private AccessReaderSystem _access = default!;
    [Dependency] private PaperSystem _paper = default!;
    [Dependency] private SharedPopupSystem _popup = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<LegalMandateComponent, GetVerbsEvent<AlternativeVerb>>(OnGetVerbs);
        SubscribeLocalEvent<LegalMandateComponent, ExaminedEvent>(OnExamined);
    }

    private void OnGetVerbs(Entity<LegalMandateComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || !args.CanComplexInteract || ent.Comp.Filed)
            return;

        var user = args.User;

        if (ent.Comp.ClientUserId == null)
        {
            args.Verbs.Add(new AlternativeVerb
            {
                Text = Loc.GetString("legal-mandate-verb-sign-client"),
                Act = () => SignAsClient(ent, user),
                Priority = 2,
            });
        }

        if (ent.Comp.LawyerUserId == null && _access.FindAccessTags(user).Contains("Lawyer"))
        {
            args.Verbs.Add(new AlternativeVerb
            {
                Text = Loc.GetString("legal-mandate-verb-sign-lawyer"),
                Act = () => SignAsLawyer(ent, user),
                Priority = 1,
            });
        }
    }

    private bool TryGetSession(EntityUid user, out ICommonSession session)
    {
        if (_players.TryGetSessionByEntity(user, out var s))
        {
            session = s;
            return true;
        }

        session = default!;
        return false;
    }

    public void SignAsClient(Entity<LegalMandateComponent> ent, EntityUid user)
    {
        if (ent.Comp.ClientUserId != null || !TryGetSession(user, out var session))
            return;

        if (ent.Comp.LawyerUserId == session.UserId)
        {
            _popup.PopupEntity(Loc.GetString("legal-mandate-same-person"), ent, user, PopupType.SmallCaution);
            return;
        }

        ent.Comp.ClientUserId = session.UserId;
        ent.Comp.ClientName = Name(user);
        ent.Comp.ClientFingerprint = TryComp<FingerprintComponent>(user, out var fp) ? fp.Fingerprint : null;
        Dirty(ent);

        Stamp(ent, Loc.GetString("legal-mandate-stamp-client", ("name", ent.Comp.ClientName)), "paper_stamp-generic");
        _popup.PopupEntity(Loc.GetString("legal-mandate-signed-client"), ent, user);
    }

    public void SignAsLawyer(Entity<LegalMandateComponent> ent, EntityUid user)
    {
        if (ent.Comp.LawyerUserId != null || !TryGetSession(user, out var session))
            return;

        if (ent.Comp.ClientUserId == session.UserId)
        {
            _popup.PopupEntity(Loc.GetString("legal-mandate-same-person"), ent, user, PopupType.SmallCaution);
            return;
        }

        ent.Comp.LawyerUserId = session.UserId;
        ent.Comp.LawyerName = Name(user);
        Dirty(ent);

        Stamp(ent, Loc.GetString("legal-mandate-stamp-lawyer", ("name", ent.Comp.LawyerName)), "paper_stamp-lawyer");
        _popup.PopupEntity(Loc.GetString("legal-mandate-signed-lawyer"), ent, user);
    }

    private void Stamp(EntityUid paper, string text, string state)
    {
        if (!TryComp<PaperComponent>(paper, out var paperComp))
            return;

        var info = new StampDisplayInfo
        {
            StampedName = text,
            StampedColor = Color.FromHex("#2b4ea8"),
        };
        _paper.TryStamp((paper, paperComp), info, state);
    }

    private void OnExamined(Entity<LegalMandateComponent> ent, ref ExaminedEvent args)
    {
        args.PushMarkup(Loc.GetString("legal-mandate-examine",
            ("client", ent.Comp.ClientName ?? Loc.GetString("legal-mandate-unsigned")),
            ("lawyer", ent.Comp.LawyerName ?? Loc.GetString("legal-mandate-unsigned")),
            ("filed", ent.Comp.Filed)));
    }
}
