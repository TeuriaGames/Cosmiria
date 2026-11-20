using Riateu.Graphics;

namespace Cosmiria;

/// <summary>
/// Default styles used by built-in elements and widgets. Change fields before the next rebuild
/// (e.g. right after creating the <see cref="UIContext"/>) to restyle every widget.
/// </summary>
public sealed class Theme
{
    private static Style S => default;

    public Color Text = new Color(0xE6E6E6FF);
    public uint Accent = Tailwind.Blue500;

    // ---- Button / ToggleButton
    public Style Button = S
        .FlexRow().ItemsCenter().JustifyCenter().Gap(6)
        .Px(14).Py(7)
        .Bg(0x3B4252FF)
        .Border(1, 0x4C566AFF)
        .Rounded(6)
        .NoWrap(); // labels stay on one line (inherited by the label text)

    public Style ButtonHover = S.Bg(0x4C566AFF);
    public Style ButtonActive = S.Bg(0x2E3440FF);
    public Style TogglePressed = S.Bg(Tailwind.Blue600).BorderColorOf(Tailwind.Blue400).TextColorOf(Tailwind.White);

    // ---- Checkbox / RadioButton / Switch (a row: control + optional label)
    public Style ControlRow = S.FlexRow().ItemsCenter().Gap(8).NoWrap();
    public Style ControlRowHover = S.TextColorOf(Tailwind.White);

    public Style Checkbox = S.Size(18).Shrink(0).Rounded(4).Border(2, Tailwind.Slate500).ItemsCenter().JustifyCenter();
    public Style CheckboxChecked = S.Bg(Tailwind.Blue500).BorderColorOf(Tailwind.Blue500).TextColorOf(Tailwind.White);

    public Style Radio = S.Size(18).Shrink(0).RoundedFull().Border(2, Tailwind.Slate500).ItemsCenter().JustifyCenter();
    public Style RadioSelected = S.BorderColorOf(Tailwind.Blue500);
    public Style RadioDot = S.Size(8).RoundedFull().Bg(Tailwind.Blue500);

    public Style SwitchTrack = S.W(36).H(20).Shrink(0).RoundedFull().Bg(Tailwind.Slate600).Relative();
    public Style SwitchTrackOn = S.Bg(Tailwind.Blue500);
    public Style SwitchKnob = S.Size(16).RoundedFull().Bg(Tailwind.White).Absolute().Top(2);

    // ---- ComboBox
    public Style ComboBox = S.FlexCol().Relative().W(200);
    public Style ComboField = S.FlexRow().ItemsCenter().Gap(8).Px(10).Py(6)
        .Bg(Tailwind.Slate800).Border(1, Tailwind.Slate600).Rounded(6).NoWrap();
    public Style ComboFieldHover = S.BorderColorOf(Tailwind.Slate400);
    public Style ComboPopup = S.FlexCol().Gap(2).Padding(4).Mt(4)
        .Bg(Tailwind.Slate800).Border(1, Tailwind.Slate600).Rounded(6)
        .MaxH(220).OverflowScroll().NoWrap();
    public Style ComboOption = S.FlexRow().ItemsCenter().Gap(8).Px(8).Py(5).Rounded(4);
    public Style ComboOptionHover = S.Bg(Tailwind.Slate700);
    public Style ComboOptionSelected = S.Bg(Tailwind.WithOpacity(Tailwind.Blue500, 0.35f)).TextColorOf(Tailwind.White);

    // ---- Slider (a knob on a track). Keep the root's horizontal padding at half its height:
    // Slider.Fraction maps the pointer over the width minus that inset, where the knob center travels.
    public Style Slider = S.FlexRow().ItemsCenter().Relative().W(200).H(16).Px(8).Shrink(0);
    public Style SliderTrack = S.Grow().H(4).RoundedFull().Bg(Tailwind.Slate600).Relative();
    public Style SliderFill = S.Absolute().Left(0).Top(0).Bottom(0).RoundedFull().Bg(Tailwind.Blue500);
    public Style SliderKnob = S.Absolute().Top(-6).Ml(-8).Size(16).RoundedFull().Bg(Tailwind.White).Border(2, Tailwind.Blue500);

    // Vertical slider: same pieces turned upright (keep Py at half the width).
    public Style SliderVertical = S.FlexCol().ItemsCenter().Relative().W(16).H(160).Py(8).Shrink(0);
    public Style SliderTrackVertical = S.Grow().W(4).RoundedFull().Bg(Tailwind.Slate600).Relative();
    public Style SliderFillVertical = S.Absolute().Left(0).Right(0).Bottom(0).RoundedFull().Bg(Tailwind.Blue500);
    public Style SliderKnobVertical = S.Absolute().Left(-6).Mt(-8).Size(16).RoundedFull().Bg(Tailwind.White).Border(2, Tailwind.Blue500);

    // ---- ProgressBar
    public Style Progress = S.W(200).H(8).Shrink(0).RoundedFull().Bg(Tailwind.Slate700).Relative().OverflowHidden();
    public Style ProgressVertical = S.W(8).H(120).Shrink(0).RoundedFull().Bg(Tailwind.Slate700).Relative().OverflowHidden();
    public Style ProgressFill = S.Absolute().Left(0).Top(0).Bottom(0).RoundedFull().Bg(Tailwind.Blue500);
    public Style ProgressFillVertical = S.Absolute().Left(0).Right(0).Bottom(0).RoundedFull().Bg(Tailwind.Blue500);

    // ---- Tabs (a bar of tab headers above the selected tab's panel)
    public Style Tabs = S.FlexCol().Gap(8);
    public Style TabBar = S.FlexRow().Gap(2).Padding(3).Bg(Tailwind.Slate800).Rounded(6).SelfStart();
    public Style Tab = S.FlexRow().ItemsCenter().Gap(6).Px(12).Py(5).Rounded(4).NoWrap().TextColorOf(Tailwind.Slate400);
    public Style TabHover = S.Bg(Tailwind.Slate700).TextColorOf(Tailwind.White);
    public Style TabSelected = S.Bg(Tailwind.Slate600).TextColorOf(Tailwind.White);
    public Style TabPanel = S.FlexCol().Gap(8);

    // ---- Split (two panes and a draggable divider)
    public Style Split = S.MinW(0).MinH(0);
    public Style SplitPane = S.FlexCol().MinW(0).MinH(0).OverflowHidden();
    public Style SplitHandle = S.Shrink(0).Bg(Tailwind.Slate700);
    public Style SplitHandleHover = S.Bg(Tailwind.WithOpacity(Tailwind.Blue500, 0.7f));
    /// <summary>Divider thickness in pixels.</summary>
    public float SplitHandleSize = 5;

    // ---- Docking
    public Style DockGroup = S.FlexCol().Grow().MinW(0).MinH(0).Relative().Bg(Tailwind.Slate900);
    public Style DockTabBar = S.FlexRow().Gap(2).Padding(3).Bg(Tailwind.Slate800).Shrink(0);
    public Style DockContent = S.FlexCol().Gap(8).Padding(8).Grow().MinW(0).MinH(0).OverflowScroll();
    /// <summary>The area the dragged panel will take, shown while an anchor is hovered.</summary>
    public Style DockDropPreview = S.Bg(Tailwind.WithOpacity(Tailwind.Blue500, 0.25f)).Border(2, Tailwind.Blue400).Rounded(4);
    /// <summary>One button of the anchor cross shown over the group under a dragged tab.</summary>
    public Style DockAnchor = S.Size(34).Padding(5).Rounded(5).Bg(Tailwind.WithOpacity(Tailwind.Slate800, 0.9f)).Border(1, Tailwind.Blue400).Shrink(0);
    public Style DockAnchorHover = S.Bg(Tailwind.WithOpacity(Tailwind.Blue600, 0.9f)).Border(1, Tailwind.Blue300);
    /// <summary>The little window drawn inside an anchor, and the part of it the panel would fill.</summary>
    public Style DockAnchorFrame = S.Grow().Relative().Rounded(2).Border(1, Tailwind.Blue300);
    public Style DockAnchorFill = S.Absolute().Bg(Tailwind.WithOpacity(Tailwind.Blue300, 0.8f)).Rounded(1);
    /// <summary>Space between the anchors of the cross.</summary>
    public float DockAnchorGap = 4;
    /// <summary>A floating window's frame, and the grip in its bottom-right corner that resizes it.</summary>
    public Style DockFloat = S.FlexCol().Bg(Tailwind.Slate900).Border(1, Tailwind.Slate500).Rounded(3)
        .Shadow(0, 12, 32, 0, Tailwind.WithOpacity(Tailwind.Black, 0.6f));
    /// <summary>The × on a closable panel's tab (<c>dock.Panel(..., closable: true)</c>), and its padding beside it.</summary>
    public Style DockTabClose = S.Size(16).Shrink(0).FlexRow().ItemsCenter().JustifyCenter().Rounded(3).TextColorOf(Tailwind.Slate500).Mr(-6);
    public Style DockTabCloseHover = S.Bg(Tailwind.Slate600).TextColorOf(Tailwind.White);
    /// <summary>The close button of a floating window whose panels are all closable.</summary>
    public Style DockFloatClose = S.Size(22).Shrink(0).SelfCenter().FlexRow().ItemsCenter().JustifyCenter().Rounded(4).TextColorOf(Tailwind.Slate400);
    public Style DockFloatCloseHover = S.Bg(Tailwind.Red600).TextColorOf(Tailwind.White);
    public Style DockFloatGrip = S.Size(12).Rounded(2).Bg(Tailwind.WithOpacity(Tailwind.Slate400, 0.3f));
    public Style DockFloatGripHover = S.Bg(Tailwind.WithOpacity(Tailwind.Blue400, 0.8f));
    /// <summary>Added to a floating window while it's dragged (see-through, so the drop targets under it show).</summary>
    public Style DockFloatDragging = S.Opacity(0.7f);
    /// <summary>Where a tab dropped on another group's tab bar goes (a bar at the edge of a tab).</summary>
    public Style DockTabMarker = S.Absolute().W(3).Top(2).Bottom(2).Rounded(1).Bg(Tailwind.Blue400);

    // ---- Focus ring, scrollbars, tooltips
    public uint FocusRing = Tailwind.Blue400;
    public uint ScrollThumb = Tailwind.WithOpacity(Tailwind.Slate400, 0.35f);
    public uint ScrollThumbHover = Tailwind.WithOpacity(Tailwind.Slate300, 0.6f);
    public uint ScrollThumbActive = Tailwind.WithOpacity(Tailwind.Slate200, 0.8f);
    public uint TooltipBackground = Tailwind.Slate950;
    public uint TooltipBorder = Tailwind.Slate600;
    public uint TooltipText = Tailwind.Slate100;
    public float TooltipTextSize = 14;
    public float TooltipMaxWidth = 320;

    // ---- Modal
    public Style ModalBackdrop = S.Fixed().Inset(0).Overlay().FlexCol().ItemsCenter().JustifyCenter().Bg(Tailwind.WithOpacity(Tailwind.Black, 0.55f));
    public Style ModalBox = S.FlexCol().Gap(12).Padding(18).MinW(320).MaxW(Length.Percent(90)).MaxH(Length.Percent(90))
        .Bg(Tailwind.Slate800).Border(1, Tailwind.Slate600).Rounded(10);
    public Style ModalHeader = S.FlexRow().ItemsCenter().Gap(8);
    public Style ModalTitle = S.Grow().TextSizeOf(18).TextColorOf(Tailwind.White);
    public Style ModalClose = S.Px(6).Py(4).Rounded(4).ItemsCenter().JustifyCenter();
    public Style ModalCloseHover = S.Bg(Tailwind.Slate700);
    public Style ModalBody = S.FlexCol().Gap(10).MinH(0).OverflowScroll();

    // ---- Menus
    public Style MenuBar = S.FlexRow().ItemsCenter().Gap(2).Px(4).Py(2).Bg(Tailwind.Slate800).Shrink(0);
    public Style MenuTitle = S.Relative().Px(10).Py(4).Rounded(4).NoWrap();
    public Style MenuTitleHover = S.Bg(Tailwind.Slate700);
    public Style MenuTitleOpen = S.Bg(Tailwind.Slate600);
    public Style MenuPopup = S.FlexCol().Padding(4).MinW(180).Bg(Tailwind.Slate800).Border(1, Tailwind.Slate600).Rounded(6).NoWrap();
    public Style MenuItem = S.Relative().FlexRow().ItemsCenter().Gap(16).Px(10).Py(5).Rounded(4);
    public Style MenuItemHover = S.Bg(Tailwind.WithOpacity(Tailwind.Blue500, 0.35f)).TextColorOf(Tailwind.White);
    public Style MenuShortcut = S.TextColorOf(Tailwind.Slate400).TextSizeOf(14);
    public Style MenuSeparator = S.H(1).Mx(6).My(4).Bg(Tailwind.Slate600);

    // ---- Tree
    public Style Tree = S.FlexCol();
    public Style TreeRow = S.FlexRow().ItemsCenter().Gap(4).Py(3).Pr(8).Rounded(4).NoWrap();
    public Style TreeRowHover = S.Bg(Tailwind.Slate700);
    public Style TreeRowSelected = S.Bg(Tailwind.WithOpacity(Tailwind.Blue500, 0.35f)).TextColorOf(Tailwind.White);
    public float TreeIndent = 16;

    // ---- NumberInput (a text field and -/+ step buttons)
    public Style NumberInput = S.FlexRow().ItemsCenter().Gap(4).W(220);
    public Style NumberField = S.Grow().W(0).MinW(48);
    public Style NumberStep = S.Px(8).Py(3).Shrink(0);

    // ---- Separator, Selectable, Labeled, Collapsible
    public Style Separator = S.FlexRow().ItemsCenter().Gap(6).Mt(4);
    public Style SeparatorLabel = S.TextColorOf(Tailwind.Slate300).TextSizeOf(13).NoWrap();
    public Style SeparatorLine = S.Grow().H(1).Bg(Tailwind.Slate600);
    public Style Selectable = S.FlexRow().ItemsCenter().Gap(6).Px(6).Py(3).Rounded(4).NoWrap();
    public Style SelectableHover = S.Bg(Tailwind.Slate700);
    public Style SelectableSelected = S.Bg(Tailwind.WithOpacity(Tailwind.Blue500, 0.35f)).TextColorOf(Tailwind.White);
    public Style Labeled = S.FlexRow().ItemsCenter().Gap(6);
    public Style Label = S.W(120).Shrink(0).TextColorOf(Tailwind.Slate300).NoWrap();
    public Style Collapsible = S.FlexCol().Gap(6);
    public Style CollapsibleHeader = S.FlexRow().ItemsCenter().Gap(6).Px(6).Py(3).Rounded(4).NoWrap();
    public Style CollapsibleHeaderHover = S.Bg(Tailwind.Slate700);
    public Style CollapsibleBody = S.FlexCol().Gap(6).Pl(16);

    // ---- Disabled elements
    public Style Disabled = S.Opacity(0.4f);

    // ---- TextInput
    public Style TextInput = S.Px(10).Py(6).W(220).Bg(Tailwind.Slate900).Border(1, Tailwind.Slate600).Rounded(6).NoWrap();
    public Style TextInputHover = S.BorderColorOf(Tailwind.Slate400);
    public Style TextInputFocus = S.BorderColorOf(Tailwind.Blue500);
    public Style TextArea = S.Px(10).Py(6).W(220).Bg(Tailwind.Slate900).Border(1, Tailwind.Slate600).Rounded(6).TextWrap();
    public uint Placeholder = Tailwind.Slate500;
    public uint Selection = Tailwind.WithOpacity(Tailwind.Blue500, 0.4f);
    public uint Caret = Tailwind.Slate100;
}
