using System.IO;
using System.Text.Json;
using Wox.Plugin.Logger;

namespace PowerToys.Run.Bitwarden.Vault;

// Per-item pick counter, persisted to disk. Items the user picks more often score
// higher and float to the top of search results, including the empty-query case
// where the match-score component is zero for everyone. Pure frequency, no recency
// decay (keep it simple).
internal sealed class UsageStats : IDisposable
{
    // Cap on the boost a single item can earn from pick frequency. Prevents a very
    // heavily-picked item from outranking a clean name-prefix match (1000) on a
    // different item: picks always influence ordering, never override relevance.
    public const int MaxBoost = 50;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    private readonly string _path;
    private readonly Lock _lock = new();
    private Dictionary<string, int> _counts = [];
    private bool _dirty;
    private readonly Timer _flushTimer;

    public UsageStats(string filePath)
    {
        _path = filePath;
        Load();
        _flushTimer = new Timer(_ => FlushIfDirty(), null, FlushInterval, FlushInterval);
    }

    public int GetBoost(string id)
    {
        lock (_lock)
        {
            return _counts.TryGetValue(id, out var c) ? Math.Min(c, MaxBoost) : 0;
        }
    }

    public void Increment(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        lock (_lock)
        {
            _counts[id] = (_counts.TryGetValue(id, out var c) ? c : 0) + 1;
            _dirty = true;
        }
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
            if (loaded != null) _counts = loaded;
        }
        catch (Exception ex)
        {
            Log.Exception("UsageStats.Load failed", ex, typeof(UsageStats));
        }
    }

    private void FlushIfDirty()
    {
        Dictionary<string, int> snapshot;
        lock (_lock)
        {
            if (!_dirty) return;
            snapshot = new Dictionary<string, int>(_counts);
            _dirty = false;
        }
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // Write-then-rename so a crash mid-write can't corrupt the live file.
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Exception("UsageStats.Flush failed", ex, typeof(UsageStats));
        }
    }

    public void Dispose()
    {
        _flushTimer.Dispose();
        FlushIfDirty();
    }
}
