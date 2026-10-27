using System.Drawing.Drawing2D;

namespace RpgLauncher;

internal sealed class SearchBox : Control
{
    private readonly CueTextBox _text;
    private bool _focused;
    private bool _hoverClear;
    private RectangleF _clearRect;

    public event EventHandler? TextValueChanged;

    public string TextValue
    {
        get => _text.Text;
        set => _text.Text = value ?? string.Empty;
    }

    public SearchBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        // Transparent so the corners outside the rounded field show the header gradient, not a flat square.
        SetStyle(ControlStyles.SupportsTransparentBackColor, true);
        TabStop = false;
        BackColor = Color.Transparent;

        _text = new CueTextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Field,
            ForeColor = Theme.Text,
            Font = Theme.Font(9.5f),
            CueText = "Search games\u2026",
            TabStop = false
        };
        _text.TextChanged += (_, _) =>
        {
            PositionTextBox();
            Invalidate();
            TextValueChanged?.Invoke(this, EventArgs.Empty);
        };
        _text.GotFocus += (_, _) => { _focused = true; _text.Invalidate(); Invalidate(); };
        _text.LostFocus += (_, _) => { _focused = false; _text.Invalidate(); Invalidate(); };
        Controls.Add(_text);
        Size = new Size(270, 38);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        PositionTextBox();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        PositionTextBox();
    }

    private void PositionTextBox()
    {
        if (_text is null) return;
        float scale = Theme.Scale(this);
        int pad = (int)Math.Round(13f * scale);
        int icon = (int)Math.Round(15f * scale);
        int gap = (int)Math.Round(9f * scale);
        int left = pad + icon + gap;
        int right = pad + (string.IsNullOrEmpty(_text.Text) ? 0 : (int)Math.Round(26f * scale));
        int height = _text.PreferredHeight;
        int width = Math.Max(20, Width - left - right);
        _text.Bounds = new Rectangle(left, Math.Max(0, (Height - height) / 2), width, height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float scale = Theme.Scale(this);
        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using var path = Theme.Rounded(rect, Theme.ControlRadius * scale);
        using (var brush = new SolidBrush(Theme.Field)) g.FillPath(brush, path);
        using (var pen = new Pen(_focused ? Theme.CardBorderHover : Theme.FieldBorder, Theme.ControlBorderWidth + 0.2f))
            g.DrawPath(pen, path);

        // magnifier
        float icon = 15f * scale;
        float cx = 13f * scale + icon / 2f;
        float cy = Height / 2f;
        float radius = 5.2f * scale;
        using (var pen = new Pen(_focused ? Theme.Gold : Theme.TextDim, 1.7f)
                   { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawEllipse(pen, cx - radius, cy - radius, radius * 2f, radius * 2f);
            g.DrawLine(pen, cx + radius * 0.55f, cy + radius * 0.55f, cx + radius * 1.45f, cy + radius * 1.45f);
        }

        if (!string.IsNullOrEmpty(_text.Text))
        {
            float clearSize = 13f * scale;
            float clearX = Width - 13f * scale - clearSize;
            float clearY = (Height - clearSize) / 2f;
            _clearRect = new RectangleF(clearX - 4f * scale, clearY - 4f * scale,
                clearSize + 8f * scale, clearSize + 8f * scale);
            using var pen = new Pen(_hoverClear ? Theme.Text : Theme.TextDim, 1.6f)
                { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, clearX, clearY, clearX + clearSize, clearY + clearSize);
            g.DrawLine(pen, clearX + clearSize, clearY, clearX, clearY + clearSize);
        }
        else
        {
            _clearRect = RectangleF.Empty;
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (_clearRect.Contains(e.Location) && !string.IsNullOrEmpty(_text.Text))
        {
            _text.Clear();
            _text.Focus();
            return;
        }
        _text.Focus();
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool overClear = _clearRect.Contains(e.Location);
        if (overClear != _hoverClear)
        {
            _hoverClear = overClear;
            Invalidate();
        }
        Cursor = overClear ? Cursors.Hand : Cursors.IBeam;
        base.OnMouseMove(e);
    }

    public new void Focus() => _text.Focus();

    private sealed class CueTextBox : TextBox
    {
        public string CueText { get; set; } = "";

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            if (m.Msg == 0x000F && TextLength == 0 && !Focused && CueText.Length > 0)
            {
                using var g = Graphics.FromHwnd(Handle);
                TextRenderer.DrawText(g, CueText, Font, ClientRectangle, Theme.TextDim,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }
    }
}
