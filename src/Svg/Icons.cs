namespace Cosmiria;

/// <summary>
/// Built-in icons (24×24, 2px round strokes in currentColor, so they take the text color).
/// Used by the widgets; available to apps too: <c>ui.Icon(Icons.Search, 16)</c>.
/// </summary>
public static class Icons
{
    private const string Head = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\">";

    private static SvgIcon Make(string body) => SvgIcon.Parse(Head + body + "</svg>");

    public static SvgIcon Check { get; } = Make("<path d=\"M20 6 9 17l-5-5\"/>");
    public static SvgIcon X { get; } = Make("<path d=\"M18 6 6 18M6 6l12 12\"/>");
    public static SvgIcon ChevronDown { get; } = Make("<path d=\"m6 9 6 6 6-6\"/>");
    public static SvgIcon ChevronUp { get; } = Make("<path d=\"m18 15-6-6-6 6\"/>");
    public static SvgIcon ChevronLeft { get; } = Make("<path d=\"m15 18-6-6 6-6\"/>");
    public static SvgIcon ChevronRight { get; } = Make("<path d=\"m9 18 6-6-6-6\"/>");
    public static SvgIcon Plus { get; } = Make("<path d=\"M12 5v14M5 12h14\"/>");
    public static SvgIcon Minus { get; } = Make("<path d=\"M5 12h14\"/>");
    public static SvgIcon Search { get; } = Make("<circle cx=\"11\" cy=\"11\" r=\"7\"/><path d=\"m20 20-4-4\"/>");
    public static SvgIcon Trash { get; } = Make("<path d=\"M3 6h18M8 6V4h8v2M6 6l1 14h10l1-14\"/>");
    public static SvgIcon Info { get; } = Make("<circle cx=\"12\" cy=\"12\" r=\"10\"/><path d=\"M12 16v-4M12 8h.01\"/>");
    public static SvgIcon Circle { get; } = Make("<circle cx=\"12\" cy=\"12\" r=\"9\"/>");
    public static SvgIcon Dot { get; } = Make("<circle cx=\"12\" cy=\"12\" r=\"5\" fill=\"currentColor\" stroke=\"none\"/>");
    public static SvgIcon Heart { get; } = Make("<path fill=\"currentColor\" stroke=\"none\" d=\"M12 21s-7.5-4.6-9.6-9.3A5.4 5.4 0 0 1 12 5.6a5.4 5.4 0 0 1 9.6 6.1C19.5 16.4 12 21 12 21z\"/>");
    public static SvgIcon Star { get; } = Make("<polygon fill=\"currentColor\" stroke=\"none\" points=\"12 2 15.1 8.3 22 9.3 17 14.1 18.2 21 12 17.8 5.8 21 7 14.1 2 9.3 8.9 8.3\"/>");
    public static SvgIcon Home { get; } = Make("<path d=\"M3 10.5 12 3l9 7.5V20a1 1 0 0 1-1 1h-5v-6h-6v6H4a1 1 0 0 1-1-1z\"/>");
}
