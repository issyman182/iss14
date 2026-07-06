using Content.Shared.Paper;
using Robust.Shared.ContentPack;
using Robust.Shared.Utility;

namespace Content.Shared._Starlight.Paper;

/// <summary>
///     Loads pre-written paper contents from text files in Resources/Documents.
///     This is done because FTL files are really hard to write large documents in.
/// </summary>
/// <remarks>
///     iss14: adapted from Starlight — reads files directly via <see cref="IResourceManager"/>
///     instead of a dedicated PreWrittenDocumentManager IoC service, to avoid touching
///     vanilla IoC/EntryPoint registration.
/// </remarks>
public sealed partial class TextFilePaperContentSystem : EntitySystem
{
    [Dependency] private PaperSystem _paper = default!;
    [Dependency] private IResourceManager _resource = default!;

    private const string DocumentsPath = "/Documents/";
    private const string FallbackLocalization = "en-US";

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<TextFilePaperContentComponent, MapInitEvent>(OnTextFilePaperContentComponentInit, after: [typeof(PaperSystem)]);
    }

    private void OnTextFilePaperContentComponentInit(Entity<TextFilePaperContentComponent> ent, ref MapInitEvent args)
    {
        if (!TryComp<PaperComponent>(ent, out var paperComp))
            return;

        if (!TryGetDocumentContents(ent.Comp.FileName, out var contents))
            return;

        _paper.SetContent((ent, paperComp), contents);

        RemCompDeferred(ent, ent.Comp);
    }

    private bool TryGetDocumentContents(string fileName, out string contents)
    {
        contents = string.Empty;

        var culture = Loc.DefaultCulture?.Name;
        if (culture != null && TryReadDocument(culture, fileName, ref contents))
            return true;

        return TryReadDocument(FallbackLocalization, fileName, ref contents);
    }

    private bool TryReadDocument(string locName, string fileName, ref string contents)
    {
        var path = new ResPath(DocumentsPath) / locName / fileName;
        if (!_resource.ContentFileExists(path))
            return false;

        contents = _resource.ContentFileReadAllText(path);
        return true;
    }
}
