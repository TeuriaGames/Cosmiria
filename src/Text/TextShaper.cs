using System.Runtime.InteropServices;
using HarfBuzzSharp;
using HBBuffer = HarfBuzzSharp.Buffer;
using HBFont = HarfBuzzSharp.Font;

namespace Cosmiria;

/// <summary>One glyph picked by the shaper, in font units at the font's base size.</summary>
internal struct ShapedGlyph
{
    /// <summary>Glyph index in the font (not a codepoint: ligatures and contextual forms have none).</summary>
    public int GlyphId;
    /// <summary>UTF-16 index of the first character this glyph comes from.</summary>
    public int Cluster;
    public float Advance;
    /// <summary>Offset from the pen position (marks over their base, kerning tweaks); y grows down.</summary>
    public float OffsetX, OffsetY;
}

/// <summary>
/// A paragraph shaped once: its glyphs, and an advance for every UTF-16 character so line
/// breaking, carets and selection work per character. A cluster's advance (a ligature, a letter
/// with its marks) is shared by the characters a caret can stop before; low surrogates and
/// combining marks get none, so the caret never lands inside a character or between a letter and
/// its accent.
/// </summary>
internal sealed class ShapedText
{
    public readonly string Text;
    /// <summary>Advance of each UTF-16 character, logical order.</summary>
    public readonly float[] CharAdvance;
    /// <summary>Sum of <see cref="CharAdvance"/> before each index (length + 1 entries).</summary>
    private readonly float[] prefix;
    /// <summary>Glyphs run by run in text order; within a right-to-left run, right to left (visual order).</summary>
    public readonly ShapedGlyph[] Glyphs;
    public readonly int GlyphCount;
    internal long LastUsed;

    public ShapedText(string text, float[] charAdvance, ShapedGlyph[] glyphs, int glyphCount)
    {
        Text = text;
        CharAdvance = charAdvance;
        Glyphs = glyphs;
        GlyphCount = glyphCount;
        prefix = new float[charAdvance.Length + 1];
        for (int i = 0; i < charAdvance.Length; i++) prefix[i + 1] = prefix[i] + charAdvance[i];
        Monotonic = true;
        for (int i = 1; i < glyphCount; i++)
            if (glyphs[i].Cluster < glyphs[i - 1].Cluster) { Monotonic = false; break; }
    }

    /// <summary>Glyph clusters never go backwards (no right-to-left run): a line's glyphs are one contiguous range.</summary>
    public readonly bool Monotonic;

    /// <summary>Where to start looking for the glyphs of characters from <paramref name="start"/> on.</summary>
    public int FirstGlyphAt(int start)
    {
        if (!Monotonic) return 0;
        int lo = 0, hi = GlyphCount;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (Glyphs[mid].Cluster < start) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    public int Length => CharAdvance.Length;

    /// <summary>Width of characters [<paramref name="start"/>, <paramref name="end"/>).</summary>
    public float Width(int start, int end)
    {
        start = Math.Clamp(start, 0, CharAdvance.Length);
        end = Math.Clamp(end, start, CharAdvance.Length);
        return prefix[end] - prefix[start];
    }

    /// <summary>Caret index in [<paramref name="start"/>, <paramref name="end"/>] closest to <paramref name="x"/> (from <paramref name="start"/>).</summary>
    public int IndexAt(int start, int end, float x)
    {
        float pen = 0;
        for (int i = start; i < end; i++)
        {
            float adv = CharAdvance[i];
            if (adv == 0) continue; // not a caret stop
            if (x < pen + adv * 0.5f) return i;
            pen += adv;
        }
        return end;
    }

    /// <summary>A caret may stop before index <paramref name="i"/> (not inside a character or before a mark).</summary>
    public bool IsCaretStop(int i) => i <= 0 || i >= CharAdvance.Length || CharAdvance[i] != 0;
}

/// <summary>
/// HarfBuzz shaping for one font: ligatures, kerning (GPOS and kern), mark positioning, and
/// complex scripts (Arabic joining, Indic reordering, ...). Text is split into runs of one script,
/// each shaped in its script's direction with the whole text as context. Results are cached by
/// text, so measuring and painting the same string again costs a lookup.
/// <para>
/// Not covered: reordering of mixed left-to-right and right-to-left runs on one line (each run
/// keeps its own direction, runs stay in text order), and fallback fonts for missing characters.
/// </para>
/// </summary>
internal sealed class TextShaper : IDisposable
{
    private const int Capacity = 4096;

    private readonly Blob blob;
    private readonly Face face;
    private readonly HBFont font;
    private readonly HBBuffer buffer = new();
    private readonly float unitScale; // font design units -> layout units (pixels at the base size)
    private readonly Dictionary<string, ShapedText> cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ShapedText>.AlternateLookup<ReadOnlySpan<char>> lookup;
    private long tick;

    private static readonly Feature[] NoFeatures = [];
    private ShapedGlyph[] glyphScratch = new ShapedGlyph[64];

    /// <param name="fontData">The font file.</param>
    /// <param name="pixelScale">Pixels per design unit at the base size (the rasterizer's scale, so advances agree).</param>
    public unsafe TextShaper(ReadOnlySpan<byte> fontData, float pixelScale)
    {
        fixed (byte* p = fontData)
            blob = new Blob((IntPtr)p, fontData.Length, MemoryMode.Duplicate); // HarfBuzz keeps its own copy
        face = new Face(blob, 0);
        font = new HBFont(face);
        font.SetScale(face.UnitsPerEm, face.UnitsPerEm); // positions in design units
        unitScale = pixelScale;
        lookup = cache.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>The shaped <paramref name="text"/> (cached).</summary>
    public ShapedText Shape(ReadOnlySpan<char> text)
    {
        tick++;
        if (lookup.TryGetValue(text, out ShapedText hit))
        {
            hit.LastUsed = tick;
            return hit;
        }
        if (cache.Count >= Capacity) Evict();
        string key = new string(text);
        ShapedText shaped = ShapeNew(key);
        shaped.LastUsed = tick;
        cache[key] = shaped;
        return shaped;
    }

    /// <summary>Drop the least recently used half (approximately: anything older than the median age).</summary>
    private void Evict()
    {
        long cutoff = tick - Capacity / 2;
        foreach (var pair in cache)
            if (pair.Value.LastUsed < cutoff) cache.Remove(pair.Key);
        if (cache.Count >= Capacity) cache.Clear(); // everything is recent: start over
    }

    private ShapedText ShapeNew(string text)
    {
        float[] advance = new float[text.Length];
        int count = 0;
        UnicodeFunctions unicode = UnicodeFunctions.Default;

        // Runs of one script. Common characters (spaces, digits, punctuation) and inherited ones
        // (combining marks) join the run they're in, so "Hello, world" is one run.
        int runStart = 0;
        Script runScript = Script.Common;
        for (int i = 0; i < text.Length;)
        {
            int cp = CodepointAt(text, i, out int units);
            Script script = unicode.GetScript(cp);
            bool neutral = script == Script.Common || script == Script.Inherited || script == Script.Unknown;
            if (!neutral)
            {
                if (runScript == Script.Common) runScript = script;
                else if (script != runScript)
                {
                    count = ShapeRun(text, runStart, i, advance, count);
                    runStart = i;
                    runScript = script;
                }
            }
            i += units;
        }
        if (text.Length > 0) count = ShapeRun(text, runStart, text.Length, advance, count);

        var glyphs = new ShapedGlyph[count];
        Array.Copy(glyphScratch, glyphs, count);
        return new ShapedText(text, advance, glyphs, count);
    }

    /// <summary>Shape [start, end) of <paramref name="text"/> and append its glyphs; fills the advances of its characters.</summary>
    private int ShapeRun(string text, int start, int end, float[] advance, int count)
    {
        buffer.ClearContents();
        // The whole text is context (for joining across runs); clusters are indices into it.
        buffer.AddUtf16(text.AsSpan(), start, end - start);
        buffer.GuessSegmentProperties();
        font.Shape(buffer, NoFeatures);

        ReadOnlySpan<GlyphInfo> infos = buffer.GetGlyphInfoSpan();
        ReadOnlySpan<GlyphPosition> positions = buffer.GetGlyphPositionSpan();
        if (glyphScratch.Length < count + infos.Length)
            Array.Resize(ref glyphScratch, Math.Max(glyphScratch.Length * 2, count + infos.Length));

        int first = count;
        for (int g = 0; g < infos.Length; g++)
        {
            int cluster = (int)infos[g].Cluster;
            ref ShapedGlyph o = ref glyphScratch[count++];
            o.GlyphId = (int)infos[g].Codepoint;
            o.Cluster = cluster;
            o.Advance = positions[g].XAdvance * unitScale;
            o.OffsetX = positions[g].XOffset * unitScale;
            o.OffsetY = -positions[g].YOffset * unitScale; // HarfBuzz y grows up
            char c = text[cluster];
            if (c < 0x20 || c == 0x7F)
            {
                // Control characters draw nothing; a tab is as wide as four spaces.
                o.GlyphId = -1;
                o.Advance = c == '\t' ? TabWidth() : 0;
                o.OffsetX = o.OffsetY = 0;
            }
        }

        DistributeAdvances(text, start, end, glyphScratch.AsSpan(first, count - first), advance);
        return count;
    }

    private float tabWidth = -1;

    private float TabWidth()
    {
        if (tabWidth >= 0) return tabWidth;
        tabWidth = 0;
        if (font.TryGetGlyph(' ', out uint space))
        {
            font.GetGlyphAdvanceForDirection(space, Direction.LeftToRight, out int x, out _);
            tabWidth = x * unitScale * 4;
        }
        return tabWidth;
    }

    /// <summary>
    /// Give each character of [start, end) its share of its cluster's advance. A cluster runs from
    /// its first character to the next cluster's start; its advance goes to the characters a caret
    /// can stop before (all of them for a ligature, so "ffi" has three carets), not to low
    /// surrogates or combining marks.
    /// </summary>
    private static void DistributeAdvances(string text, int start, int end, Span<ShapedGlyph> glyphs, float[] advance)
    {
        // Sum glyph advances into each cluster's first character.
        for (int i = start; i < end; i++) advance[i] = 0;
        Span<bool> isStart = end - start <= 1024 ? stackalloc bool[end - start] : new bool[end - start];
        foreach (ref readonly ShapedGlyph g in glyphs)
        {
            if (g.Cluster < start || g.Cluster >= end) continue;
            advance[g.Cluster] += g.Advance;
            isStart[g.Cluster - start] = true;
        }

        UnicodeFunctions unicode = UnicodeFunctions.Default;
        int c = start;
        while (c < end)
        {
            int next = c + 1;
            while (next < end && !isStart[next - start]) next++;
            float total = advance[c];
            int stops = 0;
            for (int i = c; i < next; i++) if (IsStop(text, i, c, unicode)) stops++;
            float share = stops > 0 ? total / stops : 0;
            for (int i = c; i < next; i++) advance[i] = IsStop(text, i, c, unicode) ? share : 0;
            if (stops == 0) advance[c] = total;
            c = next;
        }
    }

    private static bool IsStop(string text, int i, int clusterStart, UnicodeFunctions unicode)
    {
        char ch = text[i];
        if (char.IsLowSurrogate(ch) && i > 0 && char.IsHighSurrogate(text[i - 1])) return false;
        if (i == clusterStart) return true;
        int cp = CodepointAt(text, i, out _);
        UnicodeGeneralCategory cat = unicode.GetGeneralCategory(cp);
        return cat != UnicodeGeneralCategory.NonSpacingMark && cat != UnicodeGeneralCategory.SpacingMark
            && cat != UnicodeGeneralCategory.EnclosingMark && cat != UnicodeGeneralCategory.Format;
    }

    private static int CodepointAt(string text, int i, out int units)
    {
        char c = text[i];
        if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
        {
            units = 2;
            return char.ConvertToUtf32(c, text[i + 1]);
        }
        units = 1;
        return c;
    }

    public void Dispose()
    {
        buffer.Dispose();
        font.Dispose();
        face.Dispose();
        blob.Dispose();
    }
}
