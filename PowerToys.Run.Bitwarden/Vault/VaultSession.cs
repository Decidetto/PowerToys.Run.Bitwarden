using Microsoft.Win32;
using PowerToys.Run.Bitwarden.Cli;
using System.IO;
using System.Security;
using Wox.Plugin.Logger;

namespace PowerToys.Run.Bitwarden.Vault;

internal sealed class VaultSession : IDisposable
{
    // AutoLockMinutes <= this value means "never auto-lock", so we skip the timer entirely.
    public const int AutoLockDisabledValue = 0;
    // Cap on results we render.
    public const int MaxRenderedResults = 20;

    private static readonly TimeSpan AutoLockCheckPeriod = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SyncPeriod = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ServeReadyInitialPoll = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan ServeReadyMaxPoll = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan ServeReadyDeadline = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LockGateTimeout = TimeSpan.FromSeconds(1);

    // Score weights for the local fuzzy filter. Tiered so that strong matches always
    // outrank weak matches regardless of where they appear.
    private const int ScoreNamePrefix = 1000;
    private const int ScoreNameSubstring = 100;
    private const int ScoreUsernamePrefix = 50;
    private const int ScoreUsernameSubstring = 10;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly UsageStats _usage;

    private BitwardenServeProcess? _serve;
    private BitwardenClient? _client;
    private PluginSettings _settings = PluginSettings.Empty;
    private System.Threading.Timer? _autoLockTimer;
    private System.Threading.Timer? _syncTimer;
    // Holds an in-flight pre-unlock sync kicked off when the user clicked Unlock. The
    // unlock path awaits this before refreshing the cache, so the user sees fresh data
    // when the vault opens.
    private Task? _pendingSync;
    private DateTime _lastActivityUtc = DateTime.UtcNow;
    private volatile VaultState _state = VaultState.Uninitialized;
    // Reference swap is atomic on the CLR. Readers always see a fully populated list.
    private List<VaultEntry>? _itemsCache;
    private bool _sessionSwitchSubscribed;

    public VaultState State => _state;
    public string? LastError { get; private set; }

    public event Action<VaultState>? StateChanged;

    public VaultSession(string dataDir)
    {
        _usage = new UsageStats(Path.Combine(dataDir, "usage.json"));
        SystemEvents.SessionSwitch += OnWindowsSessionSwitch;
        _sessionSwitchSubscribed = true;
    }

    public async Task InitializeAsync(PluginSettings settings, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _settings = settings;
            Transition(VaultState.Initializing);
            await BuildSessionAsync(ct);
        }
        finally { _gate.Release(); }
    }

    // Auth-affecting changes (server URL, client_id, client_secret, bw path).
    public async Task ReconfigureAsync(PluginSettings settings, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _settings = settings;
            DisposeRuntime();
            Transition(VaultState.Initializing);
            await BuildSessionAsync(ct);
        }
        finally { _gate.Release(); }
    }

    // Lock-policy-only update. No need to log out or restart bw serve.
    public void ApplyAutoLockSetting(int autoLockMinutes)
    {
        _settings = _settings with { AutoLockMinutes = autoLockMinutes };
        if (_state == VaultState.Ready)
        {
            StopAutoLockTimer();
            StartAutoLockTimer();
        }
    }

    public void ApplyUsageBoostSetting(bool enabled)
    {
        _settings = _settings with { UsageBoostEnabled = enabled };
    }

    public async Task<bool> UnlockWithPasswordAsync(SecureString password, CancellationToken ct)
    {
        if (password is null || password.Length == 0) return false;
        await _gate.WaitAsync(ct);
        try
        {
            // bw serve is pre-warmed at Init, so we always have a running daemon to talk to.
            if (_client is null)
            {
                LastError = "bw serve is not running. Try toggling a setting to reconfigure.";
                return false;
            }

            LastError = null;
            var ok = await _client.UnlockAsync(password, ct);
            if (!ok) { LastError = "Wrong master password."; return false; }

            // Wait for any running pre-unlock background sync to finish before refreshing.
            var pending = _pendingSync;
            _pendingSync = null;
            if (pending is not null) await pending;

            if (!await RefreshCacheAsync(ct))
            {
                LastError = "Vault unlocked but failed to load items. See PowerToys Run logs.";
                // Re-lock to keep our state and bw serve's state coherent.
                try { await _client.LockAsync(ct); }
                catch (Exception ex) { Log.Exception("Re-lock after refresh failure failed", ex, typeof(VaultSession)); }
                return false;
            }
            Transition(VaultState.Ready);
            return true;
        }
        finally { _gate.Release(); }
    }

    public IReadOnlyList<VaultEntry> Search(string query)
    {
        Touch();
        if (_state != VaultState.Ready) return [];

        var cache = _itemsCache;
        if (cache is null) return [];

        var q = query?.Trim() ?? string.Empty;
        var emptyQuery = q.Length == 0;

        // Score = relevance (match strength) + pick-frequency boost (capped). For
        // empty queries the relevance is zero for everyone, so frequency alone
        // determines order: heavily-used items rise to the top, ties sort A→Z.
        // With the boost setting off, score is pure relevance and empty-query results
        // come back alphabetical.
        var useBoost = _settings.UsageBoostEnabled;
        var scored = new List<(VaultEntry Entry, int Score)>(capacity: cache.Count);
        foreach (var entry in cache)
        {
            var match = emptyQuery ? 0 : ScoreMatch(entry, q);
            if (!emptyQuery && match == 0) continue;
            var boost = useBoost ? _usage.GetBoost(entry.Id) : 0;
            scored.Add((entry, match + boost));
        }

        return scored
            .OrderByDescending(t => t.Score)
            .ThenBy(t => t.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxRenderedResults)
            .Select(t => t.Entry)
            .ToList();
    }

    internal static int ScoreMatch(VaultEntry e, string q)
    {
        var score = 0;
        if (e.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) score += ScoreNamePrefix;
        else if (e.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) score += ScoreNameSubstring;
        if (!string.IsNullOrEmpty(e.Username))
        {
            if (e.Username!.StartsWith(q, StringComparison.OrdinalIgnoreCase)) score += ScoreUsernamePrefix;
            else if (e.Username.Contains(q, StringComparison.OrdinalIgnoreCase)) score += ScoreUsernameSubstring;
        }
        return score;
    }

    // Accepts a bare host like "bitwarden.eu" or a full URL. The CLI requires a full URL
    // (and the well-known "bitwarden.eu" / "bitwarden.com" forms point at vault.<host>).
    internal static string NormalizeServerUrl(string input)
    {
        var s = input.Trim().TrimEnd('/');
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return s;
        if (s.Equals("bitwarden.eu", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("bitwarden.com", StringComparison.OrdinalIgnoreCase))
            return "https://vault." + s;
        return "https://" + s;
    }

    // Fire-and-forget sync, kicked off when the user opens the Unlock dialog.
    // UnlockWithPasswordAsync awaits this before reading the cache.
    public void StartBackgroundSync()
    {
        var client = _client;
        if (client is null) return;
        var existing = _pendingSync;
        if (existing is not null && !existing.IsCompleted) return;

        _pendingSync = Task.Run(async () =>
        {
            try { await client.SyncAsync(CancellationToken.None); }
            catch (Exception ex) { Log.Exception("Pre-unlock background sync failed", ex, typeof(VaultSession)); }
        });
    }

    // User-triggered "force sync". Takes the gate so we can't race a reconfigure or unlock.
    public async Task<bool> SyncNowAsync(CancellationToken ct)
    {
        Touch();
        await _gate.WaitAsync(ct);
        try
        {
            if (_state != VaultState.Ready || _client is null) return false;
            var ok = await _client.SyncAsync(ct);
            if (ok) ok = await RefreshCacheAsync(ct);
            return ok;
        }
        finally { _gate.Release(); }
    }

    // User-triggered explicit lock. Waits for the gate indefinitely; if the user said lock,
    // we lock once nothing else is mid-flight. Returns true iff we actually transitioned Ready → Locked.
    public async Task<bool> LockNowAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_state != VaultState.Ready) return false;
            await LockHoldingGateAsync("user command (:lock)");
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<string?> GetPasswordAsync(string id, CancellationToken ct)
    {
        Touch();
        if (_state != VaultState.Ready || _client is null) return null;
        try
        {
            var pw = await _client.GetPasswordAsync(id, ct);
            // Count the pick only when a real password came back and only when usage-based ranking is on.
            if (!string.IsNullOrEmpty(pw) && _settings.UsageBoostEnabled) _usage.Increment(id);
            return pw;
        }
        catch (Exception ex)
        {
            Log.Exception("GetPasswordAsync failed", ex, typeof(VaultSession));
            return null;
        }
    }

    private async Task BuildSessionAsync(CancellationToken ct)
    {
        try
        {
            var cli = new BitwardenCli(_settings.BwExePathOverride);
            if (!cli.IsAvailable())
            {
                LastError = "Bitwarden CLI ('bw') not found on PATH.";
                Transition(VaultState.BwMissing);
                return;
            }

            if (!string.IsNullOrWhiteSpace(_settings.ServerUrl))
                await cli.ConfigServerAsync(NormalizeServerUrl(_settings.ServerUrl!), ct);

            var status = await cli.StatusAsync(ct);
            if (status == BwStatus.Unauthenticated)
            {
                if (string.IsNullOrEmpty(_settings.ClientId) || string.IsNullOrEmpty(_settings.ClientSecret))
                {
                    LastError = "API client_id and client_secret are required.";
                    Transition(VaultState.LoggedOut);
                    return;
                }
                var ok = await cli.LoginApiKeyAsync(_settings.ClientId, _settings.ClientSecret, ct);
                if (!ok)
                {
                    LastError = "Login failed - verify your API credentials.";
                    Transition(VaultState.LoggedOut);
                    return;
                }
            }

            // Pre-warm bw serve so the user-triggered unlock is just one HTTP round-trip
            // instead of waiting for two Node-based subprocesses to cold-start.
            _serve = new BitwardenServeProcess(cli.ExePath);
            _serve.Start();
            _client = new BitwardenClient(_serve.BaseUri);
            if (!await WaitForServeReadyAsync(ct))
            {
                LastError = "bw serve failed to come up within the readiness deadline.";
                Transition(VaultState.Failed);
                return;
            }

            Transition(VaultState.Locked);
        }
        catch (Exception ex)
        {
            // Avoid surfacing raw exception text through the Failed-state SubTitle.
            // The full exception goes to the log.
            LastError = "Failed to initialize bw serve. See PowerToys Run logs.";
            Log.Exception("BuildSessionAsync failed", ex, typeof(VaultSession));
            Transition(VaultState.Failed);
        }
    }

    // Polls /status with exponential backoff until the daemon answers anything other than "unreachable".
    private async Task<bool> WaitForServeReadyAsync(CancellationToken ct)
    {
        if (_client is null) return false;
        var delay = ServeReadyInitialPoll;
        var deadline = DateTime.UtcNow + ServeReadyDeadline;
        while (DateTime.UtcNow < deadline)
        {
            if (await _client.GetStatusAsync(ct) != BwStatus.Unreachable) return true;
            await Task.Delay(delay, ct);
            if (delay < ServeReadyMaxPoll)
                delay = TimeSpan.FromMilliseconds(
                    Math.Min(delay.TotalMilliseconds * 1.5, ServeReadyMaxPoll.TotalMilliseconds));
        }
        return false;
    }

    // Returns true on success, false if listing failed (already logged). Callers on the
    // unlock and user-sync paths react to false; the periodic background timer ignores it.
    private async Task<bool> RefreshCacheAsync(CancellationToken ct)
    {
        if (_client is null) return false;
        try
        {
            var items = await _client.ListItemsAsync(ct);
            var fresh = items
                .Where(i => i.Type == 1 && i.DeletedDate is null)
                .Select(i => new VaultEntry(
                    i.Id, i.Name, i.Login?.Username, i.Login?.Uris?.FirstOrDefault()?.Uri))
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _itemsCache = fresh;
            Log.Info($"Vault cache refreshed: {fresh.Count} login items.", typeof(VaultSession));
            return true;
        }
        catch (Exception ex)
        {
            Log.Exception("RefreshCacheAsync failed", ex, typeof(VaultSession));
            return false;
        }
    }

    private void StartSyncTimer()
    {
        StopSyncTimer();
        _syncTimer = new System.Threading.Timer(async _ =>
        {
            try
            {
                if (_client is null) return;
                await _client.SyncAsync(CancellationToken.None);
                await RefreshCacheAsync(CancellationToken.None);
            }
            catch (Exception ex) { Log.Exception("Periodic sync failed", ex, typeof(VaultSession)); }
        }, null, SyncPeriod, SyncPeriod);
    }

    private void StopSyncTimer()
    {
        _syncTimer?.Dispose();
        _syncTimer = null;
    }

    private void StartAutoLockTimer()
    {
        StopAutoLockTimer();

        if (_settings.AutoLockMinutes <= AutoLockDisabledValue)
        {
            Log.Debug("Auto-lock disabled (AutoLockMinutes = 0); no idle timer started.", typeof(VaultSession));
            return;
        }

        var timeout = TimeSpan.FromMinutes(_settings.AutoLockMinutes);
        _autoLockTimer = new System.Threading.Timer(
            async _ => await AutoLockTickAsync(timeout),
            state: null,
            dueTime: AutoLockCheckPeriod,
            period: AutoLockCheckPeriod);
    }

    private void StopAutoLockTimer()
    {
        _autoLockTimer?.Dispose();
        _autoLockTimer = null;
    }

    private async Task AutoLockTickAsync(TimeSpan timeout)
    {
        try
        {
            if (_state != VaultState.Ready) return;
            if (DateTime.UtcNow - _lastActivityUtc < timeout) return;
            await LockInternalAsync($"Auto-lock after {timeout.TotalMinutes:F0} min idle.");
        }
        catch (Exception ex) { Log.Exception("Auto-lock tick failed", ex, typeof(VaultSession)); }
    }

    private void OnWindowsSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is not (SessionSwitchReason.SessionLock
            or SessionSwitchReason.ConsoleDisconnect
            or SessionSwitchReason.RemoteDisconnect)) return;
        if (_state != VaultState.Ready) return;
        // Fire-and-forget. The session-switch callback fires on the SystemEvents thread
        // and we don't want to block it.
        _ = Task.Run(() => LockInternalAsync($"Windows session {e.Reason}."));
    }

    private async Task LockInternalAsync(string reason)
    {
        // Take the gate so we can't race an in-flight unlock or reconfigure. Short timeout.
        // If something else holds the gate, that operation will leave the vault in its
        // own intended state; we don't need to stomp on it.
        if (!await _gate.WaitAsync(LockGateTimeout)) return;
        try { await LockHoldingGateAsync(reason); }
        finally { _gate.Release(); }
    }

    // Caller must already hold _gate. Centralises the lock POST + state transition so
    // both auto/system-triggered (timeout-gated) and user-triggered (wait-indefinitely)
    // entry points produce identical end states.
    private async Task LockHoldingGateAsync(string reason)
    {
        if (_state != VaultState.Ready) return;
        Log.Info($"Locking vault: {reason}", typeof(VaultSession));
        if (_client is not null)
        {
            try { await _client.LockAsync(CancellationToken.None); }
            catch (Exception ex) { Log.Exception("Lock POST failed", ex, typeof(VaultSession)); }
        }
        Transition(VaultState.Locked);
    }

    private void Touch() => _lastActivityUtc = DateTime.UtcNow;

    private void Transition(VaultState next)
    {
        var prev = _state;
        _state = next;
        if (next == VaultState.Ready)
        {
            // Anchor the idle clock to the moment we became usable.
            _lastActivityUtc = DateTime.UtcNow;
            StartAutoLockTimer();
            StartSyncTimer();
        }
        else
        {
            // No reason to keep either timer ticking: there's nothing to auto-lock, and
            // no point pulling fresh vault data the user can't read while it's locked.
            // The freshness gap is closed by syncing at unlock time (see UnlockWithPasswordAsync).
            StopAutoLockTimer();
            StopSyncTimer();
        }

        if (prev != next)
        {
            Log.Info($"VaultState: {prev} → {next}", typeof(VaultSession));
            try { StateChanged?.Invoke(next); }
            catch (Exception ex) { Log.Exception("StateChanged handler threw", ex, typeof(VaultSession)); }
        }
    }

    private void DisposeRuntime()
    {
        StopAutoLockTimer();
        StopSyncTimer();
        // Don't await the pending sync it's fire-and-forget; if it's still running it'll
        // throw against the disposed _client below and be swallowed by its own try/catch.
        _pendingSync = null;
        _client?.Dispose(); _client = null;
        _serve?.Dispose(); _serve = null;
        _itemsCache = null;
    }

    public void Dispose()
    {
        if (_sessionSwitchSubscribed)
        {
            try { SystemEvents.SessionSwitch -= OnWindowsSessionSwitch; }
            catch (Exception ex) { Log.Exception("Unsubscribe SessionSwitch failed", ex, typeof(VaultSession)); }
        }
        DisposeRuntime();
        _usage.Dispose();
        _gate.Dispose();
    }
}
