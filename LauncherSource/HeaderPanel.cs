using System.Drawing.Drawing2D;

namespace RpgLauncher;

internal sealed class HeaderPanel : Panel
{
    private readonly Font _logoFont = Theme.Font(19f, FontStyle.Bold);
    private readonly Font _subtitleFont = Theme.Font(8.2f);

    private string _subtitle = "";

    /// <summary>A drawing that peeks up beside the logo (Blob style); null for none.</summary>
    public Bitmap? Mascot { get; set; }

    private float MascotHeight => 78f * Theme.Scale(this);

    /// <summary>Left edge of the logo text, after the mascot when one is shown.</summary>
    private float LogoLeft
    {
        get
        {
            float scale = Theme.Scale(this);
            if (Mascot is null) return 28f * scale;
            return 16f * scale + Mascot.Width * MascotHeight / Mascot.Height + 12f * scale;
        }
    }
    public string Subtitle
    {
        get => _subtitle;
        set
        {
            if (_subtitle == value) return;
            _subtitle = value;
            Invalidate();
        }
    }

    public HeaderPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
    }

    /// <summary>Bounds of the logo text, so controls can sit beside it.</summary>
    public Rectangle LogoBounds()
    {
        float scale = Theme.Scale(this);
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        var rpg = TextRenderer.MeasureText("RPG", _logoFont, new Size(int.MaxValue, int.MaxValue), flags);
        var launcher = TextRenderer.MeasureText("LAUNCHER", _logoFont, new Size(int.MaxValue, int.MaxValue), flags);
        int x = (int)LogoLeft, y = (int)(20f * scale);
        return new Rectangle(x, y, rpg.Width + (int)(6f * scale) + launcher.Width, rpg.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (var gradient = new LinearGradientBrush(ClientRectangle, Theme.HeaderTop, Theme.Bg, LinearGradientMode.Vertical))
            g.FillRectangle(gradient, ClientRectangle);

        float scale = Theme.Scale(this);
        if (Theme.HeaderGlow.A > 0)
        {
            using var glow = new SolidBrush(Theme.HeaderGlow);
            g.FillEllipse(glow, -160f * scale, -200f * scale, 520f * scale, 380f * scale);
            g.FillEllipse(glow, -80f * scale, -140f * scale, 360f * scale, 240f * scale);
        }

        if (Mascot is not null)
            Mascots.Draw(g, Mascot, 16f * scale, Height - 1, MascotHeight);

        float x = LogoLeft;
        float y = 20f * scale;

        var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        var rpgSize = TextRenderer.MeasureText(g, "RPG", _logoFont, new Size(int.MaxValue, int.MaxValue), flags);
        TextRenderer.DrawText(g, "RPG", _logoFont, new Point((int)x, (int)y), Theme.Gold, flags);
        TextRenderer.DrawText(g, "LAUNCHER", _logoFont,
            new Point((int)(x + rpgSize.Width + 6f * scale), (int)y), Theme.Text, flags);

        TextRenderer.DrawText(g, _subtitle, _subtitleFont,
            new Point((int)(x + 1f * scale), (int)(y + 34f * scale)), Theme.TextMuted, flags);

        using var border = new Pen(Theme.IsLight ? Theme.CardBorder : Theme.Divider, Theme.IsLight ? 2f : 1f);
        g.DrawLine(border, 0, Height - 1, Width, Height - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _logoFont.Dispose();
            _subtitleFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
