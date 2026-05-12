using PowerToys.Run.Bitwarden.Vault;
using System.IO;
using Xunit;

namespace PowerToys.Run.Bitwarden.Tests;

public class UsageStatsTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"bw-usage-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        if (File.Exists(_path + ".tmp")) File.Delete(_path + ".tmp");
    }

    [Fact]
    public void GetBoost_UnknownId_ReturnsZero()
    {
        using var stats = new UsageStats(_path);
        Assert.Equal(0, stats.GetBoost("nope"));
    }

    [Fact]
    public void Increment_BumpsBoost()
    {
        using var stats = new UsageStats(_path);
        stats.Increment("a");
        stats.Increment("a");
        stats.Increment("a");
        Assert.Equal(3, stats.GetBoost("a"));
    }

    [Fact]
    public void GetBoost_CapsAtMaxBoost()
    {
        using var stats = new UsageStats(_path);
        for (var i = 0; i < UsageStats.MaxBoost * 2; i++) stats.Increment("a");
        Assert.Equal(UsageStats.MaxBoost, stats.GetBoost("a"));
    }

    [Fact]
    public void Increment_IgnoresEmptyId()
    {
        using var stats = new UsageStats(_path);
        stats.Increment(string.Empty);
        stats.Increment(null!);
        Assert.Equal(0, stats.GetBoost(string.Empty));
    }

    [Fact]
    public void Counts_PersistAcrossInstances()
    {
        using (var stats = new UsageStats(_path))
        {
            stats.Increment("a");
            stats.Increment("a");
            stats.Increment("b");
        }
        // Dispose flushed to disk; new instance must see the prior counts.
        using var reloaded = new UsageStats(_path);
        Assert.Equal(2, reloaded.GetBoost("a"));
        Assert.Equal(1, reloaded.GetBoost("b"));
    }

    [Fact]
    public void Load_MissingFile_StartsEmpty()
    {
        // No file exists yet at _path; constructor shouldn't throw.
        using var stats = new UsageStats(_path);
        Assert.Equal(0, stats.GetBoost("anything"));
    }
}
