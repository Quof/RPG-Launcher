using Avalonia.Styling;

namespace RpgLauncher;

/// <summary>Base for the launcher's dialogs: styled, fixed size, centered on the window that opened it.</summary>
internal class DialogWindow : Window
{
    /// <summary>The dialog's content; children are placed by its <see cref="ManualPanel.Layout"/> callback.</summary>
    protected ManualPanel Body { get; } = new();

    public DialogWindow()
    {
        Background = Look.Brush(Look.Bg);
        Foreground = Look.Brush(Look.Text);
        var font = Look.Font(9);
        FontFamily = font.Family;
        FontSize = font.Size;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.Manual;
        RequestedThemeVariant = Look.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
        Icon = App.WindowIcon;
        Content = Body;
    }

    /// <summary>Sets the size inside the window frame, like WinForms ClientSize.</summary>
    public void SetClientSize(double width, double height)
    {
        Width = width;
        Height = height;
    }

    /// <summary>Shows the dialog over <paramref name="owner"/>; true when it was accepted.</summary>
    public virtual Task<bool> ShowAsync(Window owner) => ShowDialog<bool>(owner);

    protected void Accept() => Close(true);

    protected void Cancel() => Close(false);

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        NativeMethods.StyleTitleBar(this);
    }
}

internal enum MessageButtons
{
    Ok,
    OkCancel,
    YesNo
}

/// <summary>The launcher's message box.</summary>
internal static class MessageDialog
{
    /// <summary>Shows a message; true for OK or Yes. <paramref name="defaultNo"/> makes Enter pick Cancel or No.</summary>
    public static async Task<bool> ShowAsync(Window? owner, string text, string caption,
        MessageButtons buttons = MessageButtons.Ok, bool defaultNo = false)
    {
        var dialog = new MessageWindow(text, caption, buttons, defaultNo);
        if (owner is { IsVisible: true }) return await dialog.ShowDialog<bool>(owner);

        dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowInTaskbar = true;
        var closed = new TaskCompletionSource<bool>();
        dialog.Closed += (_, _) => closed.TrySetResult(dialog.Result);
        dialog.Show();
        return await closed.Task;
    }

    private sealed class MessageWindow : DialogWindow
    {
        private const double Pad = 22;
        private const double MaxTextWidth = 440;
        private const double ButtonWidth = 96;
        private const double ButtonHeight = 36;
        private const double Gap = 10;

        private readonly bool _defaultNo;

        public bool Result { get; private set; }

        public MessageWindow(string text, string caption, MessageButtons buttons, bool defaultNo)
        {
            Title = caption;
            _defaultNo = defaultNo && buttons != MessageButtons.Ok;

            var font = Look.Font(9.5);
            var formatted = Look.Format(text, font, Look.Text);
            formatted.MaxTextWidth = MaxTextWidth;
            double textW = Math.Min(MaxTextWidth, Math.Ceiling(formatted.WidthIncludingTrailingWhitespace) + 4);
            double textH = Math.Ceiling(formatted.Height) + 2;
            var message = Ui.Paragraph(text, font, Look.Text);

            var (yesText, noText) = buttons switch
            {
                MessageButtons.YesNo => ("Yes", "No"),
                MessageButtons.OkCancel => ("OK", "Cancel"),
                _ => ("OK", (string?)null)
            };
            var yes = new PillButton { Text = yesText, Checked = !_defaultNo };
            yes.Click += (_, _) => Finish(true);
            Body.Children.Add(message);
            Body.Children.Add(yes);
            PillButton? no = null;
            if (noText is not null)
            {
                no = new PillButton { Text = noText, Checked = _defaultNo };
                no.Click += (_, _) => Finish(false);
                Body.Children.Add(no);
            }

            double buttonsW = no is null ? ButtonWidth : ButtonWidth * 2 + Gap;
            SetClientSize(Math.Max(320, Math.Max(textW, buttonsW) + Pad * 2), Pad + textH + 24 + ButtonHeight + Pad);
            Body.Layout = size =>
            {
                Body.Place(message, Pad, Pad, size.Width - Pad * 2, textH);
                double top = size.Height - Pad - ButtonHeight;
                Body.Place(yes, size.Width - Pad - (no is null ? ButtonWidth : ButtonWidth * 2 + Gap), top, ButtonWidth, ButtonHeight);
                if (no is not null) Body.Place(no, size.Width - Pad - ButtonWidth, top, ButtonWidth, ButtonHeight);
            };
        }

        private void Finish(bool result)
        {
            Result = result;
            Close(result);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key == Key.Enter) Finish(!_defaultNo);
            else if (e.Key == Key.Escape) Finish(false);
        }
    }
}
