using System.Numerics;
using System.Runtime.InteropServices;

namespace Cosmiria;

/// <summary>GPU instance data. Must match <c>UIQuad</c> in Shaders/common/ui.slang (160 bytes).</summary>
[StructLayout(LayoutKind.Explicit, Size = 160)]
internal struct UIQuad
{
    [FieldOffset(0)] public Vector4 Rect;
    [FieldOffset(16)] public Vector4 UV;
    [FieldOffset(32)] public Vector4 Color;
    [FieldOffset(48)] public Vector4 BorderColor;
    [FieldOffset(64)] public Vector4 Radius;
    [FieldOffset(80)] public float BorderWidth;
    [FieldOffset(84)] public uint Mode;
    /// <summary>Transform translation (M31, M32 of a Matrix3x2).</summary>
    [FieldOffset(88)] public Vector2 Translate;
    /// <summary>Transform linear part (M11, M12, M21, M22).</summary>
    [FieldOffset(96)] public Vector4 Matrix;
    /// <summary>Gradient end color (straight alpha).</summary>
    [FieldOffset(112)] public Vector4 Color2;
    /// <summary>Gradient middle color, used when <see cref="Gradient"/>.W is 1.</summary>
    [FieldOffset(128)] public Vector4 Color3;
    /// <summary>(direction x, direction y, kind: 0 none / 1 linear / 2 radial, has middle color).</summary>
    [FieldOffset(144)] public Vector4 Gradient;

    public const int SizeInBytes = 160;

    public const uint ModeRect = 0;
    public const uint ModeGlyph = 1;
    public const uint ModeImage = 2; // premultiplied RGBA texture × tint
    /// <summary>Box shadow: BorderWidth = sigma, Gradient = (offset x, offset y, spread), Radius = the box's radii.</summary>
    public const uint ModeShadow = 3;
}

[StructLayout(LayoutKind.Sequential)]
internal struct UIUniforms
{
    public Matrix4x4 ViewProjection;
    public uint QuadOffset;
    private uint pad0, pad1, pad2;
}
