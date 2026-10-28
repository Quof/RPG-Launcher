using System.Globalization;
using Avalonia.Rendering;

namespace RpgLauncher;

/// <summary>One row of a <see cref="CheatList"/>.</summary>
internal sealed class CheatListRow
{
    public required int Id { get; init; }
    public required string Name { get; init; }

    /// <summary>Dimmer text after the name.</summary>
    public string Detail { get; init; } = "";

    /// <summary>Shown at the right; clicking it edits it in place when <see cref="Editable"/>.</summary>
    public string Value { get; init; } = "";

    public bool Editable { get; init; }

    /// <summary>For a switch: shown as ON or OFF, toggled by a click.</summary>
    public bool? On { get; init; }

    /// <summary>Buttons at the right, such as Run.</summary>
    public IReadOnlyList<string> Actions { get; init; } = Array.Empty<string>();
}

/// <summary>
/// A long list of game values drawn by hand, so thousands of rows stay quick: an ID, a name, then a value to edit in
/// place, an ON/OFF switch, or buttons. A typed filter narrows it by name, details or ID.
/// </summary>
internal sealed class CheatList : ManualPanel
{
    public const double RowHeight = 28;
    private const double Inset = 4;

    private readonly Surface _surface;
    private readonly TextBox _editor;
    private IReadOnlyList<CheatListRow> _rows = Array.Empty<CheatListRow>();
    private List<CheatListRow> _shown = new();
    private string _filter = "";
    private double _scroll;
    private CheatListRow? _editing;

    /// <summary>A value was edited (Enter or leaving the box), with the text typed.</summary>
    public event Action<CheatListRow, string>? ValueEdited;

    public event Action<CheatListRow>? Toggled;

    /// <summary>A button was clicked; the rectangle is the button's, in this list's coordinates (to open a menu there).</summary>
    public event Action<CheatListRow, string, Rect>? ActionClicked;

    /// <summary>The tooltip for a row's button, shown while the pointer is over it.</summary>
    public Func<CheatListRow, string, string?>? ActionTip { get; set; }

    public CheatList()
    {
        _surface = new Surface(this);
        _editor = Ui.TextBox(Look.Font(9.5));
        _editor.TextAlignment = TextAlignment.Right;
        _editor.IsVisible = false;
        _editor.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Commit();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CancelEdit();
            }
        }, RoutingStrategies.Tunnel);
        _editor.LostFocus += (_, _) => Commit();
        Children.Add(_surface);
        Children.Add(_editor);
        ClipToBounds = true;
        Layout = size =>
        {
            Place(_surface, 0, 0, size.Width, size.Height);
            if (_editing is not null && ValueRect(_editing, size) is { } cell) Place(_editor, cell);
        };
    }

    /// <summary>True while a value is being edited, so refreshes can wait.</summary>
    public bool IsEditing => _editing is not null;

    public int Count => _rows.Count;
    public int ShownCount => _shown.Count;

    /// <summary>Shown when there are no rows to show.</summary>
    public string EmptyText { get; set; } = "Nothing to show";

    public string Filter
    {
        get => _filter;
        set
        {
            _filter = value ?? "";
            Refilter(resetScroll: true);
        }
    }

    /// <summary>
    /// Shows new rows of the same list, keeping the scroll position and a value being edited; <paramref name="fromTop"/>
    /// starts a different list at its top.
    /// </summary>
    public void SetRows(IReadOnlyList<CheatListRow> rows, bool fromTop = false)
    {
        if (fromTop) CancelEdit();
        _rows = rows;
        Refilter(resetScroll: fromTop);
    }

    private void Refilter(bool resetScroll)
    {
        string filter = _filter.Trim();
        _shown = filter.Length == 0
            ? _rows.ToList()
            : _rows.Where(row => row.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
                                 row.Detail.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
                                 row.Id.ToString(CultureInfo.InvariantCulture) == filter).ToList();
        if (_editing is not null)
        {
            var same = _shown.FirstOrDefault(row => row.Id == _editing.Id);
            if (same is null) CancelEdit();
            else _editing = same;
        }
        if (resetScroll) _scroll = 0;
        ClampScroll();
        _surface.InvalidateVisual();
        InvalidateArrange();
    }

    private double ContentHeight => _shown.Count * RowHeight + Inset * 2;

    private double MaxScroll => Math.Max(0, ContentHeight - Bounds.Height);

    private void ClampScroll() => _scroll = Math.Clamp(_scroll, 0, MaxScroll);

    private void ScrollBy(double delta)
    {
        Commit();
        _scroll += delta;
        ClampScroll();
        _surface.InvalidateVisual();
    }

    // ---- geometry ----

    private double RowTop(int index) => Inset + index * RowHeight - _scroll;

    private static double RightEdge(Size size) => size.Width - 16;

    /// <summary>The value box of the row whose top is at <paramref name="top"/>.</summary>
    private static Rect ValueCell(double top, Size size)
    {
        double width = Math.Min(150, Math.Max(80, size.Width * 0.3));
        return new Rect(RightEdge(size) - width, top + 3, width, RowHeight - 6);
    }

    /// <summary>The value box of a row being edited, or null when it is out of view.</summary>
    private Rect? ValueRect(CheatListRow row, Size size)
    {
        int index = _shown.IndexOf(row);
        if (index < 0) return null;
        double top = RowTop(index);
        return top + RowHeight < 0 || top > size.Height ? null : ValueCell(top, size);
    }

    private static Rect PillRect(double top, Size size) => new(RightEdge(size) - 56, top + 4, 56, RowHeight - 8);

    /// <summary>The buttons of a row, right-aligned.</summary>
    private static List<(string Action, Rect Rect)> ButtonRects(CheatListRow row, double top, Size size)
    {
        var buttons = new List<(string, Rect)>();
        double right = RightEdge(size);
        for (int i = row.Actions.Count - 1; i >= 0; i--)
        {
            double width = Look.Measure(row.Actions[i], ButtonFont).Width + 20;
            buttons.Insert(0, (row.Actions[i], new Rect(right - width, top + 4, width, RowHeight - 8)));
            right -= width + 6;
        }
        return buttons;
    }

    private static readonly UiFont IdFont = Look.Font(8.5);
    private static readonly UiFont NameFont = Look.Font(9.5);
    private static readonly UiFont DetailFont = Look.Font(8.5);
    private static readonly UiFont ButtonFont = Look.Font(8.5, bold: true);

    private void BeginEdit(CheatListRow row)
    {
        Commit();
        _editing = row;
        _editor.Text = row.Value;
        _editor.IsVisible = true;
        InvalidateArrange();
        Dispatcher.UIThread.Post(() =>
        {
            if (_editing != row) return;
            _editor.Focus();
            _editor.SelectAll();
        });
    }

    private void Commit()
    {
        if (_editing is not { } row) return;
        string text = (_editor.Text ?? "").Trim();
        CloseEditor();
        if (text != row.Value) ValueEdited?.Invoke(row, text);
    }

    private void CancelEdit()
    {
        if (_editing is null) return;
        CloseEditor();
    }

    private void CloseEditor()
    {
        _editing = null;
        _editor.IsVisible = false;
        _surface.InvalidateVisual();
    }

    /// <summary>Draws the rows and takes the pointer.</summary>
    private sealed class Surface : Control, ICustomHitTest
    {
        private enum Part { None, Value, Pill, Button }

        private readonly CheatList _list;
        private int _hoverRow = -1;
        private Part _hoverPart;
        private int _hoverButton = -1;
        private (int Row, Part Part, int Button) _pressed = (-1, Part.None, -1);
        private bool _dragging;
        private double _dragStartY, _dragStartScroll;

        public Surface(CheatList list)
        {
            _list = list;
            Focusable = false;
        }

        public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

        private bool HasScrollbar => _list.MaxScroll > 0;

        private Rect Thumb()
        {
            double track = Bounds.Height - 8;
            double length = Math.Max(30, track * Bounds.Height / Math.Max(1, _list.ContentHeight));
            double travel = Math.Max(1, track - length);
            double top = 4 + travel * (_list.MaxScroll <= 0 ? 0 : _list._scroll / _list.MaxScroll);
            return new Rect(Bounds.Width - 9, top, 5, length);
        }

        private (int Row, Part Part, int Button) HitRow(Point point)
        {
            if (HasScrollbar && point.X >= Bounds.Width - 14) return (-1, Part.None, -1);
            int index = (int)Math.Floor((point.Y + _list._scroll - Inset) / RowHeight);
            if (index < 0 || index >= _list._shown.Count) return (-1, Part.None, -1);
            var row = _list._shown[index];
            double top = _list.RowTop(index);
            if (row.Actions.Count > 0)
            {
                var buttons = ButtonRects(row, top, Bounds.Size);
                for (int i = 0; i < buttons.Count; i++)
                    if (buttons[i].Rect.Contains(point)) return (index, Part.Button, i);
            }
            else if (row.On is not null)
            {
                if (PillRect(top, Bounds.Size).Contains(point)) return (index, Part.Pill, -1);
            }
            else if (row.Editable && ValueCell(top, Bounds.Size).Contains(point))
            {
                return (index, Part.Value, -1);
            }
            return (index, Part.None, -1);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var point = e.GetPosition(this);
            if (_dragging)
            {
                var thumb = Thumb();
                double travel = Math.Max(1, Bounds.Height - 8 - thumb.Height);
                _list._scroll = _dragStartScroll + (point.Y - _dragStartY) / travel * _list.MaxScroll;
                _list.ClampScroll();
                InvalidateVisual();
                return;
            }
            var (row, part, button) = HitRow(point);
            if (row != _hoverRow || part != _hoverPart || button != _hoverButton)
            {
                _hoverRow = row;
                _hoverPart = part;
                _hoverButton = button;
                InvalidateVisual();
                var shown = part == Part.Button ? _list._shown[row] : null;
                Ui.Tip(this, shown is not null ? _list.ActionTip?.Invoke(shown, shown.Actions[button]) : null);
            }
            Cursor = part != Part.None || (HasScrollbar && point.X >= Bounds.Width - 14)
                ? new Cursor(StandardCursorType.Hand)
                : Cursor.Default;
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            _hoverRow = -1;
            _hoverPart = Part.None;
            _hoverButton = -1;
            InvalidateVisual();
            Ui.Tip(this, null);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            var point = e.GetPosition(this);
            if (HasScrollbar && point.X >= Bounds.Width - 14)
            {
                _list.Commit();
                var thumb = Thumb();
                if (!thumb.Contains(point))
                {
                    // A click on the track jumps there.
                    double travel = Math.Max(1, Bounds.Height - 8 - thumb.Height);
                    _list._scroll = (point.Y - 4 - thumb.Height / 2) / travel * _list.MaxScroll;
                    _list.ClampScroll();
                }
                _dragging = true;
                _dragStartY = point.Y;
                _dragStartScroll = _list._scroll;
                e.Pointer.Capture(this);
                InvalidateVisual();
                e.Handled = true;
                return;
            }
            _pressed = HitRow(point);
            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            if (_dragging)
            {
                _dragging = false;
                e.Pointer.Capture(null);
                InvalidateVisual();
                return;
            }
            var hit = HitRow(e.GetPosition(this));
            var pressed = _pressed;
            _pressed = (-1, Part.None, -1);
            if (hit != pressed || hit.Row < 0) return;
            var row = _list._shown[hit.Row];
            switch (hit.Part)
            {
                case Part.Value:
                    _list.BeginEdit(row);
                    break;
                case Part.Pill:
                    _list.Toggled?.Invoke(row);
                    break;
                case Part.Button:
                    var rect = ButtonRects(row, _list.RowTop(hit.Row), Bounds.Size)[hit.Button].Rect;
                    _list.ActionClicked?.Invoke(row, row.Actions[hit.Button], rect);
                    break;
            }
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            _dragging = false;
            InvalidateVisual();
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            _list.ScrollBy(-e.Delta.Y * RowHeight * 3);
            e.Handled = true;
        }

        public override void Render(DrawingContext g)
        {
            var size = Bounds.Size;
            var frame = new Rect(size).Deflate(0.5);
            g.DrawRectangle(Look.Brush(Look.Card), Look.Pen(Look.CardBorder, 1), Look.Rounded(frame, Look.ControlRadius));

            var shown = _list._shown;
            if (shown.Count == 0)
            {
                Look.DrawText(g, _list.EmptyText, NameFont, Look.TextDim, new Rect(16, 0, Math.Max(1, size.Width - 32), size.Height),
                    TextAlignment.Center);
                return;
            }

            using (g.PushClip(new Rect(1, 1, Math.Max(0, size.Width - 2), Math.Max(0, size.Height - 2))))
            {
                int first = Math.Max(0, (int)Math.Floor((_list._scroll - Inset) / RowHeight));
                int last = Math.Min(shown.Count - 1, (int)Math.Ceiling((_list._scroll + size.Height) / RowHeight));
                for (int i = first; i <= last; i++) DrawRow(g, shown[i], i, size);
            }

            if (HasScrollbar)
            {
                var thumb = Thumb();
                g.DrawRectangle(Look.Brush(Look.ScrollTrack), null,
                    Look.Rounded(new Rect(thumb.X, 4, thumb.Width, Math.Max(1, size.Height - 8)), thumb.Width / 2));
                g.DrawRectangle(Look.Brush(_dragging ? Look.ScrollThumbActive : Look.ScrollThumb), null,
                    Look.Rounded(thumb, thumb.Width / 2));
            }
        }

        private void DrawRow(DrawingContext g, CheatListRow row, int index, Size size)
        {
            double top = _list.RowTop(index);
            bool hover = index == _hoverRow;
            if (hover)
            {
                g.DrawRectangle(Look.Brush(Look.CardHover), null,
                    Look.Rounded(new Rect(4, top + 1, Math.Max(1, size.Width - 20), RowHeight - 2), 4));
            }

            Look.DrawText(g, row.Id.ToString(CultureInfo.InvariantCulture), IdFont, Look.TextDim,
                new Rect(8, top, 46, RowHeight), TextAlignment.Right);

            // The value, switch or buttons on the right; the name and details fill what's left.
            double rightStart;
            if (row.Actions.Count > 0)
            {
                var buttons = ButtonRects(row, top, size);
                rightStart = buttons[0].Rect.X;
                for (int i = 0; i < buttons.Count; i++)
                {
                    bool lit = hover && _hoverPart == Part.Button && _hoverButton == i;
                    var rect = buttons[i].Rect.Deflate(0.5);
                    g.DrawRectangle(Look.Brush(lit ? Look.AccentHover : Look.Field),
                        Look.Pen(lit ? Look.AccentBorder : Look.FieldBorder, 1), Look.Rounded(rect, Math.Min(6, Look.ControlRadius + 3)));
                    Look.DrawText(g, buttons[i].Action, ButtonFont, lit ? Look.GoldBright : Look.TextMuted, rect, TextAlignment.Center);
                }
            }
            else if (row.On is { } on)
            {
                var pill = PillRect(top, size);
                rightStart = pill.X;
                bool lit = hover && _hoverPart == Part.Pill;
                g.DrawRectangle(Look.Brush(on ? (lit ? Look.AccentHover : Look.AccentDim) : (lit ? Look.FieldHover : Look.Field)),
                    Look.Pen(on ? Look.AccentBorder : Look.FieldBorder, 1), Look.Rounded(pill.Deflate(0.5), pill.Height / 2));
                Look.DrawText(g, on ? "ON" : "OFF", ButtonFont, on ? Look.Gold : Look.TextDim, pill, TextAlignment.Center);
            }
            else if (row.Editable || row.Value.Length > 0)
            {
                var cell = ValueCell(top, size);
                rightStart = cell.X;
                if (row.Editable && _list._editing != row)
                {
                    bool lit = hover && _hoverPart == Part.Value;
                    g.DrawRectangle(Look.Brush(lit ? Look.FieldHover : Look.Field), Look.Pen(lit ? Look.CardBorderHover : Look.FieldBorder, 1),
                        Look.Rounded(cell.Deflate(0.5), Look.ControlRadius));
                }
                if (_list._editing != row)
                {
                    Look.DrawText(g, row.Value, NameFont, row.Editable ? Look.Text : Look.TextMuted,
                        new Rect(cell.X + 8, cell.Y, Math.Max(1, cell.Width - 16), cell.Height), TextAlignment.Right);
                }
            }
            else
            {
                rightStart = RightEdge(size);
            }

            double nameLeft = 66, nameRight = rightStart - 12;
            if (nameRight - nameLeft < 4) return;
            string name = row.Name.Length > 0 ? row.Name : "(no name)";
            var nameColor = row.Name.Length > 0 ? Look.Text : Look.TextDim;
            double nameWidth = Math.Min(Look.Measure(name, NameFont).Width + 2, nameRight - nameLeft);
            Look.DrawText(g, name, NameFont, nameColor, new Rect(nameLeft, top, nameWidth, RowHeight));
            if (row.Detail.Length > 0 && nameRight - (nameLeft + nameWidth + 10) > 24)
            {
                Look.DrawText(g, row.Detail, DetailFont, Look.TextDim,
                    new Rect(nameLeft + nameWidth + 10, top, nameRight - nameLeft - nameWidth - 10, RowHeight));
            }
        }
    }
}
