using Microsoft.Win32;
using PowerToys.Run.Bitwarden.Cli;
using PowerToys.Run.Bitwarden.Security;
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
    private const int ScoreUrlPrefix = 100;
    private const int ScoreUrlSubstring = 50;
    private const int ScoreUsernamePrefix = 50;
    private const int ScoreUsernameSubstring = 10;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly UsageStats _usage;
    private readonly ApiCredentialStore? _credentialStore;
    private readonly string _cliDataDirectory;

    private BitwardenServeProcess? _serve;
    private BitwardenClient? _client;
    private PluginSettings _settings = PluginSettings.Empty;
    private System.Threading.Timer? _autoLockTimer;
    private System.Threading.Timer? _syncTimer;
    private int _syncTimerRunning;
    private DateTime _lastActivityUtc = DateTime.UtcNow;
    private volatile VaultState _state = VaultState.Uninitialized;
    // Reference swap is atomic on the CLR. Readers always see a fully populated list.
    private List<VaultEntry>? _itemsCache;
    private bool _sessionSwitchSubscribed;

    public VaultState State => _state;
    public string? LastError { get; private set; }

    public event Action<VaultState>? StateChanged;

    public VaultSession(string dataDir, ApiCredentialStore? credentialStore = null)
    {
        _usage = new UsageStats(Path.Combine(dataDir, "usage.json"));
        _credentialStore = credentialStore;
        _cliDataDirectory = Path.Combine(Path.GetFullPath(dataDir), "cli");
        SystemEvents.SessionSwitch += OnWindowsSessionSwitch;
        _sessionSwitchSubscribed = true;
    }

    public async Task InitializeAsync(PluginSettings settings, CancellationToken ct)
    {
        var changedStates = new List<VaultState>();
        await _gate.WaitAsync(ct);
        try
        {
            _settings = settings;
            DisposeRuntime();
            Transition(VaultState.Initializing, changedStates);
            await BuildSessionAsync(ct, changedStates);
        }
        finally { _gate.Release(); NotifyStateChanged(changedStates); }
    }

    // Auth-affecting changes (server URL, client_id, client_secret, bw path).
    public async Task ReconfigureAsync(PluginSettings settings, CancellationToken ct)
    {
        var changedStates = new List<VaultState>();
        await _gate.WaitAsync(ct);
        try
        {
            _settings = settings;
            DisposeRuntime();
            Transition(VaultState.Initializing, changedStates);
            await BuildSessionAsync(ct, changedStates);
        }
        finally { _gate.Release(); NotifyStateChanged(changedStates); }
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

    public async Task<UnlockResult> UnlockWithPasswordAsync(SecureString password, CancellationToken ct)
    {
        var changedStates = new List<VaultState>();
        await _gate.WaitAsync(ct);
        try
        {
            LastError = null;
            if (password is null || password.Length == 0)
                return UnlockFailed("Enter your master password.");
            if (_client is null)
                return UnlockFailed("The local CLI is not running. Reapply the plugin settings or restart PowerToys.");

            var client = _client;
            try
            {
                await client.UnlockAsync(password, ct);
                // Sync and unlock must not race: sync can change the CLI's account/key
                // state. Keep the entire unlock/sync/cache transaction under _gate.
                await client.SyncAsync(ct);
                if (!await RefreshCacheAsync(client, ct))
                    throw new BitwardenException(LastError ?? "Vault unlocked but failed to load items.");
                Transition(VaultState.Ready, changedStates);
                return UnlockResult.Unlocked;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await RelockAfterUnlockFailureAsync(client, changedStates);
                throw;
            }
            catch (Exception ex)
            {
                var error = ex is BitwardenException
                    ? ex.Message
                    : "Unlock failed because of an unexpected plugin error. Restart PowerToys.";
                // A timeout or a later sync/cache failure can happen after the CLI has
                // already unlocked. Do not leave that daemon unlocked after UI failure.
                await RelockAfterUnlockFailureAsync(client, changedStates);
                return UnlockFailed(error);
            }
        }
        finally { _gate.Release(); NotifyStateChanged(changedStates); }
    }

    private UnlockResult UnlockFailed(string error)
    {
        LastError = error;
        return UnlockResult.Failed(error);
    }

    private async Task RelockAfterUnlockFailureAsync(BitwardenClient client, List<VaultState> changedStates)
    {
        _itemsCache = null;
        try
        {
            await client.LockAsync(CancellationToken.None);
            Transition(VaultState.Locked, changedStates);
        }
        catch
        {
            // If re-locking cannot be confirmed, terminating our daemon is the safe
            // fallback. Never expose the underlying exception or response body.
            DisposeRuntime();
            Transition(VaultState.Failed, changedStates);
        }
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
        if (string.IsNullOrEmpty(q)) return 0;
        // Space-stripped name + query so "mybank" matches "My Bank Account" and "my bank"
        // matches "MyBankAccount". Most item names have no spaces, so StripSpaces is a
        // no-op for them.
        var query = StripSpaces(q);
        var score = TierScore(StripSpaces(e.Name), query, ScoreNamePrefix, ScoreNameSubstring);
        if (!string.IsNullOrEmpty(e.Username))
            score += TierScore(e.Username!, query, ScoreUsernamePrefix, ScoreUsernameSubstring);
        if (e.Uris.Count > 0)
        {
            // Take the best (max) match across all the item's URLs rather than summing,
            // so items with many URLs don't unfairly outrank single-URL items.
            var bestUrl = 0;
            foreach (var raw in e.Uris)
            {
                bestUrl = Math.Max(bestUrl,
                    TierScore(NormalizeUrlForMatch(raw), query, ScoreUrlPrefix, ScoreUrlSubstring));
                if (bestUrl == ScoreUrlPrefix) break; // can't beat prefix
            }
            score += bestUrl;
        }
        return score;
    }

    // Prefix matches beat substring matches in the same field.
    private static int TierScore(string target, string query, int prefixScore, int substringScore)
    {
        if (target.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return prefixScore;
        if (target.Contains(query, StringComparison.OrdinalIgnoreCase)) return substringScore;
        return 0;
    }

    private static string StripSpaces(string s) => s.Contains(' ') ? s.Replace(" ", string.Empty) : s;

    // Strip schema and leading www. so "github" matches "https://www.github.com" cleanly
    // and noise like "https" doesn't substring-match every URL.
    private static string NormalizeUrlForMatch(string url)
    {
        var u = url;
        if (u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) u = u[8..];
        else if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) u = u[7..];
        if (u.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) u = u[4..];
        return u;
    }

    // Accepts a bare host like "bitwarden.eu" or a full URL. The CLI requires a full URL
    // (and the well-known "bitwarden.eu" / "bitwarden.com" forms point at vault.<host>).
    internal static string NormalizeServerUrl(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "https://vault.bitwarden.com";
        var s = input.Trim().TrimEnd('/');
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return s;
        if (s.Equals("bitwarden.eu", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("bitwarden.com", StringComparison.OrdinalIgnoreCase))
            return "https://vault." + s;
        return "https://" + s;
    }

    // User-triggered "force sync". Takes the gate so we can't race a reconfigure or unlock.
    public async Task<bool> SyncNowAsync(CancellationToken ct)
    {
        Touch();
        await _gate.WaitAsync(ct);
        try
        {
            if (_state != VaultState.Ready || _client is null) return false;
            var client = _client;
            LastError = null;
            await client.SyncAsync(ct);
            return await RefreshCacheAsync(client, ct);
        }
        catch (BitwardenException ex)
        {
            LastError = ex.Message;
            return false;
        }
        finally { _gate.Release(); }
    }

    // User-triggered explicit lock. Waits for the gate indefinitely; if the user said lock,
    // we lock once nothing else is mid-flight. Returns true iff we actually transitioned Ready → Locked.
    public async Task<bool> LockNowAsync(CancellationToken ct)
    {
        var changedStates = new List<VaultState>();
        await _gate.WaitAsync(ct);
        try
        {
            if (_state != VaultState.Ready) return false;
            await LockHoldingGateAsync("user command (:lock)", changedStates);
            return true;
        }
        finally { _gate.Release(); NotifyStateChanged(changedStates); }
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

    private async Task BuildSessionAsync(CancellationToken ct, List<VaultState> changedStates)
    {
        try
        {
            LastError = null;
            var cli = new BitwardenCli(_settings.BwExePathOverride, _cliDataDirectory);
            if (!cli.IsAvailable())
            {
                LastError = "Bitwarden CLI ('bw') not found on PATH.";
                Transition(VaultState.BwMissing, changedStates);
                return;
            }

            var credentials = GetEffectiveCredentials();
            if (credentials is null || !credentials.IsComplete)
            {
                LastError = "API client_id and client_secret are required in the plugin settings.";
                Transition(VaultState.LoggedOut, changedStates);
                return;
            }
            await cli.AuthenticateAsync(NormalizeServerUrl(_settings.ServerUrl), credentials, ct);

            // Pre-warm the daemon; unlocking then uses the existing local HTTP client.
            _serve = new BitwardenServeProcess(cli.ExePath, cli.DataDirectory);
            _serve.Start();
            _client = new BitwardenClient(_serve.BaseUri);
            if (!await WaitForServeReadyAsync(ct))
            {
                LastError = "bw serve failed to come up within the readiness deadline.";
                DisposeRuntime();
                Transition(VaultState.Failed, changedStates);
                return;
            }

            Transition(VaultState.Locked, changedStates);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            DisposeRuntime();
            LastError = "Bitwarden initialization was cancelled.";
            Transition(VaultState.Failed, changedStates);
            throw;
        }
        catch (Exception ex)
        {
            DisposeRuntime();
            LastError = ex is BitwardenException
                ? ex.Message
                : "Failed to initialize the plugin's Bitwarden CLI. Check its configured path and restart PowerToys.";
            Log.Warn(LastError, typeof(VaultSession));
            Transition(VaultState.Failed, changedStates);
        }
    }

    private ApiCredentials? GetEffectiveCredentials()
    {
        if (!string.IsNullOrWhiteSpace(_settings.ClientId) || !string.IsNullOrWhiteSpace(_settings.ClientSecret))
            return new ApiCredentials(_settings.ClientId, _settings.ClientSecret);

        // Both blank means the settings UI has migrated the pair to DPAPI storage.
        // A partially entered replacement must never silently reuse the old pair.
        return _credentialStore?.Load();
    }

    // Polls /status with exponential backoff until the daemon answers anything other than "unreachable".
    private async Task<bool> WaitForServeReadyAsync(CancellationToken ct)
    {
        if (_client is null) return false;
        var delay = ServeReadyInitialPoll;
        var deadline = DateTime.UtcNow + ServeReadyDeadline;
        while (DateTime.UtcNow < deadline)
        {
            var status = await _client.GetStatusAsync(ct);
            if (status == BwStatus.Locked) return true;
            if (status != BwStatus.Unreachable)
                throw new BitwardenException("The local CLI did not start with the plugin's locked login. Reapply the plugin settings.");
            await Task.Delay(delay, ct);
            if (delay < ServeReadyMaxPoll)
                delay = TimeSpan.FromMilliseconds(
                    Math.Min(delay.TotalMilliseconds * 1.5, ServeReadyMaxPoll.TotalMilliseconds));
        }
        return false;
    }

    // Returns true on success, false if listing failed (already logged). Callers on the
    // unlock and user-sync paths react to false; the periodic background timer ignores it.
    private async Task<bool> RefreshCacheAsync(
        BitwardenClient client,
        CancellationToken ct,
        Func<bool>? canApply = null)
    {
        try
        {
            var items = await client.ListItemsAsync(ct);
            var fresh = items
                .Where(i => i.Type == 1 && i.DeletedDate is null)
                .Select(i => new VaultEntry(
                    i.Id,
                    i.Name,
                    i.Login?.Username,
                    i.Login?.Uris is { } uris
                        ? uris.Where(u => !string.IsNullOrEmpty(u.Uri)).Select(u => u.Uri!).ToArray()
                        : Array.Empty<string>()))
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (canApply is not null && !canApply()) return false;
            _itemsCache = fresh;
            Log.Info($"Vault cache refreshed: {fresh.Count} login items.", typeof(VaultSession));
            return true;
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
            LastError = ex is BitwardenException ? ex.Message : "Load vault items failed: unexpected plugin error.";
            Log.Warn(LastError, typeof(VaultSession));
            return false;
        }
    }

    private void StartSyncTimer()
    {
        StopSyncTimer();
        _syncTimer = new System.Threading.Timer(async _ =>
        {
            if (Interlocked.Exchange(ref _syncTimerRunning, 1) == 1) return;
            var acquired = false;
            try
            {
                acquired = await _gate.WaitAsync(0);
                if (!acquired) return;
                var client = _client;
                if (_state != VaultState.Ready || client is null) return;
                await client.SyncAsync(CancellationToken.None);
                if (_state == VaultState.Ready && ReferenceEquals(client, _client))
                {
                    await RefreshCacheAsync(
                        client,
                        CancellationToken.None,
                        canApply: () => _state == VaultState.Ready && ReferenceEquals(client, _client));
                }
            }
            catch (Exception ex) { Log.Exception("Periodic sync failed", ex, typeof(VaultSession)); }
            finally
            {
                if (acquired) _gate.Release();
                Interlocked.Exchange(ref _syncTimerRunning, 0);
            }
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
        var changedStates = new List<VaultState>();
        try
        {
            if (_state != VaultState.Ready) return;
            if (DateTime.UtcNow - _lastActivityUtc < timeout) return;
            await LockInternalAsync($"Auto-lock after {timeout.TotalMinutes:F0} min idle.", changedStates);
        }
        catch (Exception ex) { Log.Exception("Auto-lock tick failed", ex, typeof(VaultSession)); }
        finally { NotifyStateChanged(changedStates); }
    }

    private void OnWindowsSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is not (SessionSwitchReason.SessionLock
            or SessionSwitchReason.ConsoleDisconnect
            or SessionSwitchReason.RemoteDisconnect)) return;
        if (_state != VaultState.Ready) return;
        // Fire-and-forget. The session-switch callback fires on the SystemEvents thread
        // and we don't want to block it.
        _ = Task.Run(async () =>
        {
            var changedStates = new List<VaultState>();
            await LockInternalAsync($"Windows session {e.Reason}.", changedStates);
            NotifyStateChanged(changedStates);
        });
    }

    private async Task LockInternalAsync(string reason, List<VaultState> changedStates)
    {
        // Take the gate so we can't race an in-flight unlock or reconfigure. Short timeout.
        // If something else holds the gate, that operation will leave the vault in its
        // own intended state; we don't need to stomp on it.
        if (!await _gate.WaitAsync(LockGateTimeout)) return;
        try { await LockHoldingGateAsync(reason, changedStates); }
        finally { _gate.Release(); }
    }

    // Caller must already hold _gate. Centralises the lock POST + state transition so
    // both auto/system-triggered (timeout-gated) and user-triggered (wait-indefinitely)
    // entry points produce identical end states.
    private async Task LockHoldingGateAsync(string reason, List<VaultState> changedStates)
    {
        if (_state != VaultState.Ready) return;
        Log.Info($"Locking vault: {reason}", typeof(VaultSession));
        if (_client is not null)
        {
            try { await _client.LockAsync(CancellationToken.None); }
            catch (Exception ex) { Log.Exception("Lock POST failed", ex, typeof(VaultSession)); }
        }
        Transition(VaultState.Locked, changedStates);
    }

    private void Touch() => _lastActivityUtc = DateTime.UtcNow;

    private void Transition(VaultState next, List<VaultState> changedStates)
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
            changedStates.Add(next);
        }
    }

    private void NotifyStateChanged(List<VaultState> changedStates)
    {
        foreach (var state in changedStates)
        {
            try { StateChanged?.Invoke(state); }
            catch (Exception ex) { Log.Exception("StateChanged handler threw", ex, typeof(VaultSession)); }
        }
    }

    private void DisposeRuntime()
    {
        StopAutoLockTimer();
        StopSyncTimer();
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
        // In-flight async operations still release the gate in their finally blocks.
        // No WaitHandle is allocated; let GC reclaim the semaphore after they finish.
    }
}
