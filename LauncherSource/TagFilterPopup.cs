using Avalonia.Automation;
using Avalonia.Rendering;
using Avalonia.Styling;

namespace RpgLauncher;

internal sealed record TagChipInfo(string Name, int Count, bool Favorite = false);

internal sealed record TagGroupInfo(string Title, IReadOnlyList<TagChipInfo> Tags);

/// <summary>Tag popup mode for picking one game's tags; <paramref name="Locked"/> (its DLsite tags) can't be removed.</summary>
internal sealed record TagPicking(string GameName, IReadOnlySet<string> Locked);

/// <summary>
/// Popup listing store tags by type with game counts; a click cycles show / hide / off.
/// With a <see cref="TagPicking"/> it toggles one game's tags instead, and new tags can be typed in.
/// </summary>
/// <remarks>
/// A borderless window owned by the window it opens over, as the WinForms form was: it has the keyboard while
/// open (scrolling keys, Escape, typing a new tag through an input method) and closes when it loses activation,
/// which a click anywhere else does; that click still reaches whatever was clicked. <see cref="Window.Closed"/>
/// replaces WinForms' FormClosed.
/// </remarks>
internal sealed class TagFilterPopup : Window
{
    private const double Side = 20;
    private const double HeaderHeight = 70;
    private const double PopupWidth = 700;

    private static readonly UiFont CaptionFont = Look.Font(10, bold: true);
    private static readonly UiFont SubtitleFont = Look.Font(8.3);

    private readonly Surface _surface;
    private readonly TagList _list;
    private readonly TagFilter _filter;
    private readonly TagPicking? _picking;
    private readonly TextBox? _newTag;
    private Window? _owner;
    private bool _clearHover;
    private bool _ready;
    private bool _closing;

    /// <summary>Raised after each change; the shared filter already holds the new state.</summary>
    public event Action? FilterChanged;

    /// <summary>Raised when a tag is right-clicked to pin or unpin it as a favorite.</summary>
    public event Action<string>? FavoriteToggled;

    /// <summary>Replaces the tag list in place, e.g. after a favorite was pinned.</summary>
    public void UpdateGroups(IReadOnlyList<TagGroupInfo> groups) => _list.SetGroups(groups);

    public TagFilterPopup(
        IReadOnlyList<TagGroupInfo> groups,
        TagFilter filter,
        int gameCount,
        MetadataLanguage language,
        TagPicking? picking = null)
    {
        _picking = picking;
        _filter = filter;
        _list = new TagList(groups, filter, picking);

        double contentHeight = _list.MeasureContent(PopupWidth - 2);
        double visibleContent = Math.Max(150, Math.Min(contentHeight, 520));

        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        CanResize = false;
        SizeToContent = SizeToContent.Manual;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = picking is null ? "Filter by tag" : "Add tags";
        Icon = App.WindowIcon;
        Background = Look.Brush(Look.MenuBg);
        Foreground = Look.Brush(Look.Text);
        var font = Look.Font(9);
        FontFamily = font.Family;
        FontSize = font.Size;
        RequestedThemeVariant = Look.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
        Width = PopupWidth;
        Height = HeaderHeight + visibleContent + 1;

        _surface = new Surface(this);
        _surface.Add(_list);

        if (picking is not null)
        {
            // For new tags that no game has yet.
            _newTag = SearchBox.PlainTextBox(Look.Font(9), "New tag…");
            _newTag.AddHandler(KeyDownEvent, (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                string tag = (_newTag.Text ?? "").Trim();
                if (tag.Length == 0) return;
                _filter.Include.Add(tag);
                _newTag.Clear();
                _list.InvalidateVisual();
                _surface.InvalidateVisual();
                FilterChanged?.Invoke();
            }, RoutingStrategies.Tunnel);
            _newTag.GotFocus += (_, _) => _surface.InvalidateVisual();
            _newTag.LostFocus += (_, _) => _surface.InvalidateVisual();
            _surface.Add(_newTag);
        }

        _list.FilterChanged += () =>
        {
            _surface.InvalidateVisual();
            FilterChanged?.Invoke();
        };
        _list.FavoriteToggled += tag => FavoriteToggled?.Invoke(tag);

        AutomationProperties.SetName(_list, "Store tags");
        Content = _surface;

        // Escape closes from anywhere in the popup, the new-tag box included.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        }, RoutingStrategies.Tunnel);
        Deactivated += (_, _) =>
        {
            // Closing inside the platform's deactivation message is risky; do it right after.
            if (_ready) Dispatcher.UIThread.Post(Dismiss);
        };
    }

    /// <summary>Places the popup under its header button, clamped to the current screen.</summary>
    public void ShowBelow(Control anchor)
    {
        if (TopLevel.GetTopLevel(anchor) is not Window owner) return;

        var (area, scaling) = ScreenArea(owner, anchor);
        const double gap = 7;
        FitHeight(area);

        var buttonTopLeft = anchor.PointToScreen(new Point(0, 0));
        double buttonX = buttonTopLeft.X / scaling, buttonY = buttonTopLeft.Y / scaling;
        double x = buttonX;
        double y = buttonY + anchor.Bounds.Height + gap;

        x = Math.Clamp(x, area.Left + 8, Math.Max(area.Left + 8, area.Right - Width - 8));
        if (y + Height > area.Bottom - 8)
        {
            double above = buttonY - gap - Height;
            y = above >= area.Top + 8
                ? above
                : Math.Max(area.Top + 8, area.Bottom - Height - 8);
        }

        ShowAt(owner, x, y, scaling);
    }

    /// <summary>Places the popup over the middle of <paramref name="owner"/>, clamped to its screen.</summary>
    public void ShowCentered(Window owner)
    {
        var (area, scaling) = ScreenArea(owner, owner);
        FitHeight(area);

        // The owner's outer bounds, title bar included, as WinForms' Form.Bounds.
        var frame = owner.FrameSize ?? owner.ClientSize;
        var bounds = new Rect(owner.Position.X / scaling, owner.Position.Y / scaling, frame.Width, frame.Height);
        double x = Math.Clamp(bounds.Left + (bounds.Width - Width) / 2, area.Left + 8, Math.Max(area.Left + 8, area.Right - Width - 8));
        double y = Math.Clamp(bounds.Top + (bounds.Height - Height) / 2, area.Top + 8, Math.Max(area.Top + 8, area.Bottom - Height - 8));
        ShowAt(owner, x, y, scaling);
    }

    /// <summary>The working area of the screen showing <paramref name="visual"/>, in device-independent units, and its scaling.</summary>
    private static (Rect Area, double Scaling) ScreenArea(Window owner, Visual visual)
    {
        var screen = owner.Screens?.ScreenFromVisual(visual) ?? owner.Screens?.Primary;
        if (screen is null) return (new Rect(-100000, -100000, 200000, 200000), owner.RenderScaling);
        double scaling = screen.Scaling;
        var area = screen.WorkingArea;
        return (new Rect(area.X / scaling, area.Y / scaling, area.Width / scaling, area.Height / scaling), scaling);
    }

    private void FitHeight(Rect area)
    {
        double maxHeight = area.Height - 32;
        if (Height > maxHeight) Height = Math.Max(220, maxHeight);
    }

    private void ShowAt(Window owner, double x, double y, double scaling)
    {
        _owner = owner;
        Position = new PixelPoint((int)Math.Round(x * scaling), (int)Math.Round(y * scaling));
        // A click elsewhere in the owner closes the popup even where the window manager doesn't move activation.
        owner.AddHandler(PointerPressedEvent, OnOwnerPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        Show(owner);
        Activate();
    }

    private void OnOwnerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_ready) Dismiss();
    }

    private void Dismiss()
    {
        if (!_closing) Close();
    }

    /// <summary>Scrolls the tag list by wheel notches.</summary>
    public void ScrollByWheel(double notches) => _list.ScrollByWheel(notches);

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _ready = true;
        _list.Focus();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closing = true;
        _owner?.RemoveHandler(PointerPressedEvent, OnOwnerPointerPressed);
        _owner = null;
        base.OnClosed(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        // The list scrolls too and leaves the event unhandled, so a notch over it scrolls twice, as in WinForms.
        ScrollByWheel(e.Delta.Y);
        e.Handled = true;
    }

    /// <summary>Right edge of the header's free space: left of the Blob style's mascot.</summary>
    private double HeaderRight(double width)
    {
        double right = width - Side;
        if (Mascots.For(MascotSpot.TagMenu) is { } blob)
            right -= Mascots.WidthAt(blob, 50) + 10;
        return right;
    }

    // Clear removes all tags when filtering, only non-DLsite ones when picking.
    private bool HasClearable =>
        _picking is null ? !_filter.IsEmpty : _filter.Include.Any(tag => !_picking.Locked.Contains(tag));

    /// <summary>The rounded field the new-tag box sits in (picking only).</summary>
    private Rect NewTagField(double width) => new(Math.Floor(HeaderRight(width)) - 170, 14, 170, 26);

    /// <summary>The Clear button, left of the mascot and the new-tag field; empty when there is nothing to clear.</summary>
    private Rect ClearRect(double width)
    {
        if (!HasClearable) return default;
        double right = _newTag is null ? HeaderRight(width) : NewTagField(width).Left - 10;
        return new Rect(Math.Floor(right) - 64, 14, 64, 26);
    }

    private void ClearTags()
    {
        if (_picking is null) _filter.Clear();
        else _filter.Include.RemoveWhere(tag => !_picking.Locked.Contains(tag));
        _clearHover = false;
        _list.InvalidateVisual();
        _surface.InvalidateVisual();
        FilterChanged?.Invoke();
    }

    /// <summary>Where the list goes (the WinForms padding: the header above, a one-pixel border around) and the new-tag box.</summary>
    private (Rect List, Rect NewTag) ChildRects(Size size)
    {
        var list = new Rect(1, HeaderHeight, Math.Max(0, size.Width - 2), Math.Max(0, size.Height - HeaderHeight - 1));
        // A borderless text box inside the rounded field painted below.
        var field = NewTagField(size.Width);
        const double pad = 9;
        return (list, new Rect(field.X + pad, field.Y + 2, field.Width - pad * 2, field.Height - 2));
    }

    private void Paint(DrawingContext g, Size size)
    {
        double width = size.Width, height = size.Height;
        g.FillRectangle(Look.Brush(Look.MenuBg), new Rect(size));

        var header = new Rect(Side, 14, Math.Max(1, width - Side * 2), 22);
        Look.DrawText(g, _picking is null ? "TAGS" : "ADD TAGS", CaptionFont, Look.Text, header);

        // Blob style: a blob peeks over the header divider on the right.
        double headerRight = width - Side;
        if (Mascots.For(MascotSpot.TagMenu) is { } blob)
        {
            const double blobHeight = 50;
            double blobX = headerRight - Mascots.WidthAt(blob, blobHeight);
            Mascots.Draw(g, blob, blobX, HeaderHeight - 1, blobHeight);
            headerRight = blobX - 10;
        }

        // WinForms drew the whole-pixel shapes below with crisp outlines; half a pixel in keeps them crisp here.
        var half = new Vector(0.5, 0.5);

        // Picking: the new-tag box sits at the right, the Clear button left of it.
        if (_newTag is not null)
        {
            var field = NewTagField(width);
            g.DrawRectangle(Look.Brush(Look.Field), Look.Pen(_newTag.IsFocused ? Look.CardBorderHover : Look.FieldBorder, 1),
                Look.Rounded(field.Translate(half), 6));
            headerRight = field.Left - 10;
        }

        var clear = ClearRect(width);
        if (clear.Width > 0)
        {
            g.DrawRectangle(Look.Brush(_clearHover ? Look.FieldHover : Look.Field),
                Look.Pen(_clearHover ? Look.CardBorderHover : Look.FieldBorder, 1), Look.Rounded(clear.Translate(half), 6));
            Look.DrawText(g, "Clear", SubtitleFont, _clearHover ? Look.Text : Look.TextMuted, clear, TextAlignment.Center);
        }

        string subtitleText = _picking is not null
            ? $"{_picking.GameName} · click a tag to add or remove it · its DLsite tags stay on"
            : _filter.IsEmpty
                ? "Click to show · click again to hide · right-click to pin"
                : $"{_filter.Include.Count} shown · {_filter.Exclude.Count} hidden";
        double subtitleWidth = headerRight - Side - (clear.Width > 0 ? clear.Width + 10 : 0);
        Look.DrawText(g, subtitleText, SubtitleFont, Look.TextDim, new Rect(Side, 36, Math.Max(1, subtitleWidth), 18),
            TextAlignment.Left, TextLine.Top);

        g.DrawRectangle(Look.Pen(Look.FieldBorder, 1), new Rect(0.5, 0.5, width - 1, height - 1));
        g.DrawLine(Look.Pen(Look.Divider, 1), new Point(Side, HeaderHeight - 0.5), new Point(width - Side, HeaderHeight - 0.5));
    }

    /// <summary>The popup's content: paints the header and border under the tag list and the new-tag box.</summary>
    private sealed class Surface : Control, ICustomHitTest
    {
        private readonly TagFilterPopup _popup;
        private readonly Cursor _hand = new(StandardCursorType.Hand);
        private bool _pressed;

        public Surface(TagFilterPopup popup)
        {
            _popup = popup;
        }

        public void Add(Control child)
        {
            VisualChildren.Add(child);
            LogicalChildren.Add(child);
        }

        public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

        protected override Size MeasureOverride(Size availableSize)
        {
            var size = new Size(double.IsInfinity(availableSize.Width) ? PopupWidth : availableSize.Width,
                double.IsInfinity(availableSize.Height) ? HeaderHeight : availableSize.Height);
            var (list, newTag) = _popup.ChildRects(size);
            _popup._list.Measure(list.Size);
            _popup._newTag?.Measure(newTag.Size);
            return size;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var (list, newTag) = _popup.ChildRects(finalSize);
            _popup._list.Arrange(list);
            _popup._newTag?.Arrange(newTag);
            return finalSize;
        }

        public override void Render(DrawingContext g) => _popup.Paint(g, Bounds.Size);

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!ReferenceEquals(e.Source, this)) return;
            var clear = _popup.ClearRect(Bounds.Width);
            bool hover = clear.Width > 0 && clear.Contains(e.GetPosition(this));
            if (hover != _popup._clearHover)
            {
                _popup._clearHover = hover;
                InvalidateVisual();
            }
            Cursor = hover ? _hand : Cursor.Default;
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            if (!_popup._clearHover) return;
            _popup._clearHover = false;
            InvalidateVisual();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!ReferenceEquals(e.Source, this)) return;
            e.Handled = true;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            _pressed = true;
            e.Pointer.Capture(this);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (e.InitialPressMouseButton != MouseButton.Left || !_pressed) return;
            _pressed = false;
            e.Pointer.Capture(null);
            e.Handled = true;
            var point = e.GetPosition(this);
            if (_popup.ClearRect(Bounds.Width).Contains(point) && _popup.HasClearable) _popup.ClearTags();
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            _pressed = false;
        }
    }
}

/// <summary>The scrolling chips of the tag popup, grouped under headers.</summary>
internal sealed class TagList : Control, ICustomHitTest
{
    private sealed record ChipLayout(TagChipInfo Info, Rect Bounds, double CountWidth);

    private const double SideMargin = 18;
    private const double ScrollReserve = 18;
    private const double ChipHeight = 31;
    private const double ChipGap = 8;
    private const double RowGap = 8;
    private const double GroupHeaderHeight = 25;
    private const double GroupGap = 13;
    private const double ChipPad = 12;
    private const double NameGap = 7;
    private const double StarSpace = 15;

    private static readonly UiFont GroupFont = Look.Font(7.8, bold: true);
    private static readonly UiFont ChipFont = Look.Font(9.2);
    private static readonly UiFont CountFont = Look.Font(8, bold: true);

    private IReadOnlyList<TagGroupInfo> _groups;
    private readonly TagFilter _filter;
    private readonly TagPicking? _picking;
    private readonly Cursor _hand = new(StandardCursorType.Hand);

    private readonly List<ChipLayout> _chips = new();
    private readonly List<(string Title, double Y, double TextWidth)> _groupHeaders = new();

    private double _contentHeight;
    private double _scroll;
    private double _maxScroll;
    private ChipLayout? _hoverChip;
    private bool _pressed;
    private bool _dragging;
    private bool _dragged;
    private double _dragStartY;
    private double _dragStartScroll;

    public event Action? FilterChanged;
    public event Action<string>? FavoriteToggled;

    public void SetGroups(IReadOnlyList<TagGroupInfo> groups)
    {
        _groups = groups;
        Relayout(Bounds.Width);
        InvalidateVisual();
    }

    public TagList(IReadOnlyList<TagGroupInfo> groups, TagFilter filter, TagPicking? picking = null)
    {
        _groups = groups;
        _filter = filter;
        _picking = picking;
        Focusable = true;
        IsTabStop = false;
        FocusAdorner = null;
        ClipToBounds = true;
        Cursor = Cursor.Default;
        Relayout(Bounds.Width);
    }

    public double ContentHeight => _contentHeight;

    /// <summary>Lays the chips out for <paramref name="width"/> and returns the height they need.</summary>
    public double MeasureContent(double width)
    {
        Relayout(width);
        return _contentHeight;
    }

    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    public void ScrollByWheel(double notches)
    {
        if (_maxScroll <= 0 || notches == 0) return;
        double step = Math.Round(notches * 78);
        if (step == 0) step = Math.Sign(notches) * 12;
        _scroll = Math.Clamp(_scroll - step, 0, _maxScroll);
        InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Relayout(e.NewSize.Width);
        InvalidateVisual();
    }

    private void Relayout(double width)
    {
        _chips.Clear();
        _groupHeaders.Clear();

        double wrapWidth = Math.Max(80, width - SideMargin * 2 - ScrollReserve);

        double y = 5;
        foreach (var group in _groups)
        {
            if (group.Tags.Count == 0) continue;
            if (_groupHeaders.Count > 0) y += GroupGap;

            _groupHeaders.Add((group.Title, y, MeasureText(group.Title, GroupFont)));
            y += GroupHeaderHeight;

            double x = SideMargin;
            bool rowHasChip = false;
            foreach (var chip in group.Tags)
            {
                double countWidth = MeasureText(chip.Count.ToString(), CountFont) + 2;
                double chipWidth = MeasureChip(chip, countWidth);
                if (rowHasChip && x + chipWidth > SideMargin + wrapWidth)
                {
                    x = SideMargin;
                    y += ChipHeight + RowGap;
                }

                _chips.Add(new ChipLayout(chip, new Rect(x, y, chipWidth, ChipHeight), countWidth));
                x += chipWidth + ChipGap;
                rowHasChip = true;
            }

            y += ChipHeight;
        }

        _contentHeight = Math.Ceiling(y + 10);
        _maxScroll = Math.Max(0, _contentHeight - Bounds.Height);
        _scroll = Math.Clamp(_scroll, 0, _maxScroll);
        _hoverChip = null;
    }

    private static double MeasureChip(TagChipInfo chip, double countWidth)
    {
        double nameWidth = MeasureText(chip.Name, ChipFont) + (chip.Favorite ? StarSpace : 0);
        // The WinForms slack for GDI+ text padding, kept so chips have the same sizes.
        return Math.Ceiling(ChipPad * 2 + nameWidth + NameGap + countWidth + 10);
    }

    private static double MeasureText(string text, UiFont font) => Math.Ceiling(Look.Measure(text, font).Width);

    private ChipLayout? ChipAt(Point point)
    {
        var contentPoint = new Point(point.X, point.Y + _scroll);
        for (int i = _chips.Count - 1; i >= 0; i--)
        {
            if (_chips[i].Bounds.Contains(contentPoint)) return _chips[i];
        }
        return null;
    }

    private Rect ScrollThumb()
    {
        if (_maxScroll <= 0) return default;
        double height = Bounds.Height;
        const double top = 6;
        double track = Math.Max(1, height - 12);
        double thumbHeight = Math.Max(26, track * height / Math.Max(1, _contentHeight));
        double progress = _scroll / _maxScroll;
        double y = top + progress * Math.Max(0, track - thumbHeight);
        return new Rect(Bounds.Width - 10, y, 4, thumbHeight);
    }

    private bool InScrollbar(Point point) =>
        _maxScroll > 0 && ScrollThumb().Inflate(new Thickness(5, 1)).Contains(point);

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        if (_dragging)
        {
            double delta = point.Y - _dragStartY;
            if (Math.Abs(delta) > 2) _dragged = true;
            double track = Math.Max(1, Bounds.Height - 12 - ScrollThumb().Height);
            _scroll = Math.Clamp(Math.Round(_dragStartScroll + delta * _maxScroll / track), 0, _maxScroll);
            InvalidateVisual();
            Cursor = Cursor.Default;
            return;
        }

        var chip = ChipAt(point);
        if (!ReferenceEquals(chip, _hoverChip))
        {
            _hoverChip = chip;
            InvalidateVisual();
        }

        bool clickable = chip is not null && _picking?.Locked.Contains(chip.Info.Name) != true;
        Cursor = clickable || InScrollbar(point) ? _hand : Cursor.Default;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_dragging) return;
        _hoverChip = null;
        Cursor = Cursor.Default;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        e.Handled = true;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        // WinForms captured the mouse on every press; a click is a press and a release over the list.
        _pressed = true;
        e.Pointer.Capture(this);
        var point = e.GetPosition(this);
        if (InScrollbar(point))
        {
            _dragging = true;
            _dragged = false;
            _dragStartY = point.Y;
            _dragStartScroll = _scroll;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Handled = true;
        var point = e.GetPosition(this);
        if (e.InitialPressMouseButton == MouseButton.Right)
        {
            if (!_dragging && ChipAt(point) is { } pinned) FavoriteToggled?.Invoke(pinned.Info.Name);
            return;
        }
        if (e.InitialPressMouseButton != MouseButton.Left || !_pressed) return;
        _pressed = false;
        bool dragging = _dragging;
        _dragging = false;
        e.Pointer.Capture(null);
        if (dragging) InvalidateVisual();
        if (new Rect(Bounds.Size).Contains(point)) Click(point);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _pressed = false;
        if (!_dragging) return;
        _dragging = false;
        InvalidateVisual();
    }

    private void Click(Point point)
    {
        if (_dragged)
        {
            _dragged = false;
            return;
        }

        var chip = ChipAt(point);
        if (chip is null) return;
        string name = chip.Info.Name;
        if (_picking is not null)
        {
            if (_picking.Locked.Contains(name)) return;
            if (!_filter.Include.Remove(name)) _filter.Include.Add(name);
        }
        else
        {
            _filter.Cycle(name);
        }
        InvalidateVisual();
        FilterChanged?.Invoke();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        // Left unhandled: the popup scrolls again, as the WinForms form did when the list passed the wheel on.
        ScrollByWheel(e.Delta.Y);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        const double page = 220;
        switch (e.Key)
        {
            case Key.Up:
                _scroll = Math.Max(0, _scroll - 38);
                break;
            case Key.Down:
                _scroll = Math.Min(_maxScroll, _scroll + 38);
                break;
            case Key.PageUp:
                _scroll = Math.Max(0, _scroll - page);
                break;
            case Key.PageDown:
                _scroll = Math.Min(_maxScroll, _scroll + page);
                break;
            case Key.Home:
                _scroll = 0;
                break;
            case Key.End:
                _scroll = _maxScroll;
                break;
            default:
                base.OnKeyDown(e);
                return;
        }

        e.Handled = true;
        InvalidateVisual();
    }

    public override void Render(DrawingContext g)
    {
        double width = Bounds.Width, height = Bounds.Height;
        g.FillRectangle(Look.Brush(Look.MenuBg), new Rect(Bounds.Size));

        using (g.PushClip(new Rect(Bounds.Size)))
        using (g.PushTransform(Matrix.CreateTranslation(0, -_scroll)))
        {
            double viewTop = _scroll;
            double viewBottom = _scroll + height;
            double lineRight = width - SideMargin - ScrollReserve;

            foreach (var (title, headerY, textWidth) in _groupHeaders)
            {
                if (headerY + 22 < viewTop || headerY > viewBottom) continue;
                var headerRect = new Rect(SideMargin, headerY, Math.Max(1, width - SideMargin * 2 - ScrollReserve), 22);
                Look.DrawText(g, title, GroupFont, Look.TextDim, headerRect);

                double lineX = Math.Min(SideMargin + textWidth + 11, lineRight);
                double lineY = headerY + 11 + 0.5;
                g.DrawLine(Look.Pen(Look.Divider, 1), new Point(lineX, lineY), new Point(lineRight, lineY));
            }

            foreach (var chip in _chips)
            {
                if (chip.Bounds.Bottom < viewTop || chip.Bounds.Top > viewBottom) continue;
                DrawChip(g, chip);
            }
        }

        if (_maxScroll > 0) DrawScrollbar(g);
    }

    private void DrawChip(DrawingContext g, ChipLayout chip)
    {
        bool locked = _picking?.Locked.Contains(chip.Info.Name) == true;
        bool hover = ReferenceEquals(chip, _hoverChip) && !locked;
        var state = _filter.StateOf(chip.Info.Name);
        var fill = state switch
        {
            TagState.Include => Look.AccentDim,
            TagState.Exclude => Look.DangerDim,
            _ => hover ? Look.FieldHover : Look.Field
        };
        var border = state switch
        {
            TagState.Include => Look.AccentBorder,
            TagState.Exclude => Look.DangerBorder,
            _ => hover ? Look.CardBorderHover : Look.FieldBorder
        };
        var fg = state switch
        {
            TagState.Include when locked => Look.Gold,
            TagState.Include => Look.GoldBright,
            TagState.Exclude => Look.Danger,
            _ => hover ? Look.Text : Look.TextMuted
        };
        var countColor = state switch
        {
            TagState.Include => Look.Gold,
            TagState.Exclude => Look.Danger,
            _ => Look.TextDim
        };

        // WinForms drew the chips on whole pixels with crisp outlines; half a pixel in keeps them crisp here.
        var half = new Vector(0.5, 0.5);
        var bounds = chip.Bounds;
        g.DrawRectangle(Look.Brush(fill), Look.Pen(border, 1), Look.Rounded(bounds.Translate(half), Look.ChipRadius));

        double nameLeft = bounds.Left + ChipPad;
        if (chip.Info.Favorite)
        {
            const double size = 10;
            var starRect = new Rect(nameLeft, bounds.Top + (bounds.Height - size) / 2, size, size);
            g.DrawGeometry(Look.Brush(Look.Gold), null, Look.Star(starRect.Translate(half)));
            nameLeft += StarSpace;
        }

        double nameWidth = Math.Max(1, bounds.Right - ChipPad - chip.CountWidth - NameGap - nameLeft);
        var nameRect = new Rect(nameLeft, bounds.Top, nameWidth, bounds.Height);
        Look.DrawText(g, chip.Info.Name, ChipFont, fg, nameRect);
        if (state == TagState.Exclude)
        {
            double textWidth = Math.Min(nameRect.Width, MeasureText(chip.Info.Name, ChipFont));
            double midY = nameRect.Top + nameRect.Height / 2 + 1 + 0.5;
            g.DrawLine(Look.Pen(Look.Alpha(200, Look.Danger), 1.2), new Point(nameRect.Left + 0.5, midY),
                new Point(nameRect.Left + 0.5 + textWidth, midY));
        }

        var countRect = new Rect(bounds.Right - ChipPad - chip.CountWidth, bounds.Top, chip.CountWidth, bounds.Height);
        Look.DrawText(g, chip.Info.Count.ToString(), CountFont, countColor, countRect, TextAlignment.Right);
    }

    private void DrawScrollbar(DrawingContext g)
    {
        var thumb = ScrollThumb();
        if (thumb.Width <= 0) return;
        g.DrawRectangle(Look.Brush(_dragging ? Look.ScrollThumbActive : Look.ScrollThumb), null,
            Look.Rounded(thumb.Translate(new Vector(0.5, 0.5)), thumb.Width / 2));
    }
}
