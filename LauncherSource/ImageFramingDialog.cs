using System.Drawing.Drawing2D;

namespace RpgLauncher;

/// <summary>Lets the user choose which part of a picture fills the 16:9 card.</summary>
internal sealed class ImageFramingDialog : Form
{
    private readonly FramingCanvas _canvas;
    private readonly Label _hint;
    private readonly Label _zoomLabel;
    private readonly PillButton _zoomOut;
    private readonly PillButton _zoomIn;
    private readonly PillButton _reset;
    private readonly PillButton _save;
    private readonly PillButton _cancel;
    private readonly Font _hintFont = Theme.Font(8.6f);
    private readonly Font _zoomFont = Theme.Font(9f, FontStyle.Bold);

    public ImageFraming Framing => _canvas.Framing.Clone();

    public ImageFramingDialog(Bitmap image, ImageFraming? framing, string gameTitle)
    {
        Text = "Image framing \u00b7 " + gameTitle;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Font(9f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;

        _canvas = new FramingCanvas(image, framing?.Clone() ?? new ImageFraming());
        _canvas.FramingChanged += UpdateZoomLabel;

        _hint = new Label
        {
            Text = "Drag to move \u00b7 scroll to zoom",
            Font = _hintFont,
            ForeColor = Theme.TextDim,
            BackColor = Color.Transparent,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        _zoomLabel = new Label
        {
            Font = _zoomFont,
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter
        };

        _zoomOut = new PillButton { Text = "−" };
        _zoomIn = new PillButton { Text = "+" };
        _reset = new PillButton { Text = "Reset" };
        _save = new PillButton { Text = "Save", Checked = true };
        _cancel = new PillButton { Text = "Cancel" };

        _zoomOut.Click += (_, _) => _canvas.ZoomBy(1 / 1.2);
        _zoomIn.Click += (_, _) => _canvas.ZoomBy(1.2);
        _reset.Click += (_, _) => _canvas.ResetFraming();
        _save.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        _cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        Controls.AddRange(new Control[] { _canvas, _hint, _zoomLabel, _zoomOut, _zoomIn, _reset, _save, _cancel });

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { DialogResult = DialogResult.OK; Close(); }
            else if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            else if (e.KeyCode is Keys.Add or Keys.Oemplus) _canvas.ZoomBy(1.2);
            else if (e.KeyCode is Keys.Subtract or Keys.OemMinus) _canvas.ZoomBy(1 / 1.2);
        };

        UpdateZoomLabel();
    }

    private void UpdateZoomLabel() => _zoomLabel.Text = $"{_canvas.Framing.Zoom * 100:0}%";

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { NativeMethods.ApplyDarkTitleBar(Handle); } catch { }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        float scale = Theme.Scale(this);
        int R(float value) => (int)Math.Round(value * scale);

        // Size the canvas to the picture's shape, within what fits on screen.
        var area = Screen.FromControl(Owner ?? this).WorkingArea;
        int pad = R(20f);
        int maxW = Math.Min(R(900f), area.Width - R(80f));
        int maxH = Math.Min(R(620f), area.Height - R(220f));
        var imageSize = _canvas.ImageSize;
        float fit = Math.Min(maxW / (float)imageSize.Width, maxH / (float)imageSize.Height);
        int canvasW = Math.Max(R(420f), (int)(imageSize.Width * fit));
        int canvasH = Math.Max(R(240f), (int)(imageSize.Height * fit));

        int buttonH = R(36f);
        int width = canvasW + pad * 2;
        ClientSize = new Size(width, pad + canvasH + R(10f) + R(20f) + R(14f) + buttonH + pad);

        _canvas.SetBounds(pad, pad, canvasW, canvasH);
        _hint.SetBounds(pad, _canvas.Bottom + R(10f), canvasW, R(20f));

        int y = ClientSize.Height - pad - buttonH;
        int small = R(38f);
        _zoomOut.Bounds = new Rectangle(pad, y, small, buttonH);
        _zoomLabel.SetBounds(_zoomOut.Right + R(4f), y, R(58f), buttonH);
        _zoomIn.Bounds = new Rectangle(_zoomLabel.Right + R(4f), y, small, buttonH);
        _reset.Bounds = new Rectangle(_zoomIn.Right + R(12f), y, R(84f), buttonH);

        int buttonW = R(96f);
        _save.Bounds = new Rectangle(width - pad - buttonW, y, buttonW, buttonH);
        _cancel.Bounds = new Rectangle(_save.Left - R(10f) - buttonW, y, buttonW, buttonH);
        CenterToParent();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // Blob style: a blob sits between the zoom controls and Cancel.
        Mascots.DrawCentered(e.Graphics, MascotSpot.FramingDialog, (_reset.Right + _cancel.Left) / 2f,
            ClientSize.Height, 50f * Theme.Scale(this));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hintFont.Dispose();
            _zoomFont.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class FramingCanvas : Control
    {
        private readonly Bitmap _image;
        private bool _dragging;
        private Point _dragStart;
        private PointF _dragStartCenter;

        public ImageFraming Framing { get; }
        public Size ImageSize => _image.Size;
        public event Action? FramingChanged;

        public FramingCanvas(Bitmap image, ImageFraming framing)
        {
            _image = image;
            Framing = framing;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Canvas;
            Cursor = Cursors.SizeAll;
            Normalize();
        }

        /// <summary>Where the whole picture is drawn inside the canvas.</summary>
        private RectangleF ImageRect()
        {
            float fit = Math.Min(Width / (float)_image.Width, Height / (float)_image.Height);
            float w = _image.Width * fit, h = _image.Height * fit;
            return new RectangleF((Width - w) / 2f, (Height - h) / 2f, w, h);
        }

        private RectangleF CropOnCanvas()
        {
            var image = ImageRect();
            var crop = ImageFraming.CropRect(_image.Size, Framing);
            float s = image.Width / _image.Width;
            return new RectangleF(image.X + crop.X * s, image.Y + crop.Y * s, crop.Width * s, crop.Height * s);
        }

        /// <summary>Keeps the stored center where the clamped crop actually is.</summary>
        private void Normalize()
        {
            Framing.Zoom = Math.Clamp(Framing.Zoom, 1.0, ImageFraming.MaxZoom);
            var crop = ImageFraming.CropRect(_image.Size, Framing);
            Framing.CenterX = (crop.X + crop.Width / 2.0) / _image.Width;
            Framing.CenterY = (crop.Y + crop.Height / 2.0) / _image.Height;
        }

        public void ZoomBy(double factor)
        {
            Framing.Zoom = Math.Clamp(Framing.Zoom * factor, 1.0, ImageFraming.MaxZoom);
            Normalize();
            Invalidate();
            FramingChanged?.Invoke();
        }

        public void ResetFraming()
        {
            Framing.CenterX = 0.5;
            Framing.CenterY = 0.5;
            Framing.Zoom = 1.0;
            Normalize();
            Invalidate();
            FramingChanged?.Invoke();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _dragging = true;
                _dragStart = e.Location;
                _dragStartCenter = new PointF((float)Framing.CenterX, (float)Framing.CenterY);
                Capture = true;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragging)
            {
                var image = ImageRect();
                Framing.CenterX = _dragStartCenter.X + (e.X - _dragStart.X) / image.Width;
                Framing.CenterY = _dragStartCenter.Y + (e.Y - _dragStart.Y) / image.Height;
                Normalize();
                Invalidate();
                FramingChanged?.Invoke();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _dragging = false;
            Capture = false;
            base.OnMouseUp(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            ZoomBy(e.Delta > 0 ? 1.1 : 1 / 1.1);
            base.OnMouseWheel(e);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            Focus();
            base.OnMouseEnter(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var image = ImageRect();
            g.DrawImage(_image, image);

            // Dim everything the card will not show.
            var crop = CropOnCanvas();
            using (var outside = new Region(image))
            {
                outside.Exclude(crop);
                using var dim = new SolidBrush(Color.FromArgb(165, 8, 8, 12));
                g.FillRegion(dim, outside);
            }

            using (var thirds = new Pen(Color.FromArgb(70, 255, 255, 255), 1f))
            {
                for (int i = 1; i <= 2; i++)
                {
                    float x = crop.Left + crop.Width * i / 3f;
                    float y = crop.Top + crop.Height * i / 3f;
                    g.DrawLine(thirds, x, crop.Top, x, crop.Bottom);
                    g.DrawLine(thirds, crop.Left, y, crop.Right, y);
                }
            }

            using var border = new Pen(Theme.Gold, 2f);
            g.DrawRectangle(border, crop.X, crop.Y, crop.Width, crop.Height);
        }
    }
}
