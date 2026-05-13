using ManagedCommon;
using Microsoft.PowerToys.Settings.UI.Library;
using PowerToys.Run.Bitwarden.Security;
using PowerToys.Run.Bitwarden.Vault;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Wox.Plugin;
using Wox.Plugin.Logger;

namespace PowerToys.Run.Bitwarden;

public sealed class Main : IPlugin, IContextMenu, ISettingProvider, IDisposable
{
    public static string PluginID => "7B8F2D4A6E1C9F3B5D8A0E2C4F6B8D1A";

    // Base PT-Run score we hand the top match.
    private const int TopResultScore = 10_000;

    public string Name => "Bitwarden";
    public string Description => "Search your Bitwarden vault and copy passwords to the clipboard.";

    private PluginInitContext? _context;
    private string _iconLight = "Images\\Bitwarden.light.png";
    private string _iconDark = "Images\\Bitwarden.dark.png";

    private PluginSettings _settings = PluginSettings.Empty;
    private VaultSession? _session;
    private bool _hasBeenReady;
    private bool _hasFinishedInitialBoot;
    // Captured when the user invokes the Unlock prompt so we can put it right back into
    // their search after a successful unlock.
    private volatile string? _pendingUnlockQuery;
    // Guard against rapid double-Enter on the Locked-state result stacking two modal
    // dialogs; the second ShowDialog throws InvalidOperationException.
    private volatile bool _unlockDialogOpen;

    public IEnumerable<PluginAdditionalOption> AdditionalOptions => _settings.BuildAdditionalOptions();

    public void Init(PluginInitContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _context.API.ThemeChanged += OnThemeChanged;
        UpdateIcons();

        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "PowerToys", "PowerToys Run", "Settings", "Plugins",
            typeof(Main).Assembly.GetName().Name ?? "PowerToys.Run.Bitwarden");
        Directory.CreateDirectory(dataDir);

        _session = new VaultSession(dataDir);
        _session.StateChanged += OnSessionStateChanged;

        // Init runs off PT's UI thread so a slow CLI doesn't stall PT Run startup.
        _ = Task.Run(async () =>
        {
            try { await _session.InitializeAsync(_settings, CancellationToken.None); }
            catch (Exception ex) { Log.Exception("VaultSession.InitializeAsync failed", ex, typeof(Main)); }
        });
    }

    // Filters the in-memory vault cache locally -> sub-millisecond rtt per keystroke.
    public List<Result> Query(Query query)
    {
        if (_session is null) return [];
        var icon = CurrentIcon;
        // PT rewrites the input box on result navigation to "<ActionKeyword> <QueryTextDisplay>".
        // Setting every result's QueryTextDisplay to the current search portion makes that
        // rewrite a no-op (it matches what the user already typed), so the caret stays put.
        var searchText = query.Search ?? string.Empty;

        switch (_session.State)
        {
            case VaultState.Ready:
                // ':' prefix flips the query into command mode. Real vault item names can't
                // start with ':' (the CLI strips/rejects it), so this is a safe sentinel.
                return searchText.StartsWith(':')
                    ? BuildCommandResults(searchText, icon)
                    : BuildSearchResults(searchText, icon);

            case VaultState.Uninitialized:
            case VaultState.Initializing:
                return [ new Result
                {
                    Title = "Bitwarden - starting up…",
                    IcoPath = icon,
                    QueryTextDisplay = searchText,
                } ];

            case VaultState.Locked:
                var savedQuery = query.RawQuery;
                var subtitle = _hasBeenReady
                    ? "Vault auto-locked. Press Enter to unlock again."
                    : "Press Enter to enter your master password.";
                return [ new Result
                {
                    Title = "🔒 Unlock Bitwarden vault",
                    SubTitle = subtitle,
                    IcoPath = icon,
                    QueryTextDisplay = searchText,
                    Action = _ctx =>
                    {
                        _pendingUnlockQuery = savedQuery;
                        ShowUnlockDialog();
                        return true;
                    },
                } ];

            case VaultState.LoggedOut:
                return [ new Result
                {
                    Title = "Bitwarden - not configured",
                    SubTitle = "Open PowerToys Settings → PowerToys Run → Bitwarden and fill in your API client ID and secret.",
                    IcoPath = icon,
                    QueryTextDisplay = searchText,
                } ];

            case VaultState.BwMissing:
                return [ new Result
                {
                    Title = "Install the Bitwarden CLI",
                    SubTitle = "winget install Bitwarden.CLI, then restart PowerToys (Enter opens the docs)",
                    IcoPath = icon,
                    QueryTextDisplay = searchText,
                    Action = _ => { OpenUrl("https://bitwarden.com/help/cli/"); return true; },
                } ];

            case VaultState.Failed:
            default:
                return [ new Result
                {
                    Title = "Bitwarden - error",
                    SubTitle = _session.LastError ?? "see PT Run logs",
                    IcoPath = icon,
                    QueryTextDisplay = searchText,
                } ];
        }
    }

    private List<Result> BuildSearchResults(string search, string icon)
    {
        // Local copy so the lambda below captures a stable reference even if _session is
        // nulled out by a concurrent Dispose. Worst case is a method call on a disposed
        // session (caught by the inner Task.Run try) rather than an NRE on field access.
        var session = _session!;
        var entries = session.Search(search);
        if (entries.Count == 0)
        {
            return [ new Result
            {
                Title = string.IsNullOrWhiteSpace(search)
                    ? "Bitwarden - start typing to search your vault"
                    : $"No Bitwarden items match \"{search}\"",
                IcoPath = icon,
                QueryTextDisplay = search,
            } ];
        }

        // Search returns entries pre-sorted by relevance; preserve that order by giving the
        // first result the highest Score so PT doesn't re-rank against its own heuristic.
        var max = Math.Min(entries.Count, VaultSession.MaxRenderedResults);
        var results = new List<Result>(max);
        for (var i = 0; i < max; i++)
        {
            var e = entries[i];
            results.Add(new Result
            {
                Title = e.Name,
                SubTitle = string.IsNullOrEmpty(e.Username) ? "(no username)" : e.Username,
                IcoPath = icon,
                Score = TopResultScore - i,
                ContextData = e,
                QueryTextDisplay = search,
                Action = _ctx =>
                {
                    _ = Task.Run(async () =>
                    {
                        var pw = await session.GetPasswordAsync(e.Id, CancellationToken.None);
                        if (!string.IsNullOrEmpty(pw)) SecureClipboard.SetText(pw!);
                    });
                    return true;
                },
            });
        }
        return results;
    }

    private List<Result> BuildCommandResults(string search, string icon)
    {
        var sub = search.AsSpan().TrimStart(':').Trim().ToString();
        var results = new List<Result>();

        if (sub.Length == 0 || "sync".StartsWith(sub, StringComparison.OrdinalIgnoreCase))
        {
            results.Add(new Result
            {
                Title = "Sync vault from server",
                SubTitle = "Force-refresh the local cache from your Bitwarden account.",
                IcoPath = icon,
                Score = TopResultScore,
                QueryTextDisplay = search,
                Action = _ctx => { TriggerSync(); return true; },
            });
        }

        if (sub.Length == 0 || "lock".StartsWith(sub, StringComparison.OrdinalIgnoreCase))
        {
            results.Add(new Result
            {
                Title = "Lock vault now",
                SubTitle = "Re-lock immediately; master password will be required next time.",
                IcoPath = icon,
                Score = TopResultScore - 1,
                QueryTextDisplay = search,
                Action = _ctx => { TriggerLock(); return true; },
            });
        }

        if (results.Count == 0)
        {
            results.Add(new Result
            {
                Title = $"No Bitwarden command matches \":{sub}\"",
                SubTitle = "Try :sync",
                IcoPath = icon,
                QueryTextDisplay = search,
            });
        }
        return results;
    }

    private void TriggerSync()
    {
        if (_session is null) return;
        var api = _context?.API;
        _ = Task.Run(async () =>
        {
            bool ok;
            try { ok = await _session.SyncNowAsync(CancellationToken.None); }
            catch (Exception ex)
            {
                Log.Exception("SyncNowAsync threw", ex, typeof(Main));
                ok = false;
            }
            if (ok)
            {
                ForceRefreshAfterExplicitAction();
                return;
            }
            // Failure path surfaces a popup because /sync hits the same endpoint the periodic
            // 15-min timer hits without issue. A manual failure means something's broken.
            if (api is null) return;
            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                try { api.ShowMsg("Bitwarden", "Sync failed. See PowerToys Run logs.", CurrentIcon); }
                catch (Exception ex) { Log.Exception("ShowMsg after sync failure failed", ex, typeof(Main)); }
            }));
        });
    }

    private void TriggerLock()
    {
        if (_session is null) return;
        _ = Task.Run(async () =>
        {
            bool locked;
            try { locked = await _session.LockNowAsync(CancellationToken.None); }
            catch (Exception ex)
            {
                Log.Exception("LockNowAsync threw", ex, typeof(Main));
                locked = false;
            }
            // Refresh on success so the user reopening PT Run sees the Unlock prompt instead
            // of stale results from the just-locked vault.
            if (locked) ForceRefreshAfterExplicitAction();
        });
    }

    // After an explicit user action (`:sync` / `:lock`), force PT Run to re-render with
    // current state. Skips the refresh if PT Run is somehow visible in another
    // plugin's context, to avoid clobbering a query they've moved on to.
    private void ForceRefreshAfterExplicitAction()
    {
        var api = _context?.API;
        if (api is null) return;
        var keyword = _context?.CurrentPluginMetadata?.ActionKeyword ?? string.Empty;
        if (!StateRefreshDecision.ShouldRefreshAfterExplicitAction(TryReadCurrentPtRunInput(), keyword)) return;
        var query = string.IsNullOrEmpty(keyword) ? string.Empty : keyword + " ";
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            try { api.ChangeQuery(query, requery: true); }
            catch (Exception ex) { Log.Exception("Forced ChangeQuery after explicit action failed", ex, typeof(Main)); }
        }));
    }

    public List<ContextMenuResult> LoadContextMenus(Result selectedResult)
    {
        if (selectedResult?.ContextData is not VaultEntry entry) return [];
        var menu = new List<ContextMenuResult>();

        if (!string.IsNullOrEmpty(entry.Username))
        {
            menu.Add(new ContextMenuResult
            {
                PluginName = Name,
                Title = "Copy username (Ctrl+C)",
                Glyph = "", // Copy
                FontFamily = "Segoe MDL2 Assets",
                AcceleratorKey = Key.C,
                AcceleratorModifiers = ModifierKeys.Control,
                Action = _ => { SecureClipboard.SetText(entry.Username!); return true; },
            });
        }

        if (entry.Uris.Count > 0)
        {
            // First URL = the canonical/primary one per Bitwarden's own ordering.
            var url = entry.Uris[0];
            menu.Add(new ContextMenuResult
            {
                PluginName = Name,
                Title = "Open URL (Ctrl+O)",
                Glyph = "", // Globe
                FontFamily = "Segoe MDL2 Assets",
                AcceleratorKey = Key.O,
                AcceleratorModifiers = ModifierKeys.Control,
                Action = _ => { OpenUrl(url); return true; },
            });
        }

        return menu;
    }

    public void UpdateSettings(PowerLauncherPluginSettings settings)
    {
        var incoming = PluginSettings.From(settings.AdditionalOptions);
        var diff = PluginSettings.Diff(_settings, incoming);
        _settings = incoming;

        if (_session is null) return;

        if (diff.AuthChanged)
        {
            // Reconfigure rebuilds the session and picks up all non-auth setting values
            // along the way, so the per-setting Apply* calls below would be redundant.
            _ = Task.Run(async () =>
            {
                try { await _session.ReconfigureAsync(_settings, CancellationToken.None); }
                catch (Exception ex) { Log.Exception("ReconfigureAsync failed", ex, typeof(Main)); }
            });
            return;
        }
        if (diff.AutoLockChanged) _session.ApplyAutoLockSetting(incoming.AutoLockMinutes);
        if (diff.UsageBoostChanged)
        {
            _session.ApplyUsageBoostSetting(incoming.UsageBoostEnabled);
            // Force a requery so the user sees the re-ranked list on next reopen instead of
            // stale results that look like the toggle didn't take.
            ForceRefreshAfterExplicitAction();
        }
    }

    private void OnSessionStateChanged(VaultState newState)
    {
        if (newState == VaultState.Ready) _hasBeenReady = true;

        var api = _context?.API;
        if (api is null) return;

        var keyword = _context?.CurrentPluginMetadata?.ActionKeyword ?? string.Empty;
        var defaultQuery = string.IsNullOrEmpty(keyword) ? string.Empty : keyword + " ";

        var pending = _pendingUnlockQuery;
        _pendingUnlockQuery = null;

        // Decide BEFORE updating _hasFinishedInitialBoot; the helper distinguishes the
        // initial-boot transition by reading the prior value of that flag.
        var (refresh, newQuery) = StateRefreshDecision.Decide(
            newState, _hasBeenReady, _hasFinishedInitialBoot,
            pending, TryReadCurrentPtRunInput(), keyword, defaultQuery);

        if (!_hasFinishedInitialBoot && newState is not (VaultState.Uninitialized or VaultState.Initializing))
            _hasFinishedInitialBoot = true;

        if (!refresh) return;

        // BeginInvoke on the dispatcher so we don't block the state-transition path.
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            try { api.ChangeQuery(newQuery, requery: true); }
            catch (Exception ex) { Log.Exception("ChangeQuery on state change failed", ex, typeof(Main)); }
        }));
    }

    // Reads PT Run's input box, or null if the window is hidden or the control can't be
    // located. Used to gate forced requeries so background state transitions don't clobber
    // the user's input when they've moved on to another plugin's context.
    private static string? TryReadCurrentPtRunInput()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return null;
        if (!dispatcher.CheckAccess()) return dispatcher.Invoke(TryReadCurrentPtRunInput);
        var mainWindow = Application.Current?.MainWindow;
        if (mainWindow is null || !mainWindow.IsVisible) return null;
        return FindVisualDescendant<TextBox>(mainWindow, "QueryTextBox")?.Text;
    }

    private static T? FindVisualDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T t && t.Name == name) return t;
            var found = FindVisualDescendant<T>(child, name);
            if (found is not null) return found;
        }
        return null;
    }

    private void ShowUnlockDialog()
    {
        var dispatcher = Application.Current?.Dispatcher;
        // Local copy so the dialog's OnUnlockRequested lambda holds a stable reference even
        // if _session is nulled by a concurrent Dispose between scheduling and invocation.
        var session = _session;
        if (dispatcher is null || session is null) return;
        if (_unlockDialogOpen) return;
        _unlockDialogOpen = true;

        // Kick off a sync now; the user is about to spend seconds typing their master
        // password, which is free time to refresh the local store from the server. The
        // unlock path awaits this before reading the cache, so the user sees fresh items
        // when the vault opens.
        session.StartBackgroundSync();

        // Capture PT Run's HWND now.
        var w = Application.Current?.MainWindow;
        var ptRunHwnd = w is null ? IntPtr.Zero : new WindowInteropHelper(w).Handle;

        // BeginInvoke so the Action delegate can return immediately and PT hides its window before the dialog is shown.
        dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var dialog = new UnlockDialog(ptRunHwnd)
                {
                    OnUnlockRequested = pw => session.UnlockWithPasswordAsync(pw, CancellationToken.None),
                };
                dialog.Closed += (_, _) => _unlockDialogOpen = false;
                dialog.ShowDialog();
            }
            catch (Exception ex)
            {
                _unlockDialogOpen = false;
                Log.Exception("ShowUnlockDialog failed", ex, typeof(Main));
            }
        }));
    }

    // PT calls this on certain settings interactions. Returning an empty UserControl
    // tells PT to fall back to rendering AdditionalOptions, which is what we want.
    public Control CreateSettingPanel() => new UserControl();

    private string CurrentIcon =>
        _context?.API?.GetCurrentTheme() is Theme.Light or Theme.HighContrastWhite
            ? _iconLight : _iconDark;

    private void UpdateIcons()
    {
        if (_context?.CurrentPluginMetadata is null) return;
        _iconLight = _context.CurrentPluginMetadata.IcoPathLight;
        _iconDark = _context.CurrentPluginMetadata.IcoPathDark;
    }

    private void OnThemeChanged(Theme _, Theme __) => UpdateIcons();

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Exception("OpenUrl failed", ex, typeof(Main)); }
    }

    public void Dispose()
    {
        if (_context?.API != null) _context.API.ThemeChanged -= OnThemeChanged;
        if (_session != null) _session.StateChanged -= OnSessionStateChanged;
        _session?.Dispose();
    }
}
