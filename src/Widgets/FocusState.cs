namespace Cosmiria;

/// <summary>Text input focus, shared between the context (input) and the renderer (caret, scroll).</summary>
internal sealed class FocusState
{
    public int FocusedId;      // stable id of the focused element (0 = none); text editing when it's a TextInput
    public bool Visible;       // focus came from the keyboard: draw the focus ring (CSS :focus-visible)
    public string TooltipText; // tooltip being shown (null = none)
    public float TooltipX, TooltipY;
    public int ScrollbarHot, ScrollbarActive; // stable ids of the container whose scrollbar is hovered / dragged
    public float ScrollX;      // horizontal scroll of the focused input, in pixels
    public bool CaretVisible = true;
}
