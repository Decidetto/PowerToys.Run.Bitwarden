using Microsoft.PowerToys.Settings.UI.Library;
using Xunit;

namespace PowerToys.Run.Bitwarden.Tests;

public class PluginSettingsTests
{
    [Fact]
    public void Diff_IdenticalSettings_DetectsNoChange()
    {
        var s = new PluginSettings("https://vault.bitwarden.com", "id", "secret", 30, null);
        var diff = PluginSettings.Diff(s, s with { });
        Assert.False(diff.AuthChanged);
        Assert.False(diff.AutoLockChanged);
    }

    [Fact]
    public void Diff_AuthFieldsTriggerAuthChange()
    {
        var prev = new PluginSettings("https://a", "id1", "sec1", 30, null);

        Assert.True(PluginSettings.Diff(prev, prev with { ServerUrl = "https://b" }).AuthChanged);
        Assert.True(PluginSettings.Diff(prev, prev with { ClientId = "id2" }).AuthChanged);
        Assert.True(PluginSettings.Diff(prev, prev with { ClientSecret = "sec2" }).AuthChanged);
        Assert.True(PluginSettings.Diff(prev, prev with { BwExePathOverride = "/usr/local/bin/bw" }).AuthChanged);
    }

    [Fact]
    public void Diff_AutoLockOnlyChange_DoesNotTriggerAuthChange()
    {
        var prev = new PluginSettings("https://a", "id", "sec", 30, null);
        var next = prev with { AutoLockMinutes = 5 };
        var diff = PluginSettings.Diff(prev, next);
        Assert.False(diff.AuthChanged);
        Assert.True(diff.AutoLockChanged);
    }

    [Fact]
    public void From_NullInput_ReturnsEmpty()
    {
        Assert.Equal(PluginSettings.Empty, PluginSettings.From(null));
    }

    [Fact]
    public void From_RoundTripsViaBuildAdditionalOptions()
    {
        var original = new PluginSettings("https://example.com", "my_id", "my_secret", 7, "C:\\bw.exe");
        var options = original.BuildAdditionalOptions();
        var parsed = PluginSettings.From(options);
        Assert.Equal(original, parsed);
    }

    [Fact]
    public void From_EmptyStringServerUrl_ParsesAsNull()
    {
        var options = new[]
        {
            new PluginAdditionalOption { Key = "ServerUrl", TextValue = string.Empty },
            new PluginAdditionalOption { Key = "ClientId", TextValue = "id" },
            new PluginAdditionalOption { Key = "ClientSecret", TextValue = "sec" },
            new PluginAdditionalOption { Key = "AutoLockMinutes", NumberValue = 10 },
            new PluginAdditionalOption { Key = "BwExePathOverride", TextValue = string.Empty },
        };
        var parsed = PluginSettings.From(options);
        Assert.Null(parsed.ServerUrl);
        Assert.Null(parsed.BwExePathOverride);
        Assert.Equal("id", parsed.ClientId);
        Assert.Equal(10, parsed.AutoLockMinutes);
    }

    [Fact]
    public void From_MissingAutoLockKey_UsesDefault()
    {
        var options = new[]
        {
            new PluginAdditionalOption { Key = "ClientId", TextValue = "id" },
            new PluginAdditionalOption { Key = "ClientSecret", TextValue = "sec" },
        };
        var parsed = PluginSettings.From(options);
        Assert.Equal(30, parsed.AutoLockMinutes);
    }
}
