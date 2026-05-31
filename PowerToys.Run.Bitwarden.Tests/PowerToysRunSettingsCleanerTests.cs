using PowerToys.Run.Bitwarden.Settings;
using System.IO;
using Xunit;

namespace PowerToys.Run.Bitwarden.Tests;

public class PowerToysRunSettingsCleanerTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "pt-run-settings-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void ClearPlaintextApiCredentials_ClearsOnlyBitwardenCredentialFields()
    {
        File.WriteAllText(_path, $$"""
        {
          "plugins": [
            {
              "Id": "{{Main.PluginID}}",
              "AdditionalOptions": [
                { "Key": "ServerUrl", "TextValue": "https://vault.example.com" },
                { "Key": "ClientId", "TextValue": "id" },
                { "Key": "ClientSecret", "TextValue": "secret" },
                { "Key": "BwExePathOverride", "TextValue": "C:\\bw.exe" }
              ]
            },
            {
              "Id": "other",
              "AdditionalOptions": [
                { "Key": "ClientSecret", "TextValue": "keep" }
              ]
            }
          ]
        }
        """);

        Assert.True(PowerToysRunSettingsCleaner.ClearPlaintextApiCredentials(_path, Main.PluginID));
        var cleaned = File.ReadAllText(_path);

        Assert.Contains("\"Key\": \"ClientId\"", cleaned);
        Assert.Contains("\"Key\": \"ClientSecret\"", cleaned);
        Assert.DoesNotContain("\"TextValue\": \"id\"", cleaned);
        Assert.DoesNotContain("\"TextValue\": \"secret\"", cleaned);
        Assert.Contains("\"TextValue\": \"https://vault.example.com\"", cleaned);
        Assert.Contains("\"TextValue\": \"C:\\\\bw.exe\"", cleaned);
        Assert.Contains("\"TextValue\": \"keep\"", cleaned);
    }

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch { /* best-effort cleanup */ }
    }
}
