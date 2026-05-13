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
        var prefix = new VaultEntry("1", "GitHub", "u@x", []);
        var substring = new VaultEntry("2", "MyGitHub Backup", "u@x", []);
        Assert.True(VaultSession.ScoreMatch(prefix, "Git") > VaultSession.ScoreMatch(substring, "Git"));
    }

    [Fact]
    public void ScoreMatch_NameMatch_BeatsUsernameMatch()
    {
        var nameMatch = new VaultEntry("1", "GitHub", "other", []);
        var userMatch = new VaultEntry("2", "Random Item", "github_user", []);
        Assert.True(VaultSession.ScoreMatch(nameMatch, "github") > VaultSession.ScoreMatch(userMatch, "github"));
    }

    [Fact]
    public void ScoreMatch_NoMatch_ReturnsZero()
    {
        var entry = new VaultEntry("1", "GitHub", "u@x", []);
        Assert.Equal(0, VaultSession.ScoreMatch(entry, "completelyunrelated"));
    }

    [Fact]
    public void ScoreMatch_IsCaseInsensitive()
    {
        var entry = new VaultEntry("1", "GitHub", "u@x", []);
        Assert.True(VaultSession.ScoreMatch(entry, "GITHUB") > 0);
        Assert.True(VaultSession.ScoreMatch(entry, "github") > 0);
    }

    [Fact]
    public void ScoreMatch_SpaceInsensitiveOnName()
    {
        // "mybank" against "My Bank Account" should match -- and at prefix tier since
        // space-stripping both sides makes the query a prefix of the stripped name.
        var spacey = new VaultEntry("1", "My Bank Account", null, []);
        var none = new VaultEntry("2", "Something Else", null, []);
        Assert.True(VaultSession.ScoreMatch(spacey, "mybank") > 0);
        Assert.True(VaultSession.ScoreMatch(spacey, "mybank") > VaultSession.ScoreMatch(none, "mybank"));
    }

    [Fact]
    public void ScoreMatch_SpaceInsensitiveOnQuery()
    {
        // "my bank" against "MyBank Account" should also match.
        var entry = new VaultEntry("1", "MyBank Account", null, []);
        Assert.True(VaultSession.ScoreMatch(entry, "my bank") > 0);
    }

    [Fact]
    public void ScoreMatch_MatchesUrl()
    {
        // Name and username carry no match; only the URL does.
        var entry = new VaultEntry("1", "Banking", "user@example.com", ["https://chase.com"]);
        Assert.True(VaultSession.ScoreMatch(entry, "chase") > 0);
    }

    [Fact]
    public void ScoreMatch_UrlNormalizationStripsSchemaAndWww()
    {
        // "github" should match as a prefix of the normalized URL.
        var entry = new VaultEntry("1", "Unrelated Name", null, ["https://www.github.com"]);
        var score = VaultSession.ScoreMatch(entry, "github");
        Assert.True(score > 0);
        // ...and "https" or "www" should not score, because we strip them.
        Assert.Equal(0, VaultSession.ScoreMatch(new VaultEntry("2", "x", null, ["https://www.github.com"]), "https"));
        Assert.Equal(0, VaultSession.ScoreMatch(new VaultEntry("3", "x", null, ["https://www.github.com"]), "www"));
    }

    [Fact]
    public void ScoreMatch_NamePrefixBeatsUrlMatch()
    {
        // When name AND URL both match, the name should dominate ordering.
        var nameMatch = new VaultEntry("1", "GitHub", null, []);
        var urlMatch = new VaultEntry("2", "Code Hosting", null, ["https://github.com"]);
        Assert.True(VaultSession.ScoreMatch(nameMatch, "github") > VaultSession.ScoreMatch(urlMatch, "github"));
    }

    [Fact]
    public void ScoreMatch_TakesMaxScoreAcrossMultipleUrls()
    {
        // Item with three URLs. Query "google" matches:
        //   - gmail.com: no match
        //   - accounts.google.com: substring
        //   - google.com: prefix
        // The item's URL score should match the prefix tier, equivalent to having only
        // the best-matching URL.
        var multi = new VaultEntry("1", "Email", null,
            ["https://gmail.com", "https://accounts.google.com", "https://google.com"]);
        var single = new VaultEntry("2", "Email", null, ["https://google.com"]);
        Assert.Equal(VaultSession.ScoreMatch(single, "google"), VaultSession.ScoreMatch(multi, "google"));
    }
}
