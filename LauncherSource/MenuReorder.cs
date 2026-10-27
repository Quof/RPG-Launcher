using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace RpgLauncher;

/// <summary>Drag-to-reorder for menu items. Rows are painted over the static items during the drag, so the menu keeps its size; contents swap on drop.</summary>
internal sealed class MenuReorder
{
    private const float PopSeconds = 0.18f;
    private const float SettleSeconds = 0.15f;
    // time constant of the rows' easing toward their places
    private const float SlideSeconds = 0.045f;

    private enum Phase { Idle, Pressed, Dragging, Settling }

    /// <summary>What a row shows; bounds are in menu coordinates relative to the row's top.</summary>
    private readonly record struct Row(string Text, bool Checked, Font Font, Rectangle TextBounds,
        TextFormatFlags Format, Rectangle CheckBounds);

    private readonly DarkContextMenu _menu;
    private readonly List<ToolStripMenuItem> _items;
    private readonly Action _reordered;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 10 };
    private readonly Stopwatch _clock = new();

    private Phase _phase;
    private int _source;
    private int _target;
    private Point _pressedAt;
    private float _grab;
    private float _liftY;
    private float _pop;
    private float _lift;
    private float _liftAtRelease;
    private Row[] _rows = Array.Empty<Row>();
    private float[] _rowY = Array.Empty<float>();
    private int[] _slotY = Array.Empty<int>();
    private double _lastTick;

    /// <summary>True once a press has become a drag, so the release isn't taken as a click.</summary>
    public bool Moved { get; private set; }

    public MenuReorder(DarkContextMenu menu, List<ToolStripMenuItem> items, Action reordered)
    {
        _menu = menu;
        _items = items;
        _reordered = reordered;

        foreach (var item in items)
        {
            var source = item;
            item.MouseDown += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) Press(source);
            };
        }

        // No submenu pops open while an item is dragged past it.
        foreach (ToolStripItem item in menu.Items)
        {
            if (item is ToolStripDropDownItem { HasDropDownItems: true } parent)
                parent.DropDown.Opening += (_, e) => e.Cancel |= _phase >= Phase.Dragging;
        }

        // The release that ends a drag lands on an item; that isn't a pick, so keep the menu open.
        menu.Closing += (_, e) =>
        {
            if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked && Moved) e.Cancel = true;
        };
        menu.Closed += (_, _) =>
        {
            if (_phase == Phase.Settling) Finish();
            else Stop();
        };
        menu.Disposed += (_, _) => _timer.Dispose();
        _timer.Tick += (_, _) => Tick();
    }

    private void Press(ToolStripMenuItem item)
    {
        if (_phase >= Phase.Dragging) Finish();
        Moved = false;
        _source = _items.IndexOf(item);
        if (_source < 0) return;

        _pressedAt = Cursor.Position;
        _grab = _menu.PointToClient(_pressedAt).Y - item.Bounds.Y;
        _phase = Phase.Pressed;
        _clock.Restart();
        _lastTick = 0;
        _timer.Start();
    }

    private void Begin()
    {
        _phase = Phase.Dragging;
        Moved = true;
        _rows = _items.Select(Snapshot).ToArray();
        _slotY = _items.Select(item => item.Bounds.Y).ToArray();
        _rowY = _slotY.Select(y => (float)y).ToArray();
        _target = _source;
        _liftY = _slotY[_source];
        _pop = 0f;
        _lift = 0f;
        _menu.PaintOver = Paint;
        _menu.Cursor = Cursors.SizeNS;
    }

    private void Tick()
    {
        double now = _clock.Elapsed.TotalSeconds;
        float dt = (float)Math.Min(0.05, now - _lastTick);
        _lastTick = now;
        bool held = (Control.MouseButtons & MouseButtons.Left) != 0;

        if (_phase == Phase.Pressed)
        {
            if (!held)
            {
                // a plain click; the item's Click already handled it
                Stop();
                return;
            }
            if (Math.Abs(Cursor.Position.Y - _pressedAt.Y) < SystemInformation.DragSize.Height) return;
            Begin();
        }

        if (_phase == Phase.Dragging)
        {
            if (held)
            {
                int height = _items[_source].Height;
                float y = _menu.PointToClient(Cursor.Position).Y - _grab;
                _liftY = Math.Clamp(y, _slotY[0] - height * 0.4f, _slotY[^1] + height * 0.4f);
                _target = NearestSlot(_liftY);
                _pop = Math.Min(1f, _pop + dt / PopSeconds);
                _lift = EaseOutBack(_pop);
            }
            else
            {
                _phase = Phase.Settling;
                _liftAtRelease = _lift;
                _pop = 1f;
                _menu.Cursor = Cursors.Default;
            }
        }

        if (_phase == Phase.Settling)
        {
            _pop = Math.Max(0f, _pop - dt / SettleSeconds);
            _lift = _liftAtRelease * SmoothStep(_pop);
            _liftY = Approach(_liftY, _slotY[_target], dt, SlideSeconds * 0.8f);
        }

        // The other rows close the gap the picked item left and open one where it will land.
        bool resting = true;
        int slot = 0;
        for (int i = 0; i < _items.Count; i++)
        {
            if (i == _source) continue;
            if (slot == _target) slot++;
            _rowY[i] = Approach(_rowY[i], _slotY[slot], dt, SlideSeconds);
            resting &= Math.Abs(_rowY[i] - _slotY[slot]) < 0.5f;
            slot++;
        }

        InvalidateRows();
        if (_phase == Phase.Settling && _pop <= 0f && resting && Math.Abs(_liftY - _slotY[_target]) < 0.5f)
            Finish();
    }

    private int NearestSlot(float y)
    {
        int best = 0;
        for (int i = 1; i < _slotY.Length; i++)
        {
            if (Math.Abs(y - _slotY[i]) < Math.Abs(y - _slotY[best])) best = i;
        }
        return best;
    }

    /// <summary>Puts the item where it was dropped, then hands painting back to the menu.</summary>
    private void Finish()
    {
        bool changed = _phase >= Phase.Dragging && _target != _source;
        if (changed)
        {
            var order = Enumerable.Range(0, _items.Count).ToList();
            order.RemoveAt(_source);
            order.Insert(_target, _source);

            // The menu may repaint while the items change, so first make the painted rows show the final order at rest.
            _rows = order.Select(i => _rows[i]).ToArray();
            _rowY = _slotY.Select(y => (float)y).ToArray();
            _source = _target;
            _liftY = _slotY[_target];
            _lift = 0f;

            var contents = order.Select(i => (_items[i].Text, _items[i].Tag, _items[i].Checked)).ToList();
            _menu.SuspendLayout();
            for (int i = 0; i < _items.Count; i++)
            {
                _items[i].Text = contents[i].Text;
                _items[i].Tag = contents[i].Tag;
                _items[i].Checked = contents[i].Checked;
            }
            _menu.ResumeLayout();
        }

        Stop();
        if (changed) _reordered();
    }

    private void Stop()
    {
        bool painting = _phase >= Phase.Dragging;
        _phase = Phase.Idle;
        Moved = false;
        _timer.Stop();
        _menu.PaintOver = null;
        _menu.Cursor = Cursors.Default;
        if (painting) _menu.Invalidate();
    }

    private void InvalidateRows()
    {
        if (_slotY.Length == 0) return;
        int height = _items[0].Height;
        int top = _slotY[0] - height;
        int bottom = _slotY[^1] + height * 2;
        _menu.Invalidate(new Rectangle(0, top, _menu.Width, bottom - top));
    }

    private void Paint(Graphics g)
    {
        if (_phase < Phase.Dragging || _slotY.Length == 0) return;

        var state = g.Save();
        g.SetClip(new Rectangle(1, 1, _menu.Width - 2, _menu.Height - 2), CombineMode.Intersect);

        // Cover the real rows; the snapshots are drawn on top.
        int top = _slotY[0];
        int bottom = _slotY[^1] + _items[^1].Height;
        using (var background = new SolidBrush(Theme.MenuBg))
            g.FillRectangle(background, 0, top, _menu.Width, bottom - top);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        float amount = Math.Clamp(_lift, 0f, 1f);
        float scale = Theme.Scale(_menu);

        // a faint slot where the item will land
        using (var path = Theme.Rounded(RowRect(_target, _slotY[_target]), DarkMenuRenderer.HighlightRadius))
        using (var brush = new SolidBrush(Blend(Theme.MenuBg, Theme.FieldHover, 0.6f * amount)))
            g.FillPath(brush, path);

        for (int i = 0; i < _rows.Length; i++)
        {
            if (i != _source) DrawRow(g, _rows[i], _rowY[i], 0f);
        }

        // Highlighted while lifted; once down, only if hovered, matching how the menu will show it.
        var mouse = _menu.PointToClient(Cursor.Position);
        bool hovered = new Rectangle(0, _slotY[_target], _menu.Width, _items[_target].Height).Contains(mouse);
        float highlight = hovered ? 1f : amount;

        // the picked item, lifted out of the list
        var card = RowRect(_source, _liftY);
        card.Inflate(3.5f * scale * _lift, 2f * scale * _lift);
        for (int k = 4; k >= 1; k--)
        {
            var shadow = card;
            shadow.Offset(0, 2.5f * scale * amount);
            shadow.Inflate(k * 0.8f * scale, k * 1.1f * scale);
            using var path = Theme.Rounded(shadow, DarkMenuRenderer.HighlightRadius + k);
            using var brush = new SolidBrush(Color.FromArgb((int)(26 * amount), 0, 0, 0));
            g.FillPath(brush, path);
        }
        using (var path = Theme.Rounded(card, DarkMenuRenderer.HighlightRadius))
        {
            using (var brush = new SolidBrush(Blend(Theme.MenuBg, Theme.FieldHover, highlight))) g.FillPath(brush, path);
            using var pen = new Pen(Color.FromArgb((int)(255 * amount), Theme.AccentBorder), Math.Max(1f, scale));
            g.DrawPath(pen, path);
        }
        DrawRow(g, _rows[_source], _liftY, highlight);

        g.Restore(state);
    }

    /// <summary>An item's highlight area with its top at <paramref name="y"/>, in menu coordinates.</summary>
    private RectangleF RowRect(int index, float y)
    {
        var item = _items[index];
        var bounds = DarkMenuRenderer.HighlightBounds(item, _menu);
        return new RectangleF(item.Bounds.X + bounds.X, y + bounds.Y, bounds.Width, bounds.Height);
    }

    private static Row Snapshot(ToolStripMenuItem item)
    {
        var layout = DarkMenuRenderer.LayoutOf(item);
        var text = layout?.Text ?? new Rectangle(item.ContentRectangle.X, 0, item.ContentRectangle.Width, item.Height);
        text.Offset(item.Bounds.X, 0);
        var check = layout?.Check ?? Rectangle.Empty;
        if (!check.IsEmpty) check.Offset(item.Bounds.X, 0);
        var format = (layout?.Format ?? TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine) |
                     TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform;
        return new Row(item.Text ?? "", item.Checked, item.Font, text, format, check);
    }

    /// <summary>Draws a row's check mark and text as the menu does, with its top at <paramref name="y"/>.</summary>
    private static void DrawRow(Graphics g, Row row, float y, float highlight)
    {
        int top = (int)Math.Round(y);
        if (row.Checked && !row.CheckBounds.IsEmpty)
        {
            var check = row.CheckBounds;
            check.Offset(0, top);
            DarkMenuRenderer.DrawCheck(g, check);
        }

        var text = row.TextBounds;
        text.Offset(0, top);
        var color = highlight >= 1f ? Theme.Text : highlight <= 0f ? Theme.TextMuted : Blend(Theme.TextMuted, Theme.Text, highlight);
        TextRenderer.DrawText(g, row.Text, row.Font, text, color, row.Format);
    }

    private static float Approach(float value, float target, float dt, float timeConstant) =>
        target + (value - target) * MathF.Exp(-dt / timeConstant);

    // overshoots a little before settling, for the "pop"
    private static float EaseOutBack(float t)
    {
        const float c1 = 1.70158f, c3 = c1 + 1f;
        float u = t - 1f;
        return 1f + c3 * u * u * u + c1 * u * u;
    }

    private static float SmoothStep(float t) => t * t * (3f - 2f * t);

    private static Color Blend(Color from, Color to, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        int Mix(int a, int b) => (int)Math.Round(a + (b - a) * amount);
        return Color.FromArgb(Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B));
    }
}
