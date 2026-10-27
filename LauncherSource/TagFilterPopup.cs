using System.Drawing.Drawing2D;

namespace RpgLauncher;

internal sealed record TagChipInfo(string Name, int Count, bool Favorite = false);

internal sealed record TagGroupInfo(string Title, IReadOnlyList<TagChipInfo> Tags);

/// <summary>Tag popup mode for picking one game's tags; <paramref name="Locked"/> (its DLsite tags) can't be removed.</summary>
internal sealed record TagPicking(string GameName, IReadOnlySet<string> Locked);

/// <summary>
/// Popup listing store tags by type with game counts; a click cycles show / hide / off.
/// With a <see cref="TagPicking"/> it toggles one game's tags instead, and new tags can be typed in.
/// </summary>
internal sealed class TagFilterPopup : Form
{
    private readonly TagList _list;
    private readonly Font _captionFont = Theme.Font(10f, FontStyle.Bold);
    private readonly Font _subtitleFont = Theme.Font(8.3f);
    private readonly float _scale;
    private readonly int _side;
    private readonly int _headerHeight;
    private readonly TagFilter _filter;
    private readonly TagPicking? _picking;
    private readonly TextBox? _newTag;
    private Rectangle _newTagField;
    private Rectangle _clearRect;
    private bool _clearHover;
    private bool _ready;

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
        float scale,
        TagPicking? picking = null)
    {
        _scale = scale;
        _picking = picking;
        _side = (int)Math.Round(20f * scale);
        _headerHeight = (int)Math.Round(70f * scale);

        int width = (int)Math.Round(700f * scale);
        int contentWidth = width - 2;
        _filter = filter;
        _list = new TagList(groups, filter, scale, picking)
        {
            Dock = DockStyle.Fill
        };

        int contentHeight = _list.Measure(contentWidth);
        int maxContent = (int)Math.Round(520f * scale);
        int visibleContent = Math.Max((int)Math.Round(150f * scale), Math.Min(contentHeight, maxContent));

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = picking is null ? "Filter by tag" : "Add tags";
        BackColor = Theme.MenuBg;
        ForeColor = Theme.Text;
        Font = Theme.Font(9f);
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        KeyPreview = true;
        Padding = new Padding(1, _headerHeight, 1, 1);
        ClientSize = new Size(width, _headerHeight + visibleContent + 1);
        Controls.Add(_list);

        if (picking is not null)
        {
            // For new tags that no game has yet.
            _newTag = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Field,
                ForeColor = Theme.Text,
                Font = Theme.Font(9f),
                PlaceholderText = "New tag\u2026"
            };
            _newTag.KeyDown += (_, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                string tag = _newTag.Text.Trim();
                if (tag.Length == 0) return;
                _filter.Include.Add(tag);
                _newTag.Clear();
                _list.Invalidate();
                Invalidate();
                FilterChanged?.Invoke();
            };
            _newTag.GotFocus += (_, _) => Invalidate(_newTagField);
            _newTag.LostFocus += (_, _) => Invalidate(_newTagField);
            Controls.Add(_newTag);
        }


        _list.FilterChanged += () =>
        {
            Invalidate();
            FilterChanged?.Invoke();
        };
        _list.FavoriteToggled += tag => FavoriteToggled?.Invoke(tag);

        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    /// <summary>Places the popup under its header button, clamped to the current screen.</summary>
    public void ShowBelow(Control anchor)
    {
        var owner = anchor.FindForm();
        if (owner is null) return;

        var area = Screen.FromControl(anchor).WorkingArea;
        int gap = (int)Math.Round(7f * _scale);

        int maxHeight = area.Height - (int)Math.Round(32f * _scale);
        if (Height > maxHeight) Height = Math.Max((int)Math.Round(220f * _scale), maxHeight);

        var buttonTopLeft = anchor.PointToScreen(Point.Empty);
        int x = buttonTopLeft.X;
        int y = buttonTopLeft.Y + anchor.Height + gap;

        x = Math.Clamp(x, area.Left + 8, Math.Max(area.Left + 8, area.Right - Width - 8));
        if (y + Height > area.Bottom - 8)
        {
            int above = buttonTopLeft.Y - gap - Height;
            y = above >= area.Top + 8
                ? above
                : Math.Max(area.Top + 8, area.Bottom - Height - 8);
        }

        Location = new Point(x, y);
        Show(owner);
        Activate();
    }

    /// <summary>Places the popup over the middle of <paramref name="owner"/>, clamped to its screen.</summary>
    public void ShowCentered(Form owner)
    {
        var area = Screen.FromControl(owner).WorkingArea;
        int maxHeight = area.Height - (int)Math.Round(32f * _scale);
        if (Height > maxHeight) Height = Math.Max((int)Math.Round(220f * _scale), maxHeight);

        var bounds = owner.Bounds;
        int x = Math.Clamp(bounds.Left + (bounds.Width - Width) / 2, area.Left + 8, Math.Max(area.Left + 8, area.Right - Width - 8));
        int y = Math.Clamp(bounds.Top + (bounds.Height - Height) / 2, area.Top + 8, Math.Max(area.Top + 8, area.Bottom - Height - 8));
        Location = new Point(x, y);
        Show(owner);
        Activate();
    }

    /// <summary>Right edge of the header's free space: left of the Blob style's mascot.</summary>
    private float HeaderRight()
    {
        float right = ClientSize.Width - _side;
        if (Mascots.For(MascotSpot.TagMenu) is { } blob)
            right -= Mascots.WidthAt(blob, 50f * _scale) + 10f * _scale;
        return right;
    }

    // Clear removes all tags when filtering, only non-DLsite ones when picking.
    private bool HasClearable =>
        _picking is null ? !_filter.IsEmpty : _filter.Include.Any(tag => !_picking.Locked.Contains(tag));

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_newTag is null) return;
        // Borderless text box inside a rounded field painted in OnPaint.
        int w = (int)Math.Round(170f * _scale), h = (int)Math.Round(26f * _scale), pad = (int)Math.Round(9f * _scale);
        _newTagField = new Rectangle((int)HeaderRight() - w, (int)Math.Round(14f * _scale), w, h);
        int textHeight = _newTag.PreferredHeight;
        _newTag.SetBounds(_newTagField.X + pad, _newTagField.Y + (h - textHeight) / 2 + 1, w - pad * 2, textHeight);
    }

    public void ScrollByWheel(int delta) => _list.ScrollByWheel(delta);

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _ready = true;
        _list.Focus();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (_ready && !IsDisposed) Close();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        ScrollByWheel(e.Delta);
        base.OnMouseWheel(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool hover = _clearRect.Contains(e.Location);
        if (hover != _clearHover)
        {
            _clearHover = hover;
            Invalidate(_clearRect);
        }
        Cursor = hover ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && _clearRect.Contains(e.Location) && HasClearable)
        {
            if (_picking is null) _filter.Clear();
            else _filter.Include.RemoveWhere(tag => !_picking.Locked.Contains(tag));
            _clearHover = false;
            _list.Invalidate();
            Invalidate();
            FilterChanged?.Invoke();
            return;
        }
        base.OnMouseClick(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Close();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var header = new RectangleF(_side, (int)Math.Round(14f * _scale),
            ClientSize.Width - _side * 2f, (int)Math.Round(22f * _scale));
        Theme.DrawText(g, _picking is null ? "TAGS" : "ADD TAGS", _captionFont, Theme.Text, header);

        // Blob style: a blob peeks over the header divider on the right.
        float headerRight = ClientSize.Width - _side;
        if (Mascots.For(MascotSpot.TagMenu) is { } blob)
        {
            float blobHeight = 50f * _scale;
            float blobX = headerRight - Mascots.WidthAt(blob, blobHeight);
            Mascots.Draw(g, blob, blobX, _headerHeight - 1, blobHeight);
            headerRight = blobX - 10f * _scale;
        }

        // Picking: the new-tag box sits at the right, the Clear button left of it.
        if (_newTag is not null)
        {
            using var fieldPath = Theme.Rounded(_newTagField, 6f * _scale);
            using var fieldFill = new SolidBrush(Theme.Field);
            using var fieldBorder = new Pen(_newTag.Focused ? Theme.CardBorderHover : Theme.FieldBorder, 1f);
            g.FillPath(fieldFill, fieldPath);
            g.DrawPath(fieldBorder, fieldPath);
            headerRight = _newTagField.Left - 10f * _scale;
        }

        _clearRect = Rectangle.Empty;
        if (HasClearable)
        {
            int w = (int)Math.Round(64f * _scale), h = (int)Math.Round(26f * _scale);
            _clearRect = new Rectangle((int)headerRight - w, (int)Math.Round(14f * _scale), w, h);
            using var path = Theme.Rounded(_clearRect, 6f * _scale);
            using var fill = new SolidBrush(_clearHover ? Theme.FieldHover : Theme.Field);
            using var clearBorder = new Pen(_clearHover ? Theme.CardBorderHover : Theme.FieldBorder, 1f);
            g.FillPath(fill, path);
            g.DrawPath(clearBorder, path);
            Theme.DrawText(g, "Clear", _subtitleFont, _clearHover ? Theme.Text : Theme.TextMuted, _clearRect,
                StringAlignment.Center);
        }

        string subtitleText = _picking is not null
            ? $"{_picking.GameName} \u00b7 click a tag to add or remove it \u00b7 its DLsite tags stay on"
            : _filter.IsEmpty
                ? "Click to show \u00b7 click again to hide \u00b7 right-click to pin"
                : $"{_filter.Include.Count} shown \u00b7 {_filter.Exclude.Count} hidden";
        float subtitleWidth = headerRight - _side - (_clearRect.IsEmpty ? 0 : _clearRect.Width + 10f * _scale);
        var subtitle = new RectangleF(_side, (int)Math.Round(36f * _scale), subtitleWidth, (int)Math.Round(18f * _scale));
        Theme.DrawText(g, subtitleText, _subtitleFont, Theme.TextDim, subtitle,
            StringAlignment.Near, StringAlignment.Near, StringTrimming.EllipsisCharacter);

        using var border = new Pen(Theme.FieldBorder, 1f);
        g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        using var divider = new Pen(Theme.Divider, 1f);
        g.DrawLine(divider, _side, _headerHeight - 1, ClientSize.Width - _side, _headerHeight - 1);

        base.OnPaint(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _captionFont.Dispose();
            _subtitleFont.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class TagList : Control
{
    private sealed record ChipLayout(TagChipInfo Info, RectangleF Bounds, float CountWidth);

    private IReadOnlyList<TagGroupInfo> _groups;
    private readonly TagFilter _filter;
    private readonly TagPicking? _picking;
    private readonly float _scale;
    private readonly Font _groupFont = Theme.Font(7.8f, FontStyle.Bold);
    private readonly Font _chipFont = Theme.Font(9.2f);
    private readonly Font _countFont = Theme.Font(8f, FontStyle.Bold);

    private readonly List<ChipLayout> _chips = new();
    private readonly List<(string Title, float Y, float TextWidth)> _groupHeaders = new();

    private int _contentHeight;
    private int _scroll;
    private int _maxScroll;
    private ChipLayout? _hoverChip;
    private bool _dragging;
    private bool _dragged;
    private bool _captured;
    private int _dragStartY;
    private int _dragStartScroll;

    public event Action? FilterChanged;
    public event Action<string>? FavoriteToggled;

    public void SetGroups(IReadOnlyList<TagGroupInfo> groups)
    {
        _groups = groups;
        Relayout(Width);
        Invalidate();
    }

    public TagList(IReadOnlyList<TagGroupInfo> groups, TagFilter filter, float scale, TagPicking? picking = null)
    {
        _groups = groups;
        _filter = filter;
        _picking = picking;
        _scale = scale;

        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.MenuBg;
        TabStop = false;
        Cursor = Cursors.Default;
        AccessibleName = "Store tags";

        Relayout(Width);
    }

    public int ContentHeight => _contentHeight;

    public int Measure(int width)
    {
        Relayout(width);
        return _contentHeight;
    }

    public void ScrollByWheel(int delta)
    {
        if (_maxScroll <= 0 || delta == 0) return;
        int step = (int)Math.Round(delta / 120f * 78f * _scale);
        if (step == 0) step = Math.Sign(delta) * 12;
        _scroll = Math.Clamp(_scroll - step, 0, _maxScroll);
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Relayout(Width);
        Invalidate();
    }

    private void Relayout(int width)
    {
        _chips.Clear();
        _groupHeaders.Clear();

        float side = 18f * _scale;
        float scrollReserve = 18f * _scale;
        float chipHeight = 31f * _scale;
        float chipGap = 8f * _scale;
        float rowGap = 8f * _scale;
        float headerHeight = 25f * _scale;
        float groupGap = 13f * _scale;
        float wrapWidth = Math.Max(80f, width - side * 2f - scrollReserve);

        float y = 5f * _scale;
        foreach (var group in _groups)
        {
            if (group.Tags.Count == 0) continue;
            if (_groupHeaders.Count > 0) y += groupGap;

            _groupHeaders.Add((group.Title, y, MeasureText(group.Title, _groupFont)));
            y += headerHeight;

            float x = side;
            bool rowHasChip = false;
            foreach (var chip in group.Tags)
            {
                float countWidth = MeasureText(chip.Count.ToString(), _countFont) + 2f * _scale;
                float chipWidth = MeasureChip(chip, countWidth);
                if (rowHasChip && x + chipWidth > side + wrapWidth)
                {
                    x = side;
                    y += chipHeight + rowGap;
                }

                _chips.Add(new ChipLayout(chip, new RectangleF(x, y, chipWidth, chipHeight), countWidth));
                x += chipWidth + chipGap;
                rowHasChip = true;
            }

            y += chipHeight;
        }

        _contentHeight = (int)Math.Ceiling(y + 10f * _scale);
        _maxScroll = Math.Max(0, _contentHeight - Height);
        _scroll = Math.Clamp(_scroll, 0, _maxScroll);
        _hoverChip = null;
    }

    private float MeasureChip(TagChipInfo chip, float countWidth)
    {
        float nameWidth = MeasureText(chip.Name, _chipFont) + (chip.Favorite ? StarSpace : 0f);
        float gap = 7f * _scale;
        // Slack for GDI+ DrawString's internal padding, so names don't ellipsize early.
        return (int)Math.Ceiling(12f * _scale * 2f + nameWidth + gap + countWidth + 10f * _scale);
    }

    private float StarSpace => 15f * _scale;

    private float MeasureText(string text, Font font)
    {
        return TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
    }

    private ChipLayout? ChipAt(Point point)
    {
        var contentPoint = new PointF(point.X, point.Y + _scroll);
        for (int i = _chips.Count - 1; i >= 0; i--)
        {
            if (_chips[i].Bounds.Contains(contentPoint)) return _chips[i];
        }
        return null;
    }

    private RectangleF ScrollThumb()
    {
        if (_maxScroll <= 0) return RectangleF.Empty;
        float top = 6f * _scale;
        float track = Math.Max(1f, Height - 12f * _scale);
        float thumbHeight = Math.Max(26f * _scale, track * Height / Math.Max(1f, _contentHeight));
        float progress = _maxScroll == 0 ? 0f : (float)_scroll / _maxScroll;
        float y = top + progress * Math.Max(0f, track - thumbHeight);
        return new RectangleF(Width - 10f * _scale, y, 4f * _scale, thumbHeight);
    }

    private bool InScrollbar(Point point)
    {
        if (_maxScroll <= 0) return false;
        var thumb = ScrollThumb();
        var hit = RectangleF.Inflate(thumb, 5f * _scale, 1f);
        return hit.Contains(point);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging)
        {
            int delta = e.Y - _dragStartY;
            if (Math.Abs(delta) > 2) _dragged = true;
            float track = Math.Max(1f, Height - 12f * _scale - ScrollThumb().Height);
            _scroll = Math.Clamp(
                (int)Math.Round(_dragStartScroll + delta * _maxScroll / track),
                0, _maxScroll);
            Invalidate();
            Cursor = Cursors.Default;
            base.OnMouseMove(e);
            return;
        }

        var chip = ChipAt(e.Location);
        if (!ReferenceEquals(chip, _hoverChip))
        {
            _hoverChip = chip;
            Invalidate();
        }

        bool clickable = chip is not null && _picking?.Locked.Contains(chip.Info.Name) != true;
        Cursor = clickable || InScrollbar(e.Location) ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (!_dragging)
        {
            _hoverChip = null;
            Cursor = Cursors.Default;
            Invalidate();
        }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (e.Button == MouseButtons.Left && InScrollbar(e.Location))
        {
            _dragging = true;
            _dragged = false;
            _captured = true;
            _dragStartY = e.Y;
            _dragStartScroll = _scroll;
            Capture = true;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_captured)
        {
            _captured = false;
            Capture = false;
            _dragging = false;
        }
        else if (e.Button == MouseButtons.Right && ChipAt(e.Location) is { } chip)
        {
            FavoriteToggled?.Invoke(chip.Info.Name);
        }
        base.OnMouseUp(e);
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        if (!Capture)
        {
            _captured = false;
            _dragging = false;
        }
        base.OnMouseCaptureChanged(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            if (_dragged)
            {
                _dragged = false;
                base.OnMouseClick(e);
                return;
            }

            var chip = ChipAt(e.Location);
            if (chip is not null && _picking is not null)
            {
                string name = chip.Info.Name;
                if (_picking.Locked.Contains(name)) return;
                if (!_filter.Include.Remove(name)) _filter.Include.Add(name);
                Invalidate();
                FilterChanged?.Invoke();
                return;
            }
            if (chip is not null)
            {
                _filter.Cycle(chip.Info.Name);
                Invalidate();
                FilterChanged?.Invoke();
                return;
            }
        }
        base.OnMouseClick(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        ScrollByWheel(e.Delta);
        base.OnMouseWheel(e);
    }

    protected override bool IsInputKey(Keys keyData)
    {
        return keyData is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End ||
            base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int page = Math.Max((int)Math.Round(220f * _scale), 1);
        switch (e.KeyCode)
        {
            case Keys.Up:
                _scroll = Math.Max(0, _scroll - (int)Math.Round(38f * _scale));
                break;
            case Keys.Down:
                _scroll = Math.Min(_maxScroll, _scroll + (int)Math.Round(38f * _scale));
                break;
            case Keys.PageUp:
                _scroll = Math.Max(0, _scroll - page);
                break;
            case Keys.PageDown:
                _scroll = Math.Min(_maxScroll, _scroll + page);
                break;
            case Keys.Home:
                _scroll = 0;
                break;
            case Keys.End:
                _scroll = _maxScroll;
                break;
            default:
                base.OnKeyDown(e);
                return;
        }

        e.Handled = true;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var state = g.Save();
        g.SetClip(ClientRectangle);
        g.TranslateTransform(0, -_scroll);

        float side = 18f * _scale;
        float scrollReserve = 18f * _scale;

        float viewTop = _scroll;
        float viewBottom = _scroll + Height;

        foreach (var (title, headerY, textWidth) in _groupHeaders)
        {
            if (headerY + 22f * _scale < viewTop || headerY > viewBottom) continue;
            var headerRect = new RectangleF(side, headerY, Width - side * 2f - scrollReserve,
                22f * _scale);
            Theme.DrawText(g, title, _groupFont, Theme.TextDim, headerRect,
                StringAlignment.Near, StringAlignment.Center, StringTrimming.EllipsisCharacter);

            float lineX = Math.Min(side + textWidth + 11f * _scale, Width - side - scrollReserve);
            float lineY = headerY + 11f * _scale;
            using var line = new Pen(Theme.Divider, 1f);
            g.DrawLine(line, lineX, lineY, Width - side - scrollReserve, lineY);
        }

        foreach (var chip in _chips)
        {
            if (chip.Bounds.Bottom < viewTop || chip.Bounds.Top > viewBottom) continue;
            DrawChip(g, chip);
        }

        g.Restore(state);

        if (_maxScroll > 0) DrawScrollbar(g);
    }

    private void DrawChip(Graphics g, ChipLayout chip)
    {
        bool locked = _picking?.Locked.Contains(chip.Info.Name) == true;
        bool hover = ReferenceEquals(chip, _hoverChip) && !locked;
        var state = _filter.StateOf(chip.Info.Name);
        Color fill = state switch
        {
            TagState.Include => Theme.AccentDim,
            TagState.Exclude => Theme.DangerDim,
            _ => hover ? Theme.FieldHover : Theme.Field
        };
        Color border = state switch
        {
            TagState.Include => Theme.AccentBorder,
            TagState.Exclude => Theme.DangerBorder,
            _ => hover ? Theme.CardBorderHover : Theme.FieldBorder
        };
        Color fg = state switch
        {
            TagState.Include when locked => Theme.Gold,
            TagState.Include => Theme.GoldBright,
            TagState.Exclude => Theme.Danger,
            _ => hover ? Theme.Text : Theme.TextMuted
        };
        Color countColor = state switch
        {
            TagState.Include => Theme.Gold,
            TagState.Exclude => Theme.Danger,
            _ => Theme.TextDim
        };

        using (var path = Theme.Rounded(chip.Bounds, Theme.ChipRadius * _scale))
        using (var brush = new SolidBrush(fill))
        using (var pen = new Pen(border, 1f))
        {
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }

        float pad = 12f * _scale;
        float countWidth = chip.CountWidth;
        float gap = 7f * _scale;
        float nameLeft = chip.Bounds.Left + pad;

        if (chip.Info.Favorite)
        {
            float size = 10f * _scale;
            var starRect = new RectangleF(nameLeft, chip.Bounds.Top + (chip.Bounds.Height - size) / 2f, size, size);
            using var star = Theme.Star(starRect);
            using var gold = new SolidBrush(Theme.Gold);
            g.FillPath(gold, star);
            nameLeft += StarSpace;
        }

        float nameWidth = Math.Max(1f, chip.Bounds.Right - pad - countWidth - gap - nameLeft);
        var nameRect = new RectangleF(nameLeft, chip.Bounds.Top, nameWidth, chip.Bounds.Height);
        Theme.DrawText(g, chip.Info.Name, _chipFont, fg, nameRect,
            StringAlignment.Near, StringAlignment.Center, StringTrimming.EllipsisCharacter);
        if (state == TagState.Exclude)
        {
            float textWidth = Math.Min(nameRect.Width, MeasureText(chip.Info.Name, _chipFont));
            float midY = nameRect.Top + nameRect.Height / 2f + 1f;
            using var strike = new Pen(Color.FromArgb(200, Theme.Danger), 1.2f);
            g.DrawLine(strike, nameRect.Left, midY, nameRect.Left + textWidth, midY);
        }

        // The count is measured with GDI (TextRenderer), so draw it with GDI too; GDI+ pads differently and clips digits.
        var countRect = Rectangle.Round(new RectangleF(chip.Bounds.Right - pad - countWidth, chip.Bounds.Top,
            countWidth, chip.Bounds.Height));
        TextRenderer.DrawText(g, chip.Info.Count.ToString(), _countFont, countRect,
            countColor,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsTranslateTransform |
            TextFormatFlags.PreserveGraphicsClipping);
    }

    private void DrawScrollbar(Graphics g)
    {
        var thumb = ScrollThumb();
        if (thumb.IsEmpty) return;
        using var brush = new SolidBrush(_dragging ? Theme.ScrollThumbActive : Theme.ScrollThumb);
        using var path = Theme.Rounded(thumb, thumb.Width / 2f);
        g.FillPath(brush, path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _groupFont.Dispose();
            _chipFont.Dispose();
            _countFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
