namespace Content.Shared._Starlight.Paper;

/// <summary>
///     Raised directed on a paper entity when someone signs it.
/// </summary>
/// <remarks>
///     iss14: in Starlight this event lives in Content.Shared.Paper.PaperComponent and is raised
///     from their modified vanilla PaperSystem. Here it is vendored under _Starlight and raised
///     by <c>Content.Server._Starlight.Paper.PaperSigningSystem</c> instead, to keep vanilla
///     files untouched.
/// </remarks>
[ByRefEvent]
public record struct PaperSignedEvent(EntityUid Signer);
