namespace RpgLauncher;

/// <summary>
/// Draws at a low resolution and scales it up with hard pixels, the way RPG Maker 2000 blows up its 320×240
/// screen. Subclasses paint in canvas pixels, and pointer positions are handed to them in canvas pixels too.
/// </summary>
internal abstract class PixelCanvas : Control
{
    private static readonly RenderOptions Crisp = new()
    {
        BitmapInterpolationMode = BitmapInterpolationMode.None,
        EdgeMode = EdgeMode.Aliased,
        TextRenderingMode = TextRenderingMode.Alias
    };

    private RenderTargetBitmap? _canvas;
    private bool _dirty = true;
    private Point _origin;
    private double _scaling = 1;

    /// <summary>Device pixels per canvas pixel.</summary>
    protected int Zoom { get; private set; } = 2;

    /// <summary>The canvas size in canvas pixels.</summary>
    protected Size CanvasSize { get; private set; }

    protected PixelCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    /// <summary>Paints the whole canvas; called only after <see cref="Redraw"/>.</summary>
    protected abstract void Paint(DrawingContext g, Size size);

    /// <summary>Asks for the canvas to be painted again before the next frame.</summary>
    public void Redraw()
    {
        _dirty = true;
        InvalidateVisual();
    }

    /// <summary>
    /// Canvas pixels per device pixel: about 450–700 canvas pixels high, so a 1080p window shows a 960×540 canvas
    /// at 2× and a 1440p one a 1280×720 canvas.
    /// </summary>
    private static int PickZoom(int deviceWidth, int deviceHeight) =>
        Math.Max(1, Math.Min(deviceWidth / 900, deviceHeight / 480));

    public override void Render(DrawingContext context)
    {
        _scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        int deviceWidth = (int)Math.Floor(Bounds.Width * _scaling);
        int deviceHeight = (int)Math.Floor(Bounds.Height * _scaling);
        if (deviceWidth < 2 || deviceHeight < 2) return;

        int zoom = PickZoom(deviceWidth, deviceHeight);
        var pixels = new PixelSize(Math.Max(1, deviceWidth / zoom), Math.Max(1, deviceHeight / zoom));
        if (_canvas is null || _canvas.PixelSize != pixels || zoom != Zoom)
        {
            _canvas?.Dispose();
            _canvas = new RenderTargetBitmap(pixels, new Vector(96, 96));
            Zoom = zoom;
            CanvasSize = new Size(pixels.Width, pixels.Height);
            _dirty = true;
        }

        if (_dirty)
        {
            _dirty = false;
            using var g = _canvas.CreateDrawingContext(true);
            using (g.PushRenderOptions(Crisp))
                Paint(g, CanvasSize);
        }

        // Whole device pixels per canvas pixel; any leftover is split around the picture.
        double width = pixels.Width * zoom / _scaling, height = pixels.Height * zoom / _scaling;
        _origin = new Point(Math.Floor((Bounds.Width - width) / 2 * _scaling) / _scaling,
            Math.Floor((Bounds.Height - height) / 2 * _scaling) / _scaling);
        using (context.PushRenderOptions(Crisp))
            context.DrawImage(_canvas, new Rect(0, 0, pixels.Width, pixels.Height), new Rect(_origin, new Size(width, height)));
    }

    /// <summary>A point in this control's coordinates, in canvas pixels.</summary>
    protected Point ToCanvas(Point point) =>
        new((point.X - _origin.X) * _scaling / Zoom, (point.Y - _origin.Y) * _scaling / Zoom);

    /// <summary>A canvas rectangle in this control's coordinates (for placing real controls over the canvas).</summary>
    protected Rect FromCanvas(Rect rect) =>
        new(_origin.X + rect.X * Zoom / _scaling, _origin.Y + rect.Y * Zoom / _scaling,
            rect.Width * Zoom / _scaling, rect.Height * Zoom / _scaling);

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Redraw();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _canvas?.Dispose();
        _canvas = null;
    }
}
