// iss14: GIFs in chat via GifSnap
using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    /// <summary>
    ///     Master switch for sending GIFs in OOC/LOOC chat. Replicated so the client can hide the picker button.
    /// </summary>
    public static readonly CVarDef<bool> GifsEnabled =
        CVarDef.Create("gifs.enabled", true, CVar.SERVER | CVar.REPLICATED | CVar.ARCHIVE);

    /// <summary>
    ///     Base URL of the GifSnap-compatible API (<c>/gifs/search</c>, <c>/gifs/trending</c>, <c>/gifs/{id}</c>).
    /// </summary>
    public static readonly CVarDef<string> GifsApiUrl =
        CVarDef.Create("gifs.api_url", "https://gifsnap.com/api/v1", CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    ///     Maximum width (pixels) of a GIF frame as shipped to clients. Larger GIFs are downscaled, keeping aspect.
    /// </summary>
    public static readonly CVarDef<int> GifsMaxWidth =
        CVarDef.Create("gifs.max_width", 200, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    ///     Maximum number of frames kept per GIF. Longer animations are subsampled evenly.
    /// </summary>
    public static readonly CVarDef<int> GifsMaxFrames =
        CVarDef.Create("gifs.max_frames", 40, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    ///     Maximum size (bytes) of a source GIF/WebP the server is willing to download.
    /// </summary>
    public static readonly CVarDef<int> GifsMaxDownloadBytes =
        CVarDef.Create("gifs.max_download_bytes", 4_000_000, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    ///     Maximum size (bytes) of the encoded PNG sprite sheet sent to clients.
    /// </summary>
    public static readonly CVarDef<int> GifsMaxSheetBytes =
        CVarDef.Create("gifs.max_sheet_bytes", 2_000_000, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    ///     Maximum number of search results per page shown in the picker.
    /// </summary>
    public static readonly CVarDef<int> GifsSearchLimit =
        CVarDef.Create("gifs.search_limit", 12, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    ///     Number of encoded GIF sheets kept in the server-side cache.
    /// </summary>
    public static readonly CVarDef<int> GifsCacheEntries =
        CVarDef.Create("gifs.cache_entries", 256, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    ///     Id of the (hidden) JobPrototype whose requirements gate GIF sending. Admins tune the requirement in the
    ///     role-requirement editor. If the prototype does not exist, everyone may send GIFs.
    /// </summary>
    public static readonly CVarDef<string> GifsRole =
        CVarDef.Create("gifs.role", "GifSender", CVar.SERVER | CVar.REPLICATED | CVar.ARCHIVE);

    /// <summary>
    ///     Period (seconds) over which <see cref="GifsRateLimitCount"/> GIF sends are allowed per player.
    /// </summary>
    public static readonly CVarDef<float> GifsRateLimitPeriod =
        CVarDef.Create("gifs.rate_limit_period", 10f, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    ///     How many GIFs a player may send per <see cref="GifsRateLimitPeriod"/>.
    /// </summary>
    public static readonly CVarDef<int> GifsRateLimitCount =
        CVarDef.Create("gifs.rate_limit_count", 3, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    ///     Period (seconds) over which <see cref="GifsSearchRateLimitCount"/> GIF searches are allowed per player.
    /// </summary>
    public static readonly CVarDef<float> GifsSearchRateLimitPeriod =
        CVarDef.Create("gifs.search_rate_limit_period", 5f, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    ///     How many GIF searches a player may send per <see cref="GifsSearchRateLimitPeriod"/>.
    /// </summary>
    public static readonly CVarDef<int> GifsSearchRateLimitCount =
        CVarDef.Create("gifs.search_rate_limit_count", 6, CVar.SERVERONLY | CVar.ARCHIVE);

    /// <summary>
    ///     Comma-separated list of <c>ChatSelectChannel</c> names that accept GIFs (only OOC and LOOC are supported).
    ///     Replicated so the picker can pick a sensible default channel.
    /// </summary>
    public static readonly CVarDef<string> GifsAllowedChannels =
        CVarDef.Create("gifs.allowed_channels", "OOC,LOOC", CVar.SERVER | CVar.REPLICATED | CVar.ARCHIVE);
}
