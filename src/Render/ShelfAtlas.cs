using System.Numerics;
using Riateu.Graphics;

namespace Cosmiria;

/// <summary>
/// A GPU texture filled at runtime with small RGBA bitmaps, packed in shelves (rows) and
/// uploaded in the next frame's copy pass. Owners clear it (and repaint) when it fills up.
/// </summary>
internal sealed class ShelfAtlas : IDisposable
{
    private const int Pad = 1; // transparent border so linear filtering never bleeds

    private readonly GraphicsDevice device;
    private readonly List<(int x, int y, int w, int h, byte[] pixels)> pending = new();
    private TransferBuffer transfer;
    private uint transferSize;
    private int shelfX, shelfY, shelfHeight;

    public readonly int Size;
    public Texture Texture { get; }

    public ShelfAtlas(GraphicsDevice device, int size)
    {
        this.device = device;
        Size = size;
        Texture = new Texture(device, (uint)size, (uint)size, TextureFormat.R8G8B8A8_UNORM, TextureUsageFlags.Sampler);
    }

    /// <summary>Largest bitmap side that fits (including padding).</summary>
    public int MaxSide => Size - Pad * 2;

    /// <summary>
    /// Reserve a w×h cell and return its padded RGBA pixel buffer (row stride (w + 2) * 4, the
    /// bitmap starts at (1, 1)) plus its UV rect. False when the atlas is full.
    /// </summary>
    public bool TryAdd(int w, int h, out byte[] pixels, out int stride, out Vector4 uv)
    {
        int pw = w + Pad * 2, ph = h + Pad * 2;
        if (shelfX + pw > Size)
        {
            shelfY += shelfHeight;
            shelfX = 0;
            shelfHeight = 0;
        }
        if (shelfY + ph > Size || pw > Size)
        {
            pixels = null; stride = 0; uv = default;
            return false;
        }
        int x = shelfX, y = shelfY;
        shelfX += pw;
        shelfHeight = Math.Max(shelfHeight, ph);

        pixels = new byte[pw * ph * 4];
        stride = pw * 4;
        pending.Add((x, y, pw, ph, pixels));
        float s = Size;
        uv = new Vector4((x + Pad) / s, (y + Pad) / s, (x + Pad + w) / s, (y + Pad + h) / s);
        return true;
    }

    /// <summary>Byte offset of pixel (0, 0) of the bitmap inside a buffer from <see cref="TryAdd"/>.</summary>
    public static int Origin(int stride) => stride * Pad + Pad * 4;

    public void Clear()
    {
        pending.Clear();
        shelfX = shelfY = shelfHeight = 0;
    }

    /// <summary>Upload newly added bitmaps (call outside a render pass).</summary>
    public void Upload(CommandBuffer cmd)
    {
        if (pending.Count == 0) return;

        uint total = 0;
        foreach (var p in pending) total += (uint)p.pixels.Length;
        if (transfer == null || transferSize < total)
        {
            transfer?.Dispose();
            transferSize = Math.Max(total, 256 * 1024);
            transfer = new TransferBuffer(device, TransferBufferUsage.Upload, transferSize);
        }

        Span<byte> mapped = transfer.Map(true, 0);
        uint offset = 0;
        foreach (var p in pending)
        {
            p.pixels.CopyTo(mapped.Slice((int)offset));
            offset += (uint)p.pixels.Length;
        }
        transfer.Unmap();

        CopyPass copy = cmd.BeginCopyPass();
        offset = 0;
        foreach (var p in pending)
        {
            copy.UploadToTexture(
                new TextureTransferInfo(transfer, offset, (uint)p.w, (uint)p.h),
                new TextureRegion(Texture, (uint)p.x, (uint)p.y, 0, (uint)p.w, (uint)p.h, 1),
                false);
            offset += (uint)p.pixels.Length;
        }
        cmd.EndCopyPass(copy);
        pending.Clear();
    }

    public void Dispose()
    {
        transfer?.Dispose();
        Texture.Dispose();
    }
}
