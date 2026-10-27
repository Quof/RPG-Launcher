using System.Drawing.Drawing2D;

namespace RpgLauncher;

internal sealed class PillButton : Control
{
    private bool _hover;
    private bool _pressed;
    private readonly Font _iconFont = new("Segoe MDL2 Assets", 11.5f, FontStyle.Regular, GraphicsUnit.Point);

    public string? IconGlyph { get; set; }
    public bool ShowHeart { get; set; }
    public bool DrawChevron { get; set; }

    private bool _checked;
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
        }
    }

    public PillButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        // Transparent so the corners outside the rounded shape show the parent, not a flat square.
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        TabStop = false;
        Cursor = Cursors.Hand;
        Font = Theme.Font(9.5f);
        Size = new Size(100, 38);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    public void PerformClick() => OnClick(EventArgs.Empty);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        float scale = Theme.Scale(this);
        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        float radius = Theme.ControlRadius * scale;

        Color fill = Checked ? Theme.AccentDim : Theme.Field;
        Color border = Checked ? Theme.AccentBorder : Theme.FieldBorder;
        Color fg = Checked ? Theme.Gold : Theme.TextMuted;

        if (_hover)
        {
            fill = Checked ? Theme.AccentHover : Theme.FieldHover;
            fg = Checked ? Theme.GoldBright : Theme.Text;
        }
        if (_pressed) fill = Color.FromArgb(Checked ? 70 : 45, fill.R, fill.G, fill.B);

        using var path = Theme.Rounded(rect, radius);
        using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
        using (var pen = new Pen(border, Theme.ControlBorderWidth)) g.DrawPath(pen, path);

        float iconSize = Math.Max(14f, 15f * scale);
        bool hasIcon = ShowHeart || !string.IsNullOrEmpty(IconGlyph);
        bool hasText = !string.IsNullOrEmpty(Text);
        float textW = hasText
            ? TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width
            : 0f;
        float gap = hasIcon && hasText ? 8f * scale : 0f;
        float chevronW = DrawChevron ? 15f * scale : 0f;
        float total = (hasIcon ? iconSize : 0f) + gap + textW + chevronW;
        float start = Math.Max(8f * scale, (Width - total) / 2f);

        if (ShowHeart)
        {
            var heartRect = new RectangleF(start, (Height - iconSize) / 2f - 1f, iconSize, iconSize);
            using var heart = Theme.Heart(heartRect);
            if (Checked)
            {
                using var brush = new SolidBrush(fg);
                g.FillPath(brush, heart);
            }
            else
            {
                using var pen = new Pen(fg, 1.8f) { LineJoin = LineJoin.Round };
                g.DrawPath(pen, heart);
            }
            start += iconSize + gap;
        }
        else if (!string.IsNullOrEmpty(IconGlyph))
        {
            using var iconFormat = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            using var brush = new SolidBrush(fg);
            var iconRect = new RectangleF(start, 0, iconSize, Height);
            g.DrawString(IconGlyph, _iconFont, brush, iconRect, iconFormat);
            start += iconSize + gap;
        }

        if (hasText)
        {
            TextRenderer.DrawText(g, Text, Font,
                new Rectangle((int)start, 0, (int)Math.Ceiling(textW) + 2, Height),
                fg,
                TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.Left);
            start += textW;
        }

        if (DrawChevron)
        {
            float cx = start + chevronW / 2f;
            float cy = Height / 2f + 1f;
            using var pen = new Pen(fg, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(pen, new[]
            {
                new PointF(cx - 4f * scale, cy - 2f * scale),
                new PointF(cx, cy + 2.5f * scale),
                new PointF(cx + 4f * scale, cy - 2f * scale)
            });
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _iconFont.Dispose();
        base.Dispose(disposing);
    }
}
