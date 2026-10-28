using Avalonia.Rendering;

namespace RpgLauncher;

/// <summary>The window's header: gradient, logo and subtitle, under the buttons the main window places on it.</summary>
internal sealed class HeaderPanel : ManualPanel, ICustomHitTest
{
    private static readonly UiFont LogoFont = Look.Font(19, bold: true);
    private static readonly UiFont SubtitleFont = Look.Font(8.2);

    private const double MascotHeight = 78;

    private readonly Backdrop _backdrop;
    private string _subtitle = "";
    private Bitmap? _mascot;

    public HeaderPanel()
    {
        // The glow and the bottom border reach past the edges; WinForms clipped them to the panel.
        ClipToBounds = true;
        // Panel.Render is sealed in Avalonia, so the header is painted by a child kept first (under the others).
        _backdrop = new Backdrop(this);
        Children.Add(_backdrop);
    }

    /// <summary>A drawing that peeks up beside the logo (Blob style); null for none.</summary>
    public Bitmap? Mascot
    {
        get => _mascot;
        set
        {
            _mascot = value;
            _backdrop.InvalidateVisual();
        }
    }

    /// <summary>Left edge of the logo text, after the mascot when one is shown.</summary>
    private double LogoLeft => Mascot is null ? 28 : 16 + Mascots.WidthAt(Mascot, MascotHeight) + 12;

    public string Subtitle
    {
        get => _subtitle;
        set
        {
            if (_subtitle == value) return;
            _subtitle = value ?? "";
            _backdrop.InvalidateVisual();
        }
    }

    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    /// <summary>Bounds of the logo text in whole pixels, so controls can sit beside it.</summary>
    public Rect LogoBounds()
    {
        var rpg = Look.Measure("RPG", LogoFont);
        var launcher = Look.Measure("LAUNCHER", LogoFont);
        return new Rect(Math.Floor(LogoLeft), 20, Math.Ceiling(rpg.Width) + 6 + Math.Ceiling(launcher.Width), Math.Ceiling(rpg.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Place(_backdrop, new Rect(finalSize));
        return base.ArrangeOverride(finalSize);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Look.Changed += _backdrop.InvalidateVisual;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Look.Changed -= _backdrop.InvalidateVisual;
    }

    private void Paint(DrawingContext g)
    {
        double width = Bounds.Width, height = Bounds.Height;
        g.FillRectangle(Look.Gradient(Look.HeaderTop, Look.Bg), new Rect(Bounds.Size));

        if (Look.HeaderGlow.A > 0)
        {
            var glow = Look.Brush(Look.HeaderGlow);
            g.DrawEllipse(glow, null, new Rect(-160, -200, 520, 380));
            g.DrawEllipse(glow, null, new Rect(-80, -140, 360, 240));
        }

        if (Mascot is not null)
            Mascots.Draw(g, Mascot, 16, height - 1, MascotHeight);

        // On whole pixels, as WinForms placed them.
        double x = Math.Floor(LogoLeft), y = 20;
        var rpg = Look.Format("RPG", LogoFont, Look.Gold);
        g.DrawText(rpg, new Point(x, y));
        g.DrawText(Look.Format("LAUNCHER", LogoFont, Look.Text), new Point(x + Math.Ceiling(rpg.WidthIncludingTrailingWhitespace) + 6, y));
        g.DrawText(Look.Format(_subtitle, SubtitleFont, Look.TextMuted), new Point(x + 1, y + 34));

        // on the last row of pixels
        g.DrawLine(Look.Pen(Look.IsLight ? Look.CardBorder : Look.Divider, Look.IsLight ? 2 : 1),
            new Point(0, height - 0.5), new Point(width, height - 0.5));
    }

    /// <summary>Paints the header under the panel's other children.</summary>
    private sealed class Backdrop : Control
    {
        private readonly HeaderPanel _owner;

        public Backdrop(HeaderPanel owner)
        {
            _owner = owner;
            IsHitTestVisible = false;
        }

        public override void Render(DrawingContext g) => _owner.Paint(g);
    }
}
