// iss14: GIFs in chat via GifSnap
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Content.Server.Gifs;

/// <summary>
/// Turns a decoded animated GIF/WebP into a single PNG sprite sheet plus per-frame delays, so the sandboxed client
/// (which can't touch ImageSharp frame collections) only has to decode a PNG and animate sub-rectangles.
/// </summary>
public static class GifEncoder
{
    /// <summary>Default delay (ms) for frames whose metadata says 0 (browsers treat those as ~100 ms too).</summary>
    public const int DefaultFrameDelayMs = 100;

    /// <summary>Clamp for absurd per-frame delays so a single frame can't stall the animation for minutes.</summary>
    public const int MaxFrameDelayMs = 10_000;

    /// <summary>Smallest width we are willing to shrink to before giving up on the byte budget.</summary>
    public const int MinWidth = 48;

    /// <summary>Fewest frames we are willing to keep before giving up on the byte budget.</summary>
    public const int MinFrames = 4;

    /// <summary>Upper bound on decoded frames so a hostile file can't allocate unbounded memory.</summary>
    public const uint DecodeFrameCap = 400;

    /// <summary>Upper bound on source pixels per frame (e.g. 2048x2048) before we refuse to decode.</summary>
    public const long MaxSourcePixels = 2048L * 2048L;

    /// <summary>Encoded sprite sheet ready to be sent to clients.</summary>
    public sealed class Sheet(byte[] png, int frameWidth, int frameHeight, int frameCount, int columns, int[] delaysMs)
    {
        public byte[] Png = png;
        public int FrameWidth = frameWidth;
        public int FrameHeight = frameHeight;
        public int FrameCount = frameCount;
        public int Columns = columns;
        public int[] DelaysMs = delaysMs;

        public long TotalBytes => Png.LongLength + DelaysMs.LongLength * sizeof(int);
    }

    private static readonly PngEncoder SheetEncoder = new()
    {
        CompressionLevel = PngCompressionLevel.BestCompression,
        ColorType = PngColorType.RgbWithAlpha,
    };

    private static readonly PngEncoder ThumbnailEncoder = new()
    {
        CompressionLevel = PngCompressionLevel.BestCompression,
    };

    /// <summary>
    /// Decodes an image file (GIF, WebP, PNG, ...) with sane limits. Returns null if it is too large to decode safely.
    /// </summary>
    public static Image<Rgba32>? DecodeBounded(byte[] data)
    {
        var info = Image.Identify(data);
        if ((long) info.Width * info.Height > MaxSourcePixels || info.Width <= 0 || info.Height <= 0)
            return null;

        var options = new DecoderOptions { MaxFrames = DecodeFrameCap };
        return Image.Load<Rgba32>(options, data);
    }

    /// <summary>
    /// Builds a sprite sheet from a decoded (possibly animated) image. Frames are subsampled evenly down to
    /// <paramref name="maxFrames"/> (skipped frames' delays are folded into the kept frame so timing is preserved),
    /// downscaled to <paramref name="maxWidth"/> (never upscaled) and packed into a roughly square grid. If the PNG
    /// exceeds <paramref name="maxSheetBytes"/> the width and frame count are reduced until it fits or the floors
    /// (<see cref="MinWidth"/>, <see cref="MinFrames"/>) are hit, in which case null is returned.
    /// </summary>
    public static Sheet? BuildSheet(Image<Rgba32> image, int maxWidth, int maxFrames, int maxSheetBytes)
    {
        maxWidth = Math.Max(MinWidth, maxWidth);
        maxFrames = Math.Max(MinFrames, maxFrames);

        var sourceDelays = ReadDelays(image);
        var width = Math.Min(maxWidth, image.Width);
        var frames = Math.Min(maxFrames, image.Frames.Count);

        // Shrink by 0.75x width, then by 0.75x frames, alternating, until the sheet fits the budget.
        var shrinkWidthNext = true;
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var sheet = BuildSheetOnce(image, sourceDelays, width, frames);
            if (sheet.Png.Length <= maxSheetBytes)
                return sheet;

            var canShrinkWidth = width > MinWidth;
            var canShrinkFrames = frames > MinFrames;
            if (!canShrinkWidth && !canShrinkFrames)
                return null;

            if ((shrinkWidthNext && canShrinkWidth) || !canShrinkFrames)
                width = Math.Max(MinWidth, (int) (width * 0.75f));
            else
                frames = Math.Max(MinFrames, (int) (frames * 0.75f));

            shrinkWidthNext = !shrinkWidthNext;
        }

        return null;
    }

    private static Sheet BuildSheetOnce(Image<Rgba32> image, int[] sourceDelays, int targetWidth, int targetFrames)
    {
        var sourceCount = image.Frames.Count;
        targetFrames = Math.Clamp(targetFrames, 1, sourceCount);

        // Pick frame indices evenly across the animation (always keeps the first frame).
        var kept = new int[targetFrames];
        for (var i = 0; i < targetFrames; i++)
            kept[i] = (int) ((long) i * sourceCount / targetFrames);

        // Each kept frame absorbs the delays of the frames skipped until the next kept one.
        var delays = new int[targetFrames];
        for (var i = 0; i < targetFrames; i++)
        {
            var from = kept[i];
            var to = i + 1 < targetFrames ? kept[i + 1] : sourceCount;
            long sum = 0;
            for (var f = from; f < to; f++)
                sum += sourceDelays[f];
            delays[i] = (int) Math.Clamp(sum, 1, MaxFrameDelayMs);
        }

        // Scale to fit the width, keeping aspect; never upscale.
        var scale = Math.Min(1f, targetWidth / (float) image.Width);
        var frameW = Math.Max(1, (int) MathF.Round(image.Width * scale));
        var frameH = Math.Max(1, (int) MathF.Round(image.Height * scale));

        var columns = Math.Max(1, (int) Math.Ceiling(Math.Sqrt(targetFrames)));
        var rows = (targetFrames + columns - 1) / columns;

        using var sheet = new Image<Rgba32>(frameW * columns, frameH * rows, new Rgba32(0, 0, 0, 0));
        for (var i = 0; i < targetFrames; i++)
        {
            using var frame = image.Frames.CloneFrame(kept[i]);
            if (frameW != image.Width || frameH != image.Height)
                frame.Mutate(x => x.Resize(frameW, frameH, KnownResamplers.Bicubic));

            var px = (i % columns) * frameW;
            var py = (i / columns) * frameH;
            sheet.Mutate(x => x.DrawImage(frame, new Point(px, py), 1f));
        }

        using var ms = new MemoryStream();
        sheet.Save(ms, SheetEncoder);
        return new Sheet(ms.ToArray(), frameW, frameH, targetFrames, columns, delays);
    }

    /// <summary>Per-frame delays in milliseconds, from GIF (centiseconds) or WebP (milliseconds) metadata.</summary>
    private static int[] ReadDelays(Image<Rgba32> image)
    {
        var delays = new int[image.Frames.Count];
        for (var i = 0; i < delays.Length; i++)
        {
            var meta = image.Frames[i].Metadata;
            long ms = 0;

            if (meta.TryGetFormatMetadata(GifFormat.Instance, out GifFrameMetadata? gif) && gif != null)
                ms = gif.FrameDelay * 10L;
            else if (meta.TryGetFormatMetadata(WebpFormat.Instance, out WebpFrameMetadata? webp) && webp != null)
                ms = webp.FrameDelay;

            if (ms <= 0)
                ms = DefaultFrameDelayMs;

            delays[i] = (int) Math.Clamp(ms, 1, MaxFrameDelayMs);
        }

        return delays;
    }

    /// <summary>
    /// Makes a small PNG thumbnail (first frame, fitted inside <paramref name="size"/> x <paramref name="size"/>)
    /// of the given image bytes. Returns an empty array on failure.
    /// </summary>
    public static byte[] MakeThumbnail(byte[] data, int size)
    {
        try
        {
            using var image = DecodeFirstFrameBounded(data);
            if (image == null)
                return Array.Empty<byte>();

            var scale = Math.Min(1f, size / (float) Math.Max(image.Width, image.Height));
            var w = Math.Max(1, (int) MathF.Round(image.Width * scale));
            var h = Math.Max(1, (int) MathF.Round(image.Height * scale));
            if (w != image.Width || h != image.Height)
                image.Mutate(x => x.Resize(w, h, KnownResamplers.Bicubic));

            using var ms = new MemoryStream();
            image.Save(ms, ThumbnailEncoder);
            return ms.ToArray();
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    private static Image<Rgba32>? DecodeFirstFrameBounded(byte[] data)
    {
        var info = Image.Identify(data);
        if ((long) info.Width * info.Height > MaxSourcePixels || info.Width <= 0 || info.Height <= 0)
            return null;

        var options = new DecoderOptions { MaxFrames = 1 };
        return Image.Load<Rgba32>(options, data);
    }
}
