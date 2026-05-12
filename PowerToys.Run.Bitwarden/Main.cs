using ManagedCommon;
using Microsoft.PowerToys.Settings.UI.Library;
using PowerToys.Run.Bitwarden.Security;
using PowerToys.Run.Bitwarden.Vault;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
                return BuildSearchResults(searchText, icon);

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

    // `search` is also used as QueryTextDisplay on each result. PT prepends the configured
    // ActionKeyword to it, producing the same string the user already has in the input box,
    // which suppresses PT's selection-driven Text rewrite and keeps the caret stable.
    private List<Result> BuildSearchResults(string search, string icon)
    {
        var entries = _session!.Search(search);
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
                        var pw = await _session.GetPasswordAsync(e.Id, CancellationToken.None);
                        if (!string.IsNullOrEmpty(pw)) SecureClipboard.SetText(pw!);
                    });
                    return true;
                },
            });
        }
        return results;
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

        if (!string.IsNullOrEmpty(entry.FirstUri))
        {
            menu.Add(new ContextMenuResult
            {
                PluginName = Name,
                Title = "Open URL (Ctrl+O)",
                Glyph = "", // Globe
                FontFamily = "Segoe MDL2 Assets",
                AcceleratorKey = Key.O,
                AcceleratorModifiers = ModifierKeys.Control,
                Action = _ => { OpenUrl(entry.FirstUri!); return true; },
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
            _ = Task.Run(async () =>
            {
                try { await _session.ReconfigureAsync(_settings, CancellationToken.None); }
                catch (Exception ex) { Log.Exception("ReconfigureAsync failed", ex, typeof(Main)); }
            });
        }
        else if (diff.AutoLockChanged)
        {
            // Lock-policy-only change: don't tear down bw serve, just resize the timer.
            _session.ApplyAutoLockSetting(incoming.AutoLockMinutes);
        }
    }

    private void OnSessionStateChanged(VaultState newState)
    {
        if (newState == VaultState.Ready) _hasBeenReady = true;

        // Three refresh triggers, all driven by the same staleness problem:
        // (a) initial boot finishing -> replace "starting up…" with whatever's next;
        // (b) Locked → Ready (just unlocked) -> replace the "Unlock" prompt with results;
        // (c) Ready → Locked (auto-lock fired) -> clear the now-stale account list.
        var initialBootCompleting =
            !_hasFinishedInitialBoot && newState is not (VaultState.Uninitialized or VaultState.Initializing);
        if (initialBootCompleting) _hasFinishedInitialBoot = true;

        var unlocked = newState == VaultState.Ready;
        var lockingFromReady = newState == VaultState.Locked && _hasBeenReady;
        if (!initialBootCompleting && !unlocked && !lockingFromReady) return;

        var api = _context?.API;
        if (api is null) return;
        var keyword = _context?.CurrentPluginMetadata?.ActionKeyword;
        var defaultQuery = string.IsNullOrEmpty(keyword) ? string.Empty : keyword + " ";

        // After unlock, prefer the query the user had typed before the prompt so their
        // search context survives the unlock detour.
        string newQuery;
        var pending = _pendingUnlockQuery;
        if (unlocked && !string.IsNullOrEmpty(pending))
        {
            newQuery = pending!;
        }
        else
        {
            newQuery = defaultQuery;
        }
        _pendingUnlockQuery = null;

        // BeginInvoke on the dispatcher so we don't block the state-transition path.
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            try { api.ChangeQuery(newQuery, requery: true); }
            catch (Exception ex) { Log.Exception("ChangeQuery on state change failed", ex, typeof(Main)); }
        }));
    }

    private void ShowUnlockDialog()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || _session is null) return;

        // BeginInvoke (not Invoke) so the Action delegate can return immediately and PT hides
        // its window before the dialog is shown. Otherwise the modal pump runs under PT's UI.
        dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var dialog = new UnlockDialog
                {
                    OnUnlockRequested = pw => _session.UnlockWithPasswordAsync(pw, CancellationToken.None),
                };
                dialog.ShowDialog();
            }
            catch (Exception ex) { Log.Exception("ShowUnlockDialog failed", ex, typeof(Main)); }
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
