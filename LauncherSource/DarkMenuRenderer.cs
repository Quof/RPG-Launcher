using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;

namespace RpgLauncher;

internal static class Menus
{
    // Shared: menus are created per click and would otherwise each leak a font.
    private static readonly Font MenuFont = Theme.Font(9.5f);

    /// <summary>A dark menu. Only menus with checkmarks reserve the check column, so plain menus aren't indented.</summary>
    public static DarkContextMenu Create(bool checks = false)
    {
        var menu = new DarkContextMenu
        {
            BackColor = Theme.MenuBg,
            ForeColor = Theme.Text,
            Font = MenuFont,
            ShowImageMargin = false,
            ShowCheckMargin = checks,
            Renderer = new DarkMenuRenderer(),
            Padding = new Padding(4),
            DropShadowEnabled = true,
            AutoSize = true
        };
        return menu;
    }

    public static ToolStripMenuItem Item(string text, EventHandler? onClick = null)
    {
        var item = new ToolStripMenuItem(text)
        {
            ForeColor = Theme.Text,
            BackColor = Color.Transparent,
            Padding = new Padding(2, 4, 2, 4),
            AutoSize = true
        };
        if (onClick is not null) item.Click += onClick;
        return item;
    }

    public static ToolStripSeparator Separator() => new() { AutoSize = false, Height = 9 };

    /// <summary>Gives a submenu the same look as its parent menu.</summary>
    public static void StyleSubmenu(ToolStripMenuItem item, ContextMenuStrip owner, bool checks)
    {
        if (item.DropDown is ToolStripDropDownMenu dropDown)
        {
            dropDown.ShowCheckMargin = checks;
            dropDown.ShowImageMargin = false;
            dropDown.Padding = owner.Padding;
        }
        item.DropDown.BackColor = Theme.MenuBg;
        item.DropDown.Font = owner.Font;
        item.DropDown.Renderer = owner.Renderer;
        foreach (ToolStripItem child in item.DropDownItems)
            child.ForeColor = Theme.Text;
    }

    /// <summary>Shows a menu and disposes it on close, deferred until ToolStripManager has finished activating it.</summary>
    public static void Show(ContextMenuStrip menu, Control owner, Point position,
        ToolStripDropDownDirection direction = ToolStripDropDownDirection.Default)
    {
        menu.Closed += (_, _) =>
        {
            try
            {
                owner.BeginInvoke(new Action(() =>
                {
                    if (!menu.IsDisposed) menu.Dispose();
                }));
            }
            catch
            {
                if (!menu.IsDisposed) menu.Dispose();
            }
        };
        menu.Show(owner, position, direction);
    }
}

/// <summary>A dark context menu that a drag can paint over (see <see cref="MenuReorder"/>).</summary>
internal sealed class DarkContextMenu : ContextMenuStrip
{
    public DarkContextMenu()
    {
        DoubleBuffered = true;
    }

    /// <summary>Paints on top of the items while set; hover highlights are hidden meanwhile.</summary>
    public Action<Graphics>? PaintOver { get; set; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var state = e.Graphics.Save();
        base.OnPaint(e);
        e.Graphics.Restore(state);
        PaintOver?.Invoke(e.Graphics);
    }
}

internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public const float HighlightRadius = 5f;

    // Gap between a row's highlight and the menu's sides.
    private const int HighlightGap = 5;

    /// <summary>Where an item's text and check mark were last drawn, so a drag can redraw the row exactly.</summary>
    internal sealed class RowLayout
    {
        public Rectangle Text;
        public TextFormatFlags Format;
        public Rectangle Check;
    }

    private static readonly ConditionalWeakTable<ToolStripItem, RowLayout> Layouts = new();

    public static RowLayout? LayoutOf(ToolStripItem item) => Layouts.TryGetValue(item, out var layout) ? layout : null;

    public DarkMenuRenderer() : base(new DarkColorTable())
    {
        RoundedEdges = false;
    }

    /// <summary>The hover highlight of an item, in the item's coordinates.</summary>
    public static Rectangle HighlightBounds(ToolStripItem item, ToolStrip strip)
    {
        // Items can extend under the menu's right padding; inset from the menu's edges so both gaps match.
        int left = Math.Max(0, HighlightGap - item.Bounds.X);
        int right = Math.Min(item.Width, strip.Width - HighlightGap - item.Bounds.X);
        return new Rectangle(left, 1, Math.Max(1, right - left), item.Height - 2);
    }

    private static bool Dragging(ToolStrip? strip) => strip is DarkContextMenu { PaintOver: not null };

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.MenuBg);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is not ToolStripDropDown) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        using var pen = new Pen(Theme.FieldBorder, 1f);
        g.DrawRectangle(pen, rect);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled || e.ToolStrip is not { } strip || Dragging(strip)) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.Rounded(HighlightBounds(e.Item, strip), HighlightRadius);
        using var brush = new SolidBrush(Theme.FieldHover);
        g.FillPath(brush, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        // Menu items put their padding below the text; center the text in the whole row instead.
        var rect = e.TextRectangle;
        e.TextRectangle = new Rectangle(rect.X, 0, rect.Width, e.Item.Height);
        e.TextFormat |= TextFormatFlags.VerticalCenter;

        bool selected = e.Item.Selected && !Dragging(e.ToolStrip);
        e.TextColor = e.Item.Enabled
            ? (selected ? Theme.Text : Theme.TextMuted)
            : Theme.TextDim;

        // the item's own text, not its (right-aligned) shortcut text
        if ((e.TextFormat & TextFormatFlags.Right) == 0)
        {
            var layout = Layouts.GetOrCreateValue(e.Item);
            layout.Text = e.TextRectangle;
            layout.Format = e.TextFormat;
        }
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var r = e.ImageRectangle;
        if (r.IsEmpty || r.Width == 0) r = new Rectangle(10, 0, 16, 16);
        r.Y = (e.Item.Height - r.Height) / 2;
        Layouts.GetOrCreateValue(e.Item).Check = r;
        DrawCheck(e.Graphics, r);
    }

    public static void DrawCheck(Graphics g, Rectangle r)
    {
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float x = r.Left + 2f;
        float y = r.Top + r.Height / 2f;
        using var pen = new Pen(Theme.Gold, 1.9f)
            { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, new[]
        {
            new PointF(x, y),
            new PointF(x + r.Width * 0.32f, y + r.Height * 0.30f),
            new PointF(x + r.Width * 0.80f, y - r.Height * 0.30f)
        });
        g.SmoothingMode = smoothing;
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var g = e.Graphics;
        int y = e.Item.Height / 2;
        using var pen = new Pen(Theme.Divider, 1f);
        g.DrawLine(pen, 12, y, e.Item.Width - 12, y);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        // A thin chevron instead of the default solid triangle, centered on the row like the text.
        var g = e.Graphics;
        var r = e.ArrowRectangle;
        float scale = e.Item?.Owner is { } owner ? Theme.Scale(owner) : 1f;
        float cx = r.Left + r.Width / 2f;
        float cy = e.Item is not null ? e.Item.Height / 2f : r.Top + r.Height / 2f;
        float a = 2.2f * scale, b = 4f * scale;

        PointF[] points = e.Direction switch
        {
            ArrowDirection.Left => new[] { new PointF(cx + a, cy - b), new PointF(cx - a, cy), new PointF(cx + a, cy + b) },
            ArrowDirection.Up => new[] { new PointF(cx - b, cy + a), new PointF(cx, cy - a), new PointF(cx + b, cy + a) },
            ArrowDirection.Down => new[] { new PointF(cx - b, cy - a), new PointF(cx, cy + a), new PointF(cx + b, cy - a) },
            _ => new[] { new PointF(cx - a, cy - b), new PointF(cx + a, cy), new PointF(cx - a, cy + b) }
        };

        bool selected = e.Item is { Selected: true, Enabled: true } && !Dragging(e.Item.Owner);
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(selected ? Theme.Text : Theme.TextMuted, 1.5f * scale)
            { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen, points);
        g.SmoothingMode = smoothing;
    }
}

internal sealed class DarkColorTable : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Theme.MenuBg;
    public override Color ImageMarginGradientBegin => Theme.MenuBg;
    public override Color ImageMarginGradientMiddle => Theme.MenuBg;
    public override Color ImageMarginGradientEnd => Theme.MenuBg;
    public override Color MenuBorder => Theme.FieldBorder;
    public override Color MenuItemBorder => Theme.FieldBorder;
    public override Color MenuItemSelected => Theme.FieldHover;
    public override Color MenuItemSelectedGradientBegin => Theme.FieldHover;
    public override Color MenuItemSelectedGradientEnd => Theme.FieldHover;
    public override Color MenuStripGradientBegin => Theme.MenuBg;
    public override Color MenuStripGradientEnd => Theme.MenuBg;
    public override Color SeparatorDark => Theme.Divider;
    public override Color SeparatorLight => Theme.Divider;
    public override Color CheckBackground => Theme.AccentDim;
    public override Color CheckSelectedBackground => Theme.AccentDim;
    public override Color CheckPressedBackground => Theme.AccentDim;
}
