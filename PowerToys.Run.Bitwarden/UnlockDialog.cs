using PowerToys.Run.Bitwarden.Vault;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PowerToys.Run.Bitwarden;

// Frameless code-only WPF window styled to feel like part of the PowerToys Run
// launcher rather than a standard Win32 dialog: rounded corners, drop shadow,
// dark palette.
internal sealed class UnlockDialog : Window
{
    // HWND of PT Run's launcher, captured by the caller.
    private readonly IntPtr _ptRunHwnd;

    private readonly PasswordBox _passwordBox = new();
    private readonly TextBlock _errorLabel = new();
    private readonly Button _unlockButton = new();
    private readonly Button _cancelButton = new();
    private bool _busy;
    // Set in Closed so that an unlock in flight when the user hits Cancel/Escape doesn't
    // touch controls on a closed Window after the await returns. Touching WPF controls
    // post-close can throw, and an async-void Click handler would propagate the throw to
    // the dispatcher's unhandled-exception path and crash PT Run.
    private bool _isClosing;

    public Func<SecureString, Task<UnlockResult>>? OnUnlockRequested { get; set; }

    public UnlockDialog(IntPtr ptRunHwnd = default)
    {
        _ptRunHwnd = ptRunHwnd;

        Title = "Bitwarden - Unlock vault";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        // CenterScreen is the fallback. When we have PT Run's HWND, the Loaded handler
        // below overrides via SetWindowPos with the right monitor + centering math.
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
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
        Closed += (_, _) => _isClosing = true;

        Loaded += (_, _) =>
        {
            PositionOnPtRunMonitor();
            Activate();
            _passwordBox.Focus();
        };
    }

    // Horizontal: center on PT Run. Vertical: center on its monitor's work area.
    // All math in physical pixels via Win32.
    private void PositionOnPtRunMonitor()
    {
        if (_ptRunHwnd == IntPtr.Zero) return;
        var ourHwnd = new WindowInteropHelper(this).Handle;
        if (ourHwnd == IntPtr.Zero) return;

        if (!GetWindowRect(_ptRunHwnd, out var ptRunPx)) return;
        var hMon = MonitorFromWindow(_ptRunHwnd, MONITOR_DEFAULTTONEAREST);
        if (hMon == IntPtr.Zero) return;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hMon, ref mi)) return;
        if (!GetWindowRect(ourHwnd, out var ourPx)) return;

        var ourW = ourPx.Right - ourPx.Left;
        var ourH = ourPx.Bottom - ourPx.Top;
        var x = (ptRunPx.Left + ptRunPx.Right - ourW) / 2;
        var y = (mi.rcWork.Top + mi.rcWork.Bottom - ourH) / 2;

        SetWindowPos(ourHwnd, IntPtr.Zero, x, y, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

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
            // Keep the unlock transaction off the UI thread.
            var result = await Task.Run(() => requested(password));
            if (_isClosing) return;
            if (result.Success) { DialogResult = true; Close(); return; }
            _errorLabel.Text = result.Error ?? "Unlock failed. Please try again.";
        }
        catch (Exception)
        {
            if (!_isClosing) _errorLabel.Text = "Unlock could not complete. Please try again or restart PowerToys.";
        }
        finally
        {
            password.Dispose();
            if (!_isClosing)
            {
                _passwordBox.Clear();
                _busy = false;
                _unlockButton.IsEnabled = true;
                _unlockButton.Content = "Unlock";
                _passwordBox.Focus();
            }
        }
    }
}
