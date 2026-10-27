using System.Drawing.Drawing2D;

namespace RpgLauncher;

/// <summary>Segmented switch in the header, between the library and new releases.</summary>
internal sealed class ViewToggle : Control
{
    private static readonly Font SegmentFont = Theme.Font(9f, FontStyle.Bold);
    private readonly string[] _labels;
    private int _selected;
    private int _hover = -1;

    public event Action<int>? SelectedChanged;

    public ViewToggle(params string[] labels)
    {
        _labels = labels;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
    }

    public int Selected
    {
        get => _selected;
        set
        {
            value = Math.Clamp(value, 0, _labels.Length - 1);
            if (value == _selected) return;
            _selected = value;
            Invalidate();
            SelectedChanged?.Invoke(value);
        }
    }

    /// <summary>Width that fits every label.</summary>
    public int PreferredWidth(float scale)
    {
        int width = 0;
        foreach (var label in _labels)
        {
            width += TextRenderer.MeasureText(label, SegmentFont, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + (int)Math.Round(26f * scale);
        }
        return width + (int)Math.Round(6f * scale);
    }

    private Rectangle SegmentRect(int index)
    {
        float scale = Theme.Scale(this);
        int inset = (int)Math.Round(3f * scale);
        int x = inset;
        for (int i = 0; i < _labels.Length; i++)
        {
            int w = TextRenderer.MeasureText(_labels[i], SegmentFont, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + (int)Math.Round(26f * scale);
            if (i == _labels.Length - 1) w = Width - inset - x;
            if (i == index) return new Rectangle(x, inset, w, Height - inset * 2);
            x += w;
        }
        return Rectangle.Empty;
    }

    private int HitTest(Point point)
    {
        for (int i = 0; i < _labels.Length; i++)
            if (SegmentRect(i).Contains(point)) return i;
        return -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = Theme.Scale(this);

        var outer = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using (var path = Theme.Rounded(outer, Radius(outer.Height)))
        using (var fill = new SolidBrush(Theme.Field))
        using (var border = new Pen(Theme.FieldBorder, 1f))
        {
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }

        for (int i = 0; i < _labels.Length; i++)
        {
            var rect = SegmentRect(i);
            bool selected = i == _selected;
            if (selected || i == _hover)
            {
                using var path = Theme.Rounded(rect, Radius(rect.Height));
                using var fill = new SolidBrush(selected ? Theme.AccentDim : Theme.FieldHover);
                g.FillPath(fill, path);
                if (selected)
                {
                    using var border = new Pen(Theme.AccentBorder, 1f);
                    g.DrawPath(border, path);
                }
            }

            Color fore = selected ? Theme.Gold : i == _hover ? Theme.Text : Theme.TextMuted;
            TextRenderer.DrawText(g, _labels[i], SegmentFont, rect, fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
    }

    // A pill in most styles; square tabs in Angelic.
    private float Radius(float height) => Theme.Style switch
    {
        UiStyle.Angelic => 0f,
        UiStyle.Dark => 6f * Theme.Scale(this),
        _ => height / 2f
    };

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int hit = HitTest(e.Location);
        if (hit != _hover)
        {
            _hover = hit;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = -1;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && HitTest(e.Location) is var hit and >= 0) Selected = hit;
        base.OnMouseClick(e);
    }
}
