using PowerToys.Run.Bitwarden.Native;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PowerToys.Run.Bitwarden;

// Frameless code-only WPF window styled to feel like part of the PowerToys Run
// launcher instead of a standard Win32 dialog: no titlebar, rounded corners,
// drop shadow, dark palette. Drag from anywhere on the panel.
internal sealed class UnlockDialog : Window
{
    // Used to pre-center the dialog on the cursor monitor before WPF has computed
    // ActualHeight; close enough to avoid a flash, then refined in Loaded.
    private const double EstimatedDialogHeight = 240;

    private readonly PasswordBox _passwordBox = new();
    private readonly TextBlock _errorLabel = new();
    private readonly Button _unlockButton = new();
    private readonly Button _cancelButton = new();
    private bool _busy;

    public Func<SecureString, Task<bool>>? OnUnlockRequested { get; set; }

    public UnlockDialog()
    {
        Title = "Bitwarden - Unlock vault";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        // Manual + custom positioning so the dialog opens on the monitor the user is
        // working on (cursor location) rather than the primary screen.
        WindowStartupLocation = WindowStartupLocation.Manual;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;

        // PowerToys Run dark launcher palette.
        var panelBg = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B));
        var subtleFg = new SolidColorBrush(Color.FromRgb(0xB8, 0xB8, 0xB8));
        var inputBg = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        var inputBorder = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
        var errorFg = new SolidColorBrush(Color.FromRgb(0xFF, 0x7C, 0x7C));

        _passwordBox.Margin = new Thickness(0, 0, 0, 10);
        _passwordBox.Padding = new Thickness(8, 6, 8, 6);
        _passwordBox.Background = inputBg;
        _passwordBox.Foreground = Brushes.White;
        _passwordBox.BorderBrush = inputBorder;
        _passwordBox.BorderThickness = new Thickness(1);
        _passwordBox.CaretBrush = Brushes.White;
        _passwordBox.FontSize = 14;

        _errorLabel.Foreground = errorFg;
        _errorLabel.Margin = new Thickness(0, 0, 0, 10);
        _errorLabel.TextWrapping = TextWrapping.Wrap;
        _errorLabel.MinHeight = 18;

        _unlockButton.Content = "Unlock";
        _unlockButton.IsDefault = true;
        _unlockButton.MinWidth = 100;
        _unlockButton.Padding = new Thickness(16, 6, 16, 6);
        _unlockButton.Margin = new Thickness(0, 0, 8, 0);

        _cancelButton.Content = "Cancel";
        _cancelButton.IsCancel = true;
        _cancelButton.MinWidth = 100;
        _cancelButton.Padding = new Thickness(16, 6, 16, 6);

        var header = new TextBlock
        {
            Text = "Bitwarden",
            Foreground = Brushes.White,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 2),
        };
        var sub = new TextBlock
        {
            Text = "Enter master password to unlock the vault.",
            Foreground = subtleFg,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 14),
        };

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttonRow.Children.Add(_unlockButton);
        buttonRow.Children.Add(_cancelButton);

        var content = new StackPanel();
        content.Children.Add(header);
        content.Children.Add(sub);
        content.Children.Add(_passwordBox);
        content.Children.Add(_errorLabel);
        content.Children.Add(buttonRow);

        // Outer Border carries the visible shape: rounded corners + shadow. The Window
        // itself is transparent, so the Margin on this Border is the visible breathing
        // room the shadow lives in.
        var panel = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = panelBg,
            Margin = new Thickness(16),
            Padding = new Thickness(20),
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 18,
                ShadowDepth = 0,
                Opacity = 0.55,
            },
            Child = content,
        };
        // Frameless windows lose the standard drag handle, so let any non-interactive
        // area of the panel act as the drag region.
        panel.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        };
        Content = panel;

        _unlockButton.Click += async (_, _) => await TryUnlockAsync();

        // Pre-position before the window is shown to avoid a flash on the primary
        // monitor; refine again after layout so the height-based centering uses
        // the real ActualHeight rather than the rough estimate.
        SourceInitialized += (_, _) => PositionOnCursorMonitor(estimatedHeight: EstimatedDialogHeight);
        Loaded += (_, _) =>
        {
            PositionOnCursorMonitor(estimatedHeight: ActualHeight);
            Activate();
            _passwordBox.Focus();
        };
    }

    private void PositionOnCursorMonitor(double estimatedHeight)
    {
        if (!MonitorHelper.TryGetWorkAreaUnderCursor(out var area)) return;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - estimatedHeight) / 2;
    }

    private async Task TryUnlockAsync()
    {
        if (_busy || OnUnlockRequested is null) return;
        _busy = true;
        _unlockButton.IsEnabled = false;
        _unlockButton.Content = "Unlocking…";
        _errorLabel.Text = string.Empty;
        // SecurePassword returns a fresh copy we own; dispose as soon as we're done so
        // the cleartext doesn't linger in memory longer than necessary.
        var password = _passwordBox.SecurePassword;
        var requested = OnUnlockRequested;
        try
        {
            // Run the unlock chain on a thread pool thread: bw unlock and bw serve spawn
            // synchronously (Process.Start blocks until the child exists), and the await
            // chain would otherwise run those sync steps on this dialog's UI thread.
            var ok = await Task.Run(() => requested(password));
            if (ok) { DialogResult = true; Close(); return; }
            _errorLabel.Text = "Unlock failed - wrong master password?";
        }
        catch (Exception ex)
        {
            _errorLabel.Text = "Unlock error: " + ex.Message;
        }
        finally
        {
            password.Dispose();
            _passwordBox.Clear();
            _busy = false;
            _unlockButton.IsEnabled = true;
            _unlockButton.Content = "Unlock";
            _passwordBox.Focus();
        }
    }
}
