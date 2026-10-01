// iss14: GIFs in chat via GifSnap
using Content.Shared.Chat;
using Robust.Shared.Serialization;

namespace Content.Shared.Gifs;

/// <summary>
/// Shared constants and validation helpers for the GIF chat feature.
/// </summary>
public static class GifConstants
{
    /// <summary>Longest search query accepted by the server.</summary>
    public const int MaxQueryLength = 100;

    /// <summary>Highest result page a client may ask for.</summary>
    public const int MaxPage = 20;

    /// <summary>Longest GIF title that is carried around (also ends up in chat markup).</summary>
    public const int MaxTitleLength = 80;

    /// <summary>Start of the inline GIF markup tag the server appends to a chat line.</summary>
    public const string TagMarker = "[gif ";

    /// <summary>True if a wrapped chat line carries an inline GIF tag.</summary>
    public static bool ContainsGifTag(string? wrappedMessage)
        => wrappedMessage != null && wrappedMessage.Contains(TagMarker, StringComparison.Ordinal);

    /// <summary>
    /// Removes every inline GIF tag (and the space before it) from a wrapped chat line, leaving the plain text.
    /// A <c>]</c> inside the (escaped) title is written as <c>\]</c>, so the first unescaped <c>]</c> ends the tag.
    /// </summary>
    public static string StripGifTags(string wrappedMessage)
    {
        var text = wrappedMessage;
        int start;
        while ((start = text.IndexOf(TagMarker, StringComparison.Ordinal)) >= 0)
        {
            var end = start + TagMarker.Length;
            while (end < text.Length)
            {
                if (text[end] == '\\')
                {
                    end += 2;
                    continue;
                }

                if (text[end] == ']')
                    break;

                end++;
            }

            // Malformed (no closing bracket): drop the rest of the line.
            var cut = end >= text.Length ? text.Length : end + 1;
            var before = text[..start].TrimEnd();
            text = before + text[cut..];
        }

        return text;
    }

    /// <summary>Longest GIF id accepted.</summary>
    public const int MaxIdLength = 80;

    /// <summary>
    /// GIF ids end up inside chat markup, so only a conservative character set is accepted
    /// (equivalent to <c>^[A-Za-z0-9_\-]{1,80}$</c>).
    /// </summary>
    public static bool IsValidId(string? id)
    {
        if (id == null || id.Length == 0 || id.Length > MaxIdLength)
            return false;

        foreach (var c in id)
        {
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-')
                continue;
            return false;
        }

        return true;
    }

    /// <summary>Parses the <c>gifs.allowed_channels</c> CVar into a channel mask.</summary>
    public static ChatSelectChannel ParseAllowedChannels(string raw)
    {
        var result = ChatSelectChannel.None;
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<ChatSelectChannel>(part, true, out var channel))
                result |= channel;
        }

        // GIFs only ever go through the OOC and LOOC paths.
        return result & (ChatSelectChannel.OOC | ChatSelectChannel.LOOC);
    }
}

/// <summary>A single search result as shown in the picker.</summary>
[Serializable, NetSerializable]
public sealed class GifSearchEntry(string id, string title, int width, int height, byte[] thumbnailPng)
{
    public string Id = id;
    public string Title = title;
    public int Width = width;
    public int Height = height;

    /// <summary>Server-made ~96 px PNG thumbnail of the preview image. May be empty if the preview failed to load.</summary>
    public byte[] ThumbnailPng = thumbnailPng;
}

/// <summary>Client → server: search GifSnap. An empty query returns trending GIFs.</summary>
[Serializable, NetSerializable]
public sealed class GifSearchRequestEvent(string query, int page) : EntityEventArgs
{
    public string Query = query;
    public int Page = page;
}

/// <summary>Server → client: search results for a previous <see cref="GifSearchRequestEvent"/>.</summary>
[Serializable, NetSerializable]
public sealed class GifSearchResponseEvent(string query, int page, bool hasNext, List<GifSearchEntry> results, string? error)
    : EntityEventArgs
{
    public string Query = query;
    public int Page = page;
    public bool HasNext = hasNext;
    public List<GifSearchEntry> Results = results;

    /// <summary>Localization key of an error, or null on success.</summary>
    public string? Error = error;
}

/// <summary>Client → server: post the given GIF to the given chat channel.</summary>
[Serializable, NetSerializable]
public sealed class GifSendRequestEvent(string id, ChatSelectChannel channel) : EntityEventArgs
{
    public string Id = id;
    public ChatSelectChannel Channel = channel;
}

/// <summary>Client → server: ask for the sprite sheet of a GIF that appeared in chat.</summary>
[Serializable, NetSerializable]
public sealed class GifDataRequestEvent(string id) : EntityEventArgs
{
    public string Id = id;
}

/// <summary>
/// Server → client: a decoded GIF as a PNG sprite sheet. Frames are laid out left-to-right, top-to-bottom in
/// <see cref="Columns"/> columns, each <see cref="FrameWidth"/> x <see cref="FrameHeight"/> pixels.
/// </summary>
[Serializable, NetSerializable]
public sealed class GifDataEvent(string id, byte[] sheetPng, int frameWidth, int frameHeight, int frameCount, int columns, int[] frameDelaysMs)
    : EntityEventArgs
{
    public string Id = id;
    public byte[] SheetPng = sheetPng;
    public int FrameWidth = frameWidth;
    public int FrameHeight = frameHeight;
    public int FrameCount = frameCount;
    public int Columns = columns;
    public int[] FrameDelaysMs = frameDelaysMs;
}

/// <summary>
/// Server → client: something went wrong. <see cref="Message"/> is a localization key the client resolves;
/// <see cref="Minutes"/> is passed as the <c>minutes</c> argument (used by the timed-out error).
/// </summary>
[Serializable, NetSerializable]
public sealed class GifErrorEvent(string message, int minutes = 0) : EntityEventArgs
{
    public string Message = message;
    public int Minutes = minutes;
}
