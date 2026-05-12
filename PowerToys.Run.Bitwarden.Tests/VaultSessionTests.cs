using PowerToys.Run.Bitwarden.Vault;
using Xunit;

namespace PowerToys.Run.Bitwarden.Tests;

public class VaultSessionTests
{
    [Theory]
    [InlineData("https://vault.bitwarden.com", "https://vault.bitwarden.com")]
    [InlineData("https://vault.bitwarden.com/", "https://vault.bitwarden.com")]
    [InlineData("http://localhost:8080", "http://localhost:8080")]
    [InlineData("bitwarden.com", "https://vault.bitwarden.com")]
    [InlineData("bitwarden.eu", "https://vault.bitwarden.eu")]
    [InlineData("BITWARDEN.EU", "https://vault.BITWARDEN.EU")]
    [InlineData("vault.example.com", "https://vault.example.com")]
    [InlineData("  https://vault.example.com  ", "https://vault.example.com")]
    public void NormalizeServerUrl_HandlesCommonInputs(string input, string expected)
    {
        Assert.Equal(expected, VaultSession.NormalizeServerUrl(input));
    }

    [Fact]
    public void ScoreMatch_PrefixOnName_BeatsSubstring()
    {
        var prefix = new VaultEntry("1", "GitHub", "u@x", null);
        var substring = new VaultEntry("2", "MyGitHub Backup", "u@x", null);
        Assert.True(VaultSession.ScoreMatch(prefix, "Git") > VaultSession.ScoreMatch(substring, "Git"));
    }

    [Fact]
    public void ScoreMatch_NameMatch_BeatsUsernameMatch()
    {
        var nameMatch = new VaultEntry("1", "GitHub", "other", null);
        var userMatch = new VaultEntry("2", "Random Item", "github_user", null);
        Assert.True(VaultSession.ScoreMatch(nameMatch, "github") > VaultSession.ScoreMatch(userMatch, "github"));
    }

    [Fact]
    public void ScoreMatch_NoMatch_ReturnsZero()
    {
        var entry = new VaultEntry("1", "GitHub", "u@x", null);
        Assert.Equal(0, VaultSession.ScoreMatch(entry, "completelyunrelated"));
    }

    [Fact]
    public void ScoreMatch_IsCaseInsensitive()
    {
        var entry = new VaultEntry("1", "GitHub", "u@x", null);
        Assert.True(VaultSession.ScoreMatch(entry, "GITHUB") > 0);
        Assert.True(VaultSession.ScoreMatch(entry, "github") > 0);
    }
}
