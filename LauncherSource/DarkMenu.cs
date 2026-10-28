using System.Diagnostics;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Rendering;

namespace RpgLauncher;

/// <summary>Builds and shows the launcher's menus.</summary>
internal static class Menus
{
    /// <summary>A menu. Only menus with checkmarks reserve the check column, so plain menus aren't indented.</summary>
    public static DarkMenu Create(bool checks = false) => new() { ShowChecks = checks };

    public static DarkMenuItem Item(string text, EventHandler? onClick = null)
    {
        var item = new DarkMenuItem(text);
        if (onClick is not null) item.Click += onClick;
        return item;
    }

    public static DarkMenuItem Separator() => new("") { IsSeparator = true };

    /// <summary>Sets whether a submenu reserves the check column.</summary>
    public static void StyleSubmenu(DarkMenuItem item, DarkMenu owner, bool checks) => item.ShowChecks = checks;

    /// <summary>Shows a menu at a point in <paramref name="owner"/>'s coordinates.</summary>
    public static void Show(DarkMenu menu, Control owner, Point position, MenuDirection direction = MenuDirection.BelowRight) =>
        menu.Show(owner, position, direction);
}

/// <summary>One menu entry. Text uses WinForms mnemonics: "&amp;&amp;" shows one ampersand and a lone "&amp;" is hidden.</summary>
internal sealed class DarkMenuItem
{
    public DarkMenuItem(string text)
    {
        Text = text;
    }

    public string Text { get; set; }
    public bool Checked { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Bold text, for the main action.</summary>
    public bool Bold { get; set; }

    public bool IsSeparator { get; init; }
    public object? Tag { get; set; }

    /// <summary>Whether this item's submenu reserves the check column.</summary>
    public bool ShowChecks { get; set; }

    public List<DarkMenuItem> DropDownItems { get; } = new();
    public bool HasDropDownItems => DropDownItems.Count > 0;

    public event EventHandler? Click;

    public void PerformClick() => Click?.Invoke(this, EventArgs.Empty);

    /// <summary>The text as shown, without mnemonic markers.</summary>
    internal string DisplayText
    {
        get
        {
            if (!Text.Contains('&')) return Text;
            var shown = new System.Text.StringBuilder(Text.Length);
            for (int i = 0; i < Text.Length; i++)
            {
                if (Text[i] != '&') shown.Append(Text[i]);
                else if (i + 1 < Text.Length && Text[i + 1] == '&') shown.Append(Text[++i]);
            }
            return shown.ToString();
        }
    }
}

/// <summary>Which way a menu opens from its point.</summary>
internal enum MenuDirection
{
    BelowRight,
    AboveRight,
    BelowLeft,
    AboveLeft
}

/// <summary>A popup menu drawn in the launcher's style, with submenus, checkmarks and drag-to-reorder.</summary>
internal sealed class DarkMenu
{
    private MenuView? _root;
    private TopLevel? _topLevel;

    public List<DarkMenuItem> Items { get; } = new();
    public bool ShowChecks { get; set; }

    /// <summary>The narrowest the menu may be, e.g. the width of the box it drops from.</summary>
    public double MinWidth { get; set; }

    public bool IsOpen => _root is not null;

    public event EventHandler? Closed;

    internal (List<DarkMenuItem> Items, Action Reordered)? Reorder { get; private set; }

    /// <summary>
    /// Lets the user drag these items (contiguous rows of this menu) into a new order. On drop their texts, tags
    /// and checks swap places, so each item keeps its row; <paramref name="reordered"/> then reads the new order.
    /// </summary>
    public void EnableReorder(List<DarkMenuItem> items, Action reordered) => Reorder = (items, reordered);

    /// <summary>The panel the menu's popups are attached to, so they belong to the window's control tree.</summary>
    internal Panel? Host { get; private set; }

    public void Show(Control owner, Point position, MenuDirection direction = MenuDirection.BelowRight)
    {
        Close();
        _topLevel = TopLevel.GetTopLevel(owner);
        Host = owner as Panel ?? Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<Panel>(owner) ?? _topLevel?.Content as Panel;
        if (_topLevel is null || Host is null) return;

        _root = new MenuView(this, null, Items, ShowChecks);
        var (anchor, gravity) = direction switch
        {
            MenuDirection.AboveRight => (PopupAnchor.TopLeft, PopupGravity.TopRight),
            MenuDirection.BelowLeft => (PopupAnchor.TopLeft, PopupGravity.BottomLeft),
            MenuDirection.AboveLeft => (PopupAnchor.TopLeft, PopupGravity.TopLeft),
            _ => (PopupAnchor.TopLeft, PopupGravity.BottomRight)
        };
        // Popups only anchor inside their target, and the point may lie outside the owner (below a button, say):
        // anchor on the host panel instead, at the same spot.
        var at = owner.TranslatePoint(position, Host) ?? position;
        at = new Point(Math.Clamp(at.X, 0, Math.Max(0, Host.Bounds.Width - 1)), Math.Clamp(at.Y, 0, Math.Max(0, Host.Bounds.Height - 1)));
        _root.Open(Host, new Rect(at, new Size(1, 1)), anchor, gravity, lightDismiss: true);

        _topLevel.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        if (_topLevel is WindowBase window) window.Deactivated += OnDeactivated;
    }

    public void Close()
    {
        if (_root is null) return;
        var root = _root;
        _root = null;
        if (_topLevel is not null)
        {
            _topLevel.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            if (_topLevel is WindowBase window) window.Deactivated -= OnDeactivated;
            _topLevel = null;
        }
        root.CloseTree();
        Host = null;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void OnDeactivated(object? sender, EventArgs e) => Close();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_root is null) return;
        var view = _root;
        while (view.Child is { } child) view = child;
        e.Handled = view.HandleKey(e.Key);
    }

    /// <summary>Called by a view when the user picked an item: closes everything, then runs the item.</summary>
    internal void Pick(DarkMenuItem item)
    {
        Close();
        item.PerformClick();
    }
}

/// <summary>The drawn list of one menu level, hosted in a popup.</summary>
internal sealed class MenuView : Control, ICustomHitTest
{
    private const double Pad = 4;
    private const double RowHeight = 28;
    private const double SeparatorHeight = 9;
    private const double HighlightGap = 5;
    private const double HighlightRadius = 5;
    private const double SubmenuDelayMs = 220;

    private static readonly UiFont MenuFont = Look.Font(9.5);
    private static readonly UiFont BoldFont = Look.Font(9.5, bold: true);

    private readonly DarkMenu _menu;
    private readonly MenuView? _parent;
    private readonly List<DarkMenuItem> _items;
    private readonly bool _checks;
    private readonly double[] _tops;
    private readonly double _width, _height;
    private readonly Popup _popup = new();
    private readonly DispatcherTimer _submenuTimer = new() { Interval = TimeSpan.FromMilliseconds(SubmenuDelayMs) };

    private int _hover = -1;
    private int _pressed = -1;
    private int _childIndex = -1;
    private bool _closing;

    public MenuView? Child { get; private set; }

    public MenuView(DarkMenu menu, MenuView? parent, List<DarkMenuItem> items, bool checks)
    {
        _menu = menu;
        _parent = parent;
        _items = items;
        _checks = checks;
        Focusable = false;
        Cursor = Cursor.Default;

        _tops = new double[items.Count];
        double y = Pad, textWidth = 0;
        bool arrows = false;
        foreach (var (item, index) in items.Select((item, index) => (item, index)))
        {
            _tops[index] = y;
            y += item.IsSeparator ? SeparatorHeight : RowHeight;
            if (item.IsSeparator) continue;
            textWidth = Math.Max(textWidth, Look.Measure(item.DisplayText, item.Bold ? BoldFont : MenuFont).Width);
            arrows |= item.HasDropDownItems;
        }
        _height = y + Pad;
        _width = Math.Ceiling(Math.Max(Math.Max(120, parent is null ? menu.MinWidth : 0),
            TextLeft + textWidth + (arrows ? 34 : 22) + Pad));
        Width = _width;
        Height = _height;

        _submenuTimer.Tick += (_, _) =>
        {
            _submenuTimer.Stop();
            if (_hover >= 0 && _items[_hover].HasDropDownItems && _items[_hover].Enabled) OpenChild(_hover);
            else CloseChild();
        };

        if (parent is null && menu.Reorder is { } reorder)
            _drag = new DragState(this, reorder.Items, reorder.Reordered);
    }

    private double TextLeft => Pad + (_checks ? 30 : 14);

    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    // ---- popup ----

    public void Open(Control target, Rect anchorRect, PopupAnchor anchor, PopupGravity gravity, bool lightDismiss)
    {
        _popup.Child = this;
        _popup.PlacementTarget = target;
        _popup.Placement = PlacementMode.AnchorAndGravity;
        _popup.PlacementRect = anchorRect;
        _popup.PlacementAnchor = anchor;
        _popup.PlacementGravity = gravity;
        _popup.PlacementConstraintAdjustment = PopupPositionerConstraintAdjustment.FlipX |
            PopupPositionerConstraintAdjustment.FlipY | PopupPositionerConstraintAdjustment.SlideX |
            PopupPositionerConstraintAdjustment.SlideY | PopupPositionerConstraintAdjustment.ResizeY;
        _popup.IsLightDismissEnabled = lightDismiss;
        _popup.WindowManagerAddShadowHint = true;
        _popup.Closed += (_, _) =>
        {
            // Light dismiss (a click elsewhere) closes the root popup; close the whole menu with it.
            if (!_closing && _parent is null) _menu.Close();
        };
        // A popup shows its content only once it is part of a window's control tree: it joins the host panel
        // (where it takes no space) for as long as it is open.
        _menu.Host?.Children.Add(_popup);
        _popup.Open();
    }

    /// <summary>Closes this level and every submenu below it.</summary>
    public void CloseTree()
    {
        CloseChild();
        _closing = true;
        _submenuTimer.Stop();
        _drag?.Stop(redraw: false);
        _popup.Close();
        _popup.Child = null;
        (_popup.Parent as Panel)?.Children.Remove(_popup);
    }

    private void OpenChild(int index)
    {
        if (_childIndex == index && Child is not null) return;
        CloseChild();
        var item = _items[index];
        var child = new MenuView(_menu, this, item.DropDownItems, item.ShowChecks);
        Child = child;
        _childIndex = index;
        var row = new Rect(0, _tops[index] - Pad, _width - 2, RowHeight + Pad * 2);
        child.Open(this, row, PopupAnchor.TopRight, PopupGravity.BottomRight, lightDismiss: false);
        InvalidateVisual();
    }

    private void CloseChild()
    {
        if (Child is null) return;
        Child.CloseTree();
        Child = null;
        _childIndex = -1;
        InvalidateVisual();
    }

    // ---- input ----

    private int RowAt(Point point)
    {
        if (point.X < 0 || point.X > _width) return -1;
        for (int i = 0; i < _items.Count; i++)
        {
            double height = _items[i].IsSeparator ? SeparatorHeight : RowHeight;
            if (point.Y >= _tops[i] && point.Y < _tops[i] + height) return _items[i].IsSeparator ? -1 : i;
        }
        return -1;
    }

    private void SetHover(int index)
    {
        if (index == _hover) return;
        _hover = index;
        InvalidateVisual();
        _submenuTimer.Stop();
        if (index >= 0 && _items[index].HasDropDownItems && _items[index].Enabled)
        {
            if (_childIndex != index) _submenuTimer.Start();
        }
        else if (Child is not null)
        {
            _submenuTimer.Start();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        if (_drag is not null && _drag.PointerMoved(point)) return;
        SetHover(RowAt(point));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        // Keep the row of an open submenu lit while the pointer is over the submenu.
        if (_drag is { Active: true }) return;
        if (Child is not null) _hover = _childIndex;
        else SetHover(-1);
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        e.Handled = true;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var point = e.GetPosition(this);
        int row = RowAt(point);
        _pressed = row;
        if (row >= 0 && _drag is not null && _drag.Press(_items[row], point)) e.Pointer.Capture(this);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Handled = true;
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        var point = e.GetPosition(this);
        if (_drag is not null && _drag.Release())
        {
            e.Pointer.Capture(null);
            _pressed = -1;
            return;
        }
        e.Pointer.Capture(null);
        int row = RowAt(point);
        if (row >= 0 && row == _pressed) Activate(row);
        _pressed = -1;
    }

    private void Activate(int index)
    {
        var item = _items[index];
        if (!item.Enabled || item.IsSeparator) return;
        if (item.HasDropDownItems)
        {
            _submenuTimer.Stop();
            OpenChild(index);
            return;
        }
        _menu.Pick(item);
    }

    /// <summary>Keyboard navigation; true when the key was used.</summary>
    public bool HandleKey(Key key)
    {
        switch (key)
        {
            case Key.Down:
            case Key.Up:
            {
                int step = key == Key.Down ? 1 : -1;
                int index = _hover >= 0 ? _hover : step > 0 ? -1 : _items.Count;
                for (int n = 0; n < _items.Count; n++)
                {
                    index = ((index + step) % _items.Count + _items.Count) % _items.Count;
                    if (!_items[index].IsSeparator && _items[index].Enabled) break;
                }
                _hover = index;
                _submenuTimer.Stop();
                InvalidateVisual();
                return true;
            }
            case Key.Right:
                if (_hover >= 0 && _items[_hover].HasDropDownItems && _items[_hover].Enabled)
                {
                    OpenChild(_hover);
                    Child?.HandleKey(Key.Down);
                }
                return true;
            case Key.Left:
                if (_parent is not null) _parent.CloseChild();
                return true;
            case Key.Enter:
            case Key.Space:
                if (_hover >= 0) Activate(_hover);
                return true;
            case Key.Escape:
                if (_parent is not null) _parent.CloseChild();
                else _menu.Close();
                return true;
            default:
                return false;
        }
    }

    // ---- drawing ----

    public override void Render(DrawingContext g)
    {
        var bounds = new Rect(0, 0, _width, _height);
        g.FillRectangle(Look.Brush(Look.MenuBg), bounds);

        if (_drag is { Active: true })
        {
            // Hover highlights are hidden while a row is dragged.
            for (int i = 0; i < _items.Count; i++)
            {
                if (!_drag.Covers(_items[i])) DrawRow(g, i, _tops[i], 0);
            }
            _drag.Render(g);
        }
        else
        {
            for (int i = 0; i < _items.Count; i++)
            {
                bool lit = (i == _hover || i == _childIndex) && _items[i].Enabled && !_items[i].IsSeparator;
                DrawRow(g, i, _tops[i], lit ? 1 : 0);
            }
        }

        g.DrawRectangle(Look.Pen(Look.FieldBorder, 1), bounds.Deflate(0.5));
    }

    /// <summary>The highlight of a row whose top is at <paramref name="top"/>.</summary>
    internal Rect HighlightRect(double top) =>
        new(HighlightGap, top + 1, Math.Max(1, _width - HighlightGap * 2), RowHeight - 2);

    internal void DrawRow(DrawingContext g, int index, double top, double highlight)
    {
        var item = _items[index];
        if (item.IsSeparator)
        {
            double y = Math.Round(top + SeparatorHeight / 2) + 0.5;
            g.DrawLine(Look.Pen(Look.Divider, 1), new Point(12, y), new Point(_width - 12, y));
            return;
        }
        DrawItem(g, item, top, highlight);
    }

    /// <summary>Draws an item's highlight, check, text and arrow with its row's top at <paramref name="top"/>.</summary>
    internal void DrawItem(DrawingContext g, DarkMenuItem item, double top, double highlight, bool fill = true)
    {
        if (fill && highlight > 0 && item.Enabled)
        {
            var back = highlight >= 1 ? Look.FieldHover : Look.Blend(Look.MenuBg, Look.FieldHover, highlight);
            g.DrawRectangle(Look.Brush(back), null, Look.Rounded(HighlightRect(top), HighlightRadius));
        }

        if (item.Checked && _checks)
            DrawCheck(g, new Rect(Pad + 8, top + (RowHeight - 16) / 2, 16, 16));

        var color = !item.Enabled ? Look.TextDim
            : highlight >= 1 ? Look.Text
            : highlight <= 0 ? Look.TextMuted
            : Look.Blend(Look.TextMuted, Look.Text, highlight);
        double right = item.HasDropDownItems ? 30 : 14;
        Look.DrawText(g, item.DisplayText, item.Bold ? BoldFont : MenuFont, color,
            new Rect(TextLeft, top, Math.Max(1, _width - TextLeft - right - Pad), RowHeight));

        if (item.HasDropDownItems)
        {
            double cx = _width - Pad - 14, cy = top + RowHeight / 2;
            g.DrawGeometry(null, Look.Pen(highlight >= 1 && item.Enabled ? Look.Text : Look.TextMuted, 1.5, round: true),
                Look.Lines(false, new Point(cx - 2.2, cy - 4), new Point(cx + 2.2, cy), new Point(cx - 2.2, cy + 4)));
        }
    }

    public static void DrawCheck(DrawingContext g, Rect r)
    {
        double x = r.Left + 2, y = r.Top + r.Height / 2;
        g.DrawGeometry(null, Look.Pen(Look.Gold, 1.9, round: true), Look.Lines(false,
            new Point(x, y),
            new Point(x + r.Width * 0.32, y + r.Height * 0.30),
            new Point(x + r.Width * 0.80, y - r.Height * 0.30)));
    }

    // ---- drag to reorder ----

    private readonly DragState? _drag;

    /// <summary>Drag-to-reorder: the picked row lifts and follows the pointer while the others slide aside.</summary>
    private sealed class DragState
    {
        private const double PopSeconds = 0.18;
        private const double SettleSeconds = 0.15;
        // time constant of the rows' easing toward their places
        private const double SlideSeconds = 0.045;
        private const double DragThreshold = 4;

        private enum Phase { Idle, Pressed, Dragging, Settling }

        private readonly MenuView _view;
        private readonly List<DarkMenuItem> _items;
        private readonly Action _reordered;
        private bool _animating;
        private readonly Stopwatch _clock = new();

        private Phase _phase;
        private bool _held;
        private int _source, _target;
        private Point _pressedAt, _pointer;
        private double _grab, _liftY, _pop, _lift, _liftAtRelease, _lastTick;
        private double[] _rowY = Array.Empty<double>();
        private double[] _slotY = Array.Empty<double>();
        private (string Text, object? Tag, bool Checked, bool Bold)[] _rows = Array.Empty<(string, object?, bool, bool)>();

        public DragState(MenuView view, List<DarkMenuItem> items, Action reordered)
        {
            _view = view;
            _items = items;
            _reordered = reordered;
        }

        public bool Active => _phase >= Phase.Dragging;

        public bool Covers(DarkMenuItem item) => _items.Contains(item);

        private double SlotTop(int index) => _view._tops[_view._items.IndexOf(_items[index])];

        /// <summary>A press on a row; true when it may become a drag.</summary>
        public bool Press(DarkMenuItem item, Point point)
        {
            if (_phase >= Phase.Dragging) Finish();
            _source = _items.IndexOf(item);
            if (_source < 0) return false;
            _held = true;
            _pressedAt = _pointer = point;
            _grab = point.Y - SlotTop(_source);
            _phase = Phase.Pressed;
            _clock.Restart();
            _lastTick = 0;
            return true;
        }

        /// <summary>Pointer moved; true when a drag is using it.</summary>
        public bool PointerMoved(Point point)
        {
            _pointer = point;
            if (_phase == Phase.Pressed && _held && Math.Abs(point.Y - _pressedAt.Y) >= DragThreshold) Begin();
            return _phase >= Phase.Dragging;
        }

        /// <summary>The button came up; true when it ended a drag, so it isn't a click.</summary>
        public bool Release()
        {
            _held = false;
            if (_phase == Phase.Dragging)
            {
                _phase = Phase.Settling;
                _liftAtRelease = _lift;
                _pop = 1;
                _view.Cursor = Cursor.Default;
                return true;
            }
            if (_phase == Phase.Pressed) Stop(redraw: false);
            return _phase == Phase.Settling;
        }

        private void Begin()
        {
            _phase = Phase.Dragging;
            _rows = _items.Select(i => (i.Text, i.Tag, i.Checked, i.Bold)).ToArray();
            _slotY = Enumerable.Range(0, _items.Count).Select(SlotTop).ToArray();
            _rowY = _slotY.ToArray();
            _target = _source;
            _liftY = _slotY[_source];
            _pop = 0;
            _lift = 0;
            _view.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
            _view._submenuTimer.Stop();
            _view.CloseChild();
            Animate();
        }

        /// <summary>Ticks once per frame until the drag is over.</summary>
        private void Animate()
        {
            if (_animating) return;
            _animating = true;
            RequestFrame();
        }

        private void RequestFrame() => TopLevel.GetTopLevel(_view)?.RequestAnimationFrame(_ =>
        {
            if (!_animating) return;
            Tick();
            if (_animating) RequestFrame();
        });

        private void Tick()
        {
            double now = _clock.Elapsed.TotalSeconds;
            double dt = Math.Min(0.05, now - _lastTick);
            _lastTick = now;

            if (_phase == Phase.Dragging)
            {
                double y = _pointer.Y - _grab;
                _liftY = Math.Clamp(y, _slotY[0] - RowHeight * 0.4, _slotY[^1] + RowHeight * 0.4);
                _target = NearestSlot(_liftY);
                _pop = Math.Min(1, _pop + dt / PopSeconds);
                _lift = EaseOutBack(_pop);
            }
            else if (_phase == Phase.Settling)
            {
                _pop = Math.Max(0, _pop - dt / SettleSeconds);
                _lift = _liftAtRelease * SmoothStep(_pop);
                _liftY = Approach(_liftY, _slotY[_target], dt, SlideSeconds * 0.8);
            }

            // The other rows close the gap the picked item left and open one where it will land.
            bool resting = true;
            int slot = 0;
            for (int i = 0; i < _items.Count; i++)
            {
                if (i == _source) continue;
                if (slot == _target) slot++;
                _rowY[i] = Approach(_rowY[i], _slotY[slot], dt, SlideSeconds);
                resting &= Math.Abs(_rowY[i] - _slotY[slot]) < 0.5;
                slot++;
            }

            _view.InvalidateVisual();
            if (_phase == Phase.Settling && _pop <= 0 && resting && Math.Abs(_liftY - _slotY[_target]) < 0.5)
                Finish();
        }

        private int NearestSlot(double y)
        {
            int best = 0;
            for (int i = 1; i < _slotY.Length; i++)
            {
                if (Math.Abs(y - _slotY[i]) < Math.Abs(y - _slotY[best])) best = i;
            }
            return best;
        }

        /// <summary>Puts the item where it was dropped, then hands drawing back to the menu.</summary>
        private void Finish()
        {
            bool changed = _phase >= Phase.Dragging && _target != _source;
            if (changed)
            {
                var order = Enumerable.Range(0, _items.Count).ToList();
                order.RemoveAt(_source);
                order.Insert(_target, _source);
                var contents = order.Select(i => _rows[i]).ToList();
                for (int i = 0; i < _items.Count; i++)
                {
                    _items[i].Text = contents[i].Text;
                    _items[i].Tag = contents[i].Tag;
                    _items[i].Checked = contents[i].Checked;
                    _items[i].Bold = contents[i].Bold;
                }
            }
            Stop(redraw: true);
            // The rows changed under a still pointer; light the one it is over now.
            _view._hover = _view.RowAt(_pointer);
            if (changed) _reordered();
        }

        public void Stop(bool redraw)
        {
            _phase = Phase.Idle;
            _held = false;
            _animating = false;
            _view.Cursor = Cursor.Default;
            if (redraw) _view.InvalidateVisual();
        }

        public void Render(DrawingContext g)
        {
            if (_slotY.Length == 0) return;
            double amount = Math.Clamp(_lift, 0, 1);

            // a faint slot where the item will land
            g.DrawRectangle(Look.Brush(Look.Blend(Look.MenuBg, Look.FieldHover, 0.6 * amount)), null,
                Look.Rounded(_view.HighlightRect(_slotY[_target]), HighlightRadius));

            for (int i = 0; i < _items.Count; i++)
            {
                if (i != _source) DrawSnapshot(g, i, _rowY[i], 0);
            }

            // Highlighted while lifted; once down, only if hovered, matching how the menu will show it.
            bool hovered = _pointer.Y >= _slotY[_target] && _pointer.Y < _slotY[_target] + RowHeight;
            double highlight = hovered ? 1 : amount;

            // the picked item, lifted out of the list
            var card = _view.HighlightRect(_liftY).Inflate(new Thickness(3.5 * _lift, 2 * _lift));
            for (int k = 4; k >= 1; k--)
            {
                var shadow = card.Translate(new Vector(0, 2.5 * amount)).Inflate(new Thickness(k * 0.8, k * 1.1));
                g.DrawRectangle(Look.Brush(Color.FromArgb((byte)(26 * amount), 0, 0, 0)), null,
                    Look.Rounded(shadow, HighlightRadius + k));
            }
            g.DrawRectangle(Look.Brush(Look.Blend(Look.MenuBg, Look.FieldHover, highlight)),
                Look.Pen(Look.Alpha((int)(255 * amount), Look.AccentBorder), 1), Look.Rounded(card, HighlightRadius));
            DrawSnapshot(g, _source, _liftY, highlight, fill: false);
        }

        /// <summary>Draws a row as it was when the drag began; <paramref name="fill"/> off for the lifted card, which has its own.</summary>
        private void DrawSnapshot(DrawingContext g, int index, double top, double highlight, bool fill = true)
        {
            var row = _rows[index];
            var shown = new DarkMenuItem(row.Text) { Checked = row.Checked, Bold = row.Bold };
            _view.DrawItem(g, shown, Math.Round(top), highlight, fill);
        }

        private static double Approach(double value, double target, double dt, double timeConstant) =>
            target + (value - target) * Math.Exp(-dt / timeConstant);

        // overshoots a little before settling, for the "pop"
        private static double EaseOutBack(double t)
        {
            const double c1 = 1.70158, c3 = c1 + 1;
            double u = t - 1;
            return 1 + c3 * u * u * u + c1 * u * u;
        }

        private static double SmoothStep(double t) => t * t * (3 - 2 * t);
    }
}
