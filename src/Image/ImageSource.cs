using Riateu.Graphics;

namespace Cosmiria;

/// <summary>How an image fills its element's content box (CSS object-fit).</summary>
public enum ImageFit : byte
{
    /// <summary>Stretch to the box (the default). With only a width or height set, the box already has the image's aspect ratio.</summary>
    Fill,
    /// <summary>Scale to fit inside the box, keeping the aspect ratio (letterboxed).</summary>
    Contain,
    /// <summary>Scale to cover the box, keeping the aspect ratio (cropped, centered).</summary>
    Cover,
}

/// <summary>How an image's pixels are filtered when it's drawn at another size.</summary>
public enum ImageSampling : byte
{
    /// <summary>Smooth (bilinear, plus mipmaps for <see cref="ImageSource"/> files).</summary>
    Linear,
    /// <summary>Hard pixel edges (nearest neighbor): pixel art and game screens scaled up.</summary>
    Nearest,
}

public static class ImageFitExtensions
{
    /// <summary>
    /// Map a pointer event on an image element to the image's pixel coordinates (e.g. a game
    /// screen drawn with <see cref="ImageFit.Contain"/>), undoing the fit's scaling, letterboxing
    /// and cropping. The result can be outside [0, width) × [0, height) in the letterbox bars.
    /// Assumes the element has no padding or border.
    /// </summary>
    public static System.Numerics.Vector2 ToPixel(this ImageFit fit, in PointerEvent e, float imageWidth, float imageHeight)
    {
        float w = e.Size.X, h = e.Size.Y;
        if (w <= 0 || h <= 0 || imageWidth <= 0 || imageHeight <= 0) return default;
        System.Numerics.Vector2 p = e.LocalPosition;
        switch (fit)
        {
            case ImageFit.Contain:
            case ImageFit.Cover:
            {
                float k = fit == ImageFit.Contain ? MathF.Min(w / imageWidth, h / imageHeight) : MathF.Max(w / imageWidth, h / imageHeight);
                float ox = (w - imageWidth * k) * 0.5f, oy = (h - imageHeight * k) * 0.5f;
                return new System.Numerics.Vector2((p.X - ox) / k, (p.Y - oy) / k);
            }
            default:
                return new System.Numerics.Vector2(p.X / w * imageWidth, p.Y / h * imageHeight);
        }
    }
}

/// <summary>
/// A raster image for <see cref="UIStack.Image(ImageSource, ImageFit, in Style)"/>. Decoded on the
/// CPU once; the UI uploads it to the GPU with a full mipmap chain the first time it's drawn,
/// so it stays smooth (no shimmering or aliasing) when drawn smaller than its pixel size.
/// Drawn larger than that, a raster image can only be interpolated: use an SVG for artwork
/// that must stay sharp at any size.
/// </summary>
public sealed class ImageSource
{
    private static readonly Dictionary<string, ImageSource> files = new();
    private static int nextId;

    internal readonly int Id = Interlocked.Increment(ref nextId);
    /// <summary>Premultiplied RGBA pixels, or null for a texture source or a failed load.</summary>
    internal byte[] Pixels;
    internal Texture External;
    /// <summary>Bumped by <see cref="Update"/> so the GPU copy is refreshed.</summary>
    internal int Version;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public float AspectRatio => Height > 0 ? Width / (float)Height : 1f;
    /// <summary>False when the file couldn't be loaded (nothing is drawn).</summary>
    public bool IsValid => Width > 0 && Height > 0;
    public string Path { get; private set; }

    private ImageSource() { }

    /// <summary>
    /// Load PNG, JPEG, BMP, TGA, … from <paramref name="path"/>, relative to the app's directory
    /// (<see cref="AppContext.BaseDirectory"/>) unless absolute. Cached: the same path returns the
    /// same source. A missing or unreadable file gives an invalid source and a warning on stderr.
    /// </summary>
    public static ImageSource FromFile(string path)
    {
        lock (files)
        {
            if (files.TryGetValue(path, out var cached)) return cached;
            var source = new ImageSource { Path = path };
            string full = System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(AppContext.BaseDirectory, path);
            try
            {
                using var image = new Image(full);
                source.SetPixels(image.Pixels, image.Width, image.Height);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[Cosmiria] Couldn't load image '{full}': {e.Message}");
            }
            files[path] = source;
            return source;
        }
    }

    /// <summary>An image from straight-alpha RGBA pixels (copied).</summary>
    public static ImageSource FromPixels(ReadOnlySpan<Color> pixels, int width, int height)
    {
        var source = new ImageSource();
        source.SetPixels(pixels, width, height);
        return source;
    }

    /// <summary>
    /// Draw an existing GPU texture (e.g. a render target of your game). It's sampled as is:
    /// premultiplied alpha, and mipmapped only if you generated its mip levels.
    /// </summary>
    public static ImageSource FromTexture(Texture texture) =>
        new() { External = texture, Width = (int)texture.Width, Height = (int)texture.Height };

    private Dictionary<(int, int, int, int), ImageSource> regions;

    /// <summary>
    /// A part of this image, e.g. one sprite of an atlas: (<paramref name="x"/>, <paramref name="y"/>)
    /// is its top-left corner in pixels, clamped to the image. Cached per rectangle, so calling it
    /// on every render is cheap. A texture-backed or invalid source gives an invalid image.
    /// </summary>
    public ImageSource Region(int x, int y, int width, int height)
    {
        if (Pixels == null) return new ImageSource();
        x = Math.Clamp(x, 0, Width);
        y = Math.Clamp(y, 0, Height);
        width = Math.Min(width, Width - x);
        height = Math.Min(height, Height - y);
        if (width <= 0 || height <= 0) return new ImageSource();

        lock (this)
        {
            regions ??= new();
            if (regions.TryGetValue((x, y, width, height), out var cached)) return cached;
            var bytes = new byte[width * height * 4];
            for (int row = 0; row < height; row++)
                Pixels.AsSpan(((y + row) * Width + x) * 4, width * 4).CopyTo(bytes.AsSpan(row * width * 4));
            var region = new ImageSource { Pixels = bytes, Width = width, Height = height };
            regions[(x, y, width, height)] = region;
            return region;
        }
    }

    /// <summary>Replace the pixels (same or new size); the GPU copy is refreshed on the next paint. Call <c>ui.Notify()</c> to repaint.</summary>
    public void Update(ReadOnlySpan<Color> pixels, int width, int height)
    {
        if (External != null) throw new InvalidOperationException("A texture-backed ImageSource can't be updated with pixels.");
        SetPixels(pixels, width, height);
        Version++;
    }

    private void SetPixels(ReadOnlySpan<Color> pixels, int width, int height)
    {
        if (width <= 0 || height <= 0 || pixels.Length < width * height)
            throw new ArgumentException("Pixel count doesn't match the size.");
        var bytes = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            Color c = pixels[i];
            int a = c.A;
            bytes[i * 4] = (byte)((c.R * a + 127) / 255);
            bytes[i * 4 + 1] = (byte)((c.G * a + 127) / 255);
            bytes[i * 4 + 2] = (byte)((c.B * a + 127) / 255);
            bytes[i * 4 + 3] = (byte)a;
        }
        Pixels = bytes;
        Width = width;
        Height = height;
    }
}

/// <summary>GPU textures for <see cref="ImageSource"/>s, with mipmaps, created on first draw.</summary>
internal sealed class ImageCache : IDisposable
{
    private sealed class Entry
    {
        public Texture Texture;
        public uint Levels;
        public int Version = -1;
        public int LastUsed;
        public bool NeedsUpload;
    }

    /// <summary>Paints an image may go unused before its texture is freed (re-uploaded on demand).</summary>
    private const int EvictAfter = 600;

    private readonly GraphicsDevice device;
    private readonly Dictionary<int, (ImageSource source, Entry entry)> entries = new();
    private readonly List<int> dead = new();
    private int paint;

    public ImageCache(GraphicsDevice device) { this.device = device; }

    public void BeginPaint() => paint++;

    public Texture Get(ImageSource source)
    {
        if (source.External != null) return source.External;
        if (source.Pixels == null) return null;
        if (!entries.TryGetValue(source.Id, out var e))
        {
            e = (source, new Entry());
            entries[source.Id] = e;
        }
        Entry entry = e.entry;
        entry.LastUsed = paint;
        if (entry.Version != source.Version || entry.Texture == null)
        {
            if (entry.Texture != null && (entry.Texture.Width != source.Width || entry.Texture.Height != source.Height))
            {
                entry.Texture.Dispose();
                entry.Texture = null;
            }
            if (entry.Texture == null)
            {
                uint levels = 1 + (uint)Math.Floor(Math.Log2(Math.Max(source.Width, source.Height)));
                entry.Levels = levels;
                entry.Texture = new Texture(device, new TextureCreateInfo
                {
                    Width = (uint)source.Width,
                    Height = (uint)source.Height,
                    LayerCountOrDepth = 1,
                    TextureType = TextureType.Texture2D,
                    LevelCount = levels,
                    SampleCount = SampleCount.One,
                    Format = TextureFormat.R8G8B8A8_UNORM,
                    // Mipmap generation blits between levels, which needs ColorTarget.
                    UsageFlags = TextureUsageFlags.Sampler | TextureUsageFlags.ColorTarget,
                });
            }
            entry.Version = source.Version;
            entry.NeedsUpload = true;
        }
        return entry.Texture;
    }

    /// <summary>Upload new or changed images and build their mip chains (outside a render pass).</summary>
    public void Upload(CommandBuffer cmd)
    {
        foreach (var (id, (source, entry)) in entries)
        {
            if (paint - entry.LastUsed > EvictAfter) { dead.Add(id); continue; }
            if (!entry.NeedsUpload) continue;
            entry.NeedsUpload = false;

            using var transfer = new TransferBuffer(device, TransferBufferUsage.Upload, (uint)source.Pixels.Length);
            source.Pixels.CopyTo(transfer.Map(true, 0));
            transfer.Unmap();
            CopyPass copy = cmd.BeginCopyPass();
            copy.UploadToTexture(
                new TextureTransferInfo(transfer, 0, (uint)source.Width, (uint)source.Height),
                new TextureRegion(entry.Texture, 0, 0, 0, (uint)source.Width, (uint)source.Height, 1),
                false);
            cmd.EndCopyPass(copy);
            if (entry.Levels > 1)
                SDL3.SDL.SDL_GenerateMipmapsForGPUTexture(cmd.Handle, entry.Texture.Handle);
        }
        foreach (int id in dead)
        {
            entries[id].entry.Texture?.Dispose();
            entries.Remove(id);
        }
        dead.Clear();
    }

    public void Dispose()
    {
        foreach (var (_, (_, entry)) in entries) entry.Texture?.Dispose();
        entries.Clear();
    }
}
