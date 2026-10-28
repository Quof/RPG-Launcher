using Avalonia.Platform;
using Avalonia.Rendering;

namespace RpgLauncher;

/// <summary>Lets the user choose which part of a picture fills the 16:9 card.</summary>
internal sealed class ImageFramingDialog : DialogWindow
{
    private readonly FramingCanvas _canvas;
    private readonly TextBlock _hint;
    private readonly TextBlock _zoomLabel;
    private readonly PillButton _zoomOut;
    private readonly PillButton _zoomIn;
    private readonly PillButton _reset;
    private readonly PillButton _save;
    private readonly PillButton _cancel;
    private readonly UiFont _hintFont = Look.Font(8.6);
    private readonly UiFont _zoomFont = Look.Font(9, bold: true);
    private double _canvasW, _canvasH;

    public ImageFraming Framing => _canvas.Framing.Clone();

    /// <param name="image">The picture, e.g. from <see cref="GameImageLoader.LoadSourcePicture"/>; the caller disposes it.</param>
    public ImageFramingDialog(Bitmap image, ImageFraming? framing, string gameTitle)
    {
        Title = "Image framing \u00b7 " + gameTitle;

        _canvas = new FramingCanvas(image, framing?.Clone() ?? new ImageFraming());
        _canvas.FramingChanged += UpdateZoomLabel;

        _hint = Ui.Label("Drag to move \u00b7 scroll to zoom", _hintFont, Look.TextDim);
        _zoomLabel = Ui.Label("", _zoomFont, Look.TextMuted, TextAlignment.Center);

        _zoomOut = new PillButton { Text = "−" };
        _zoomIn = new PillButton { Text = "+" };
        _reset = new PillButton { Text = "Reset" };
        _save = new PillButton { Text = "Save", Checked = true };
        _cancel = new PillButton { Text = "Cancel" };

        _zoomOut.Click += (_, _) => _canvas.ZoomBy(1 / 1.2);
        _zoomIn.Click += (_, _) => _canvas.ZoomBy(1.2);
        _reset.Click += (_, _) => _canvas.ResetFraming();
        _save.Click += (_, _) => Accept();
        _cancel.Click += (_, _) => Cancel();

        var backdrop = new Backdrop(PaintBackdrop);
        Body.Children.AddRange(new Control[] { backdrop, _canvas, _hint, _zoomLabel, _zoomOut, _zoomIn, _reset, _save, _cancel });

        UpdateZoomLabel();
        SizeToScreen(Screens?.Primary);

        const double pad = 20, buttonH = 36;
        Body.Layout = size =>
        {
            Body.Place(backdrop, 0, 0, size.Width, size.Height);
            Body.Place(_canvas, pad, pad, _canvasW, _canvasH);
            Body.Place(_hint, pad, pad + _canvasH + 10, _canvasW, 20);

            double y = size.Height - pad - buttonH;
            const double small = 38;
            Body.Place(_zoomOut, pad, y, small, buttonH);
            Body.Place(_zoomLabel, pad + small + 4, y, 58, buttonH);
            Body.Place(_zoomIn, pad + small + 4 + 58 + 4, y, small, buttonH);
            Body.Place(_reset, pad + small + 4 + 58 + 4 + small + 12, y, 84, buttonH);

            const double buttonW = 96;
            Body.Place(_save, size.Width - pad - buttonW, y, buttonW, buttonH);
            Body.Place(_cancel, size.Width - pad - buttonW - 10 - buttonW, y, buttonW, buttonH);
        };
    }

    /// <summary>Shows the dialog with its canvas sized for the screen <paramref name="owner"/> is on; true when saved.</summary>
    public override Task<bool> ShowAsync(Window owner)
    {
        SizeToScreen(owner.Screens?.ScreenFromWindow(owner) ?? owner.Screens?.Primary);
        return base.ShowAsync(owner);
    }

    /// <summary>Sizes the canvas to the picture's shape, within what fits on the screen.</summary>
    private void SizeToScreen(Screen? screen)
    {
        // working area in device-independent pixels; without one only the fixed limits apply
        var area = screen is { WorkingArea: { Width: > 0, Height: > 0 } working }
            ? new Size(working.Width / screen.Scaling, working.Height / screen.Scaling)
            : new Size(double.MaxValue / 4, double.MaxValue / 4);
        double pad = 20;
        double maxW = Math.Min(900, area.Width - 80);
        double maxH = Math.Min(620, area.Height - 220);
        var imageSize = _canvas.ImageSize;
        double fit = Math.Min(maxW / imageSize.Width, maxH / imageSize.Height);
        // At least wide enough for the zoom buttons and Reset on the left and Cancel and Save on the right.
        _canvasW = Math.Max(460, (int)(imageSize.Width * fit));
        _canvasH = Math.Max(240, (int)(imageSize.Height * fit));

        double buttonH = 36;
        double width = _canvasW + pad * 2;
        SetClientSize(width, pad + _canvasH + 10 + 20 + 14 + buttonH + pad);
    }

    private void UpdateZoomLabel() => _zoomLabel.Text = $"{_canvas.Framing.Zoom * 100:0}%";

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Enter) Accept();
        else if (e.Key == Key.Escape) Cancel();
        else if (e.Key is Key.Add or Key.OemPlus) _canvas.ZoomBy(1.2);
        else if (e.Key is Key.Subtract or Key.OemMinus) _canvas.ZoomBy(1 / 1.2);
        else return;
        e.Handled = true;
    }

    // Blob style: a blob sits between the zoom controls and Cancel.
    private void PaintBackdrop(DrawingContext g, Size size) =>
        Mascots.DrawCentered(g, MascotSpot.FramingDialog, (Body.PlaceOf(_reset).Right + Body.PlaceOf(_cancel).Left) / 2,
            size.Height, 50);

    /// <summary>Paints under the dialog's controls, as the form's OnPaint did.</summary>
    private sealed class Backdrop : Control
    {
        private readonly Action<DrawingContext, Size> _paint;

        public Backdrop(Action<DrawingContext, Size> paint)
        {
            _paint = paint;
            IsHitTestVisible = false;
        }

        public override void Render(DrawingContext g) => _paint(g, Bounds.Size);
    }

    private sealed class FramingCanvas : Control, ICustomHitTest
    {
        private readonly Bitmap _image;
        private readonly SharpPicture _picture = new();
        private bool _dragging;
        private Point _dragStart;
        private Point _dragStartCenter;

        public ImageFraming Framing { get; }
        public Size ImageSize => _image.Size;
        public event Action? FramingChanged;

        public FramingCanvas(Bitmap image, ImageFraming framing)
        {
            _image = image;
            Framing = framing;
            Cursor = new Cursor(StandardCursorType.SizeAll);
            Normalize();
        }

        public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

        /// <summary>Where the whole picture is drawn inside the canvas.</summary>
        private Rect ImageRect()
        {
            double fit = Math.Min(Bounds.Width / ImageSize.Width, Bounds.Height / ImageSize.Height);
            double w = ImageSize.Width * fit, h = ImageSize.Height * fit;
            return new Rect((Bounds.Width - w) / 2, (Bounds.Height - h) / 2, w, h);
        }

        private Rect CropOnCanvas()
        {
            var image = ImageRect();
            var crop = ImageFraming.CropRect(ImageSize, Framing);
            double s = image.Width / ImageSize.Width;
            return new Rect(image.X + crop.X * s, image.Y + crop.Y * s, crop.Width * s, crop.Height * s);
        }

        /// <summary>Keeps the stored center where the clamped crop actually is.</summary>
        private void Normalize()
        {
            Framing.Zoom = Math.Clamp(Framing.Zoom, 1.0, ImageFraming.MaxZoom);
            var crop = ImageFraming.CropRect(ImageSize, Framing);
            Framing.CenterX = (crop.X + crop.Width / 2.0) / ImageSize.Width;
            Framing.CenterY = (crop.Y + crop.Height / 2.0) / ImageSize.Height;
        }

        public void ZoomBy(double factor)
        {
            Framing.Zoom = Math.Clamp(Framing.Zoom * factor, 1.0, ImageFraming.MaxZoom);
            Normalize();
            InvalidateVisual();
            FramingChanged?.Invoke();
        }

        public void ResetFraming()
        {
            Framing.CenterX = 0.5;
            Framing.CenterY = 0.5;
            Framing.Zoom = 1.0;
            Normalize();
            InvalidateVisual();
            FramingChanged?.Invoke();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            _dragging = true;
            _dragStart = e.GetPosition(this);
            _dragStartCenter = new Point(Framing.CenterX, Framing.CenterY);
            e.Pointer.Capture(this);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_dragging) return;
            var image = ImageRect();
            var point = e.GetPosition(this);
            Framing.CenterX = _dragStartCenter.X + (point.X - _dragStart.X) / image.Width;
            Framing.CenterY = _dragStartCenter.Y + (point.Y - _dragStart.Y) / image.Height;
            Normalize();
            InvalidateVisual();
            FramingChanged?.Invoke();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            _dragging = false;
            e.Pointer.Capture(null);
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            _dragging = false;
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            // Only the vertical wheel zooms, as WinForms' MouseWheel.
            if (e.Delta.Y == 0) return;
            ZoomBy(e.Delta.Y > 0 ? 1.1 : 1 / 1.1);
            e.Handled = true;
        }

        public override void Render(DrawingContext g)
        {
            g.FillRectangle(Look.Brush(Look.Canvas), new Rect(Bounds.Size));

            var image = ImageRect();
            _picture.Draw(g, this, _image, image);

            // Dim everything the card will not show.
            var crop = CropOnCanvas();
            var outside = new StreamGeometry();
            using (var ctx = outside.Open())
            {
                ctx.SetFillRule(FillRule.EvenOdd);
                foreach (var rect in new[] { image, crop })
                {
                    ctx.BeginFigure(rect.TopLeft, true);
                    ctx.LineTo(rect.TopRight);
                    ctx.LineTo(rect.BottomRight);
                    ctx.LineTo(rect.BottomLeft);
                    ctx.EndFigure(true);
                }
            }
            g.DrawGeometry(Look.Brush(Color.FromArgb(165, 8, 8, 12)), null, outside);

            var thirds = Look.Pen(Color.FromArgb(70, 255, 255, 255), 1);
            for (int i = 1; i <= 2; i++)
            {
                double x = crop.Left + crop.Width * i / 3;
                double y = crop.Top + crop.Height * i / 3;
                g.DrawLine(thirds, new Point(x, crop.Top), new Point(x, crop.Bottom));
                g.DrawLine(thirds, new Point(crop.Left, y), new Point(crop.Right, y));
            }

            g.DrawRectangle(null, Look.Pen(Look.Gold, 2), crop);
        }
    }
}
