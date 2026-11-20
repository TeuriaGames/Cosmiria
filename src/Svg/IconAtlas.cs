using System.Numerics;
using Riateu.Graphics;

namespace Cosmiria;

/// <summary>
/// GPU atlas of rasterized icons, keyed by (icon, pixel width, pixel height): each icon is
/// rasterized once per size it's drawn at (crisp at any scale). When the atlas fills up it's
/// cleared and refilled with what's currently on screen.
/// </summary>
internal sealed class IconAtlas : IDisposable
{
    private readonly ShelfAtlas atlas;
    private readonly SvgRasterizer rasterizer = new();
    private readonly Dictionary<(int id, int w, int h), Vector4> entries = new();
    private byte[] scratch = new byte[64 * 64 * 4];

    public Texture Texture => atlas.Texture;

    /// <summary>Set when the atlas had to be cleared: the caller should repaint everything.</summary>
    public bool WasReset { get; set; }

    public IconAtlas(GraphicsDevice device) { atlas = new ShelfAtlas(device, 1024); }

    /// <summary>UV rect (u0, v0, u1, v1) of the icon at this pixel size, rasterizing it if needed.</summary>
    public bool TryGet(SvgIcon icon, int w, int h, out Vector4 uv)
    {
        w = Math.Clamp(w, 1, atlas.MaxSide);
        h = Math.Clamp(h, 1, atlas.MaxSide);
        if (entries.TryGetValue((icon.Id, w, h), out uv)) return true;

        if (!atlas.TryAdd(w, h, out byte[] pixels, out int stride, out uv))
        {
            // Full: start over. Everything visible gets re-rasterized on the next paint.
            entries.Clear();
            atlas.Clear();
            WasReset = true;
            if (!atlas.TryAdd(w, h, out pixels, out stride, out uv)) return false;
        }

        int bytes = w * h * 4;
        if (scratch.Length < bytes) scratch = new byte[bytes];
        Array.Clear(scratch, 0, bytes);
        rasterizer.Rasterize(icon, w, h, scratch.AsSpan(0, bytes));
        int origin = ShelfAtlas.Origin(stride);
        for (int row = 0; row < h; row++)
        {
            Buffer.BlockCopy(scratch, row * w * 4, pixels, origin + row * stride, w * 4);
        }
        entries[(icon.Id, w, h)] = uv;
        return true;
    }

    public void Upload(CommandBuffer cmd) => atlas.Upload(cmd);

    public void Dispose() => atlas.Dispose();
}
