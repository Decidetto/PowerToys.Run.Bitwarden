using System.Diagnostics;
using System.IO;
using PowerToys.Run.Bitwarden.Cli;
using PowerToys.Run.Bitwarden.Security;
using PowerToys.Run.Bitwarden.Vault;
using Xunit;

namespace PowerToys.Run.Bitwarden.Tests;

public sealed class BitwardenAuthenticationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bw-auth-tests-" + Guid.NewGuid().ToString("N"));

    public BitwardenAuthenticationTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData("locked", "status,logout,config,login,status")]
    [InlineData("unlocked", "status,logout,config,login,status")]
    [InlineData("unauthenticated", "status,config,login,status")]
    public async Task Authentication_AlwaysUsesConfiguredServerAndCredentials(string initial, string expected)
    {
        var cli = FakeCli(initial);
        await cli.AuthenticateAsync("https://vault.example.test", new("dummy-client", "dummy-secret"), default);
        Assert.Equal(expected, string.Join(",", File.ReadAllLines(Path.Combine(_directory, "calls.txt"))));
        Assert.Equal("https://vault.example.test", File.ReadAllText(Path.Combine(_directory, "server.txt")).Trim());
        Assert.Equal("correct", File.ReadAllText(Path.Combine(_directory, "credentials.txt")).Trim());
    }

    [Theory]
    [InlineData("status", "status")]
    [InlineData("logout", "status,logout")]
    [InlineData("config", "status,logout,config")]
    [InlineData("login", "status,logout,config,login")]
    public async Task Authentication_StopsOnEveryFailedStage(string stage, string expected)
    {
        var cli = FakeCli("locked", stage);
        var error = await Assert.ThrowsAsync<BitwardenException>(() =>
            cli.AuthenticateAsync("https://vault.example.test", new("dummy-client", "dummy-secret"), default));
        Assert.Contains("exit 7", error.Message);
        Assert.DoesNotContain("dummy-secret", error.ToString());
        Assert.Equal(expected, string.Join(",", File.ReadAllLines(Path.Combine(_directory, "calls.txt"))));
    }

    [Fact]
    public async Task Authentication_DoesNotTrustMalformedStatus()
    {
        var cli = FakeCli("unexpected");
        await Assert.ThrowsAsync<BitwardenException>(() =>
            cli.AuthenticateAsync("https://vault.example.test", new("dummy-client", "dummy-secret"), default));
        Assert.Equal(new[] { "status" }, File.ReadAllLines(Path.Combine(_directory, "calls.txt")));
    }

    [Fact]
    public async Task Authentication_MissingCredentialsCannotReuseExistingLogin()
    {
        var cli = FakeCli("locked");
        await Assert.ThrowsAsync<BitwardenException>(() =>
            cli.AuthenticateAsync("https://vault.example.test", new("", ""), default));
        Assert.False(File.Exists(Path.Combine(_directory, "calls.txt")));
    }

    [Fact]
    public void Environment_IsolatesProfileAndRemovesAmbientCredentials()
    {
        var psi = new ProcessStartInfo();
        psi.Environment["BW_SESSION"] = "dummy-session";
        psi.Environment["BW_CLIENTID"] = "ambient-client";
        psi.Environment["BW_CLIENTSECRET"] = "ambient-secret";
        psi.Environment["BW_PASSWORD"] = "ambient-password";
        psi.Environment["BW_NOINTERACTION"] = "false";
        psi.Environment["BITWARDENCLI_APPDATA_DIR"] = "unrelated-profile";
        BitwardenProcess.ConfigureEnvironment(psi, _directory);
        Assert.Equal(_directory, psi.Environment["BITWARDENCLI_APPDATA_DIR"]);
        Assert.Equal("true", psi.Environment["BW_NOINTERACTION"]);
        Assert.DoesNotContain("BW_SESSION", psi.Environment.Keys);
        Assert.DoesNotContain("BW_CLIENTID", psi.Environment.Keys);
        Assert.DoesNotContain("BW_CLIENTSECRET", psi.Environment.Keys);
        Assert.DoesNotContain("BW_PASSWORD", psi.Environment.Keys);
    }

    [Fact]
    public void MissingExplicitExecutable_DoesNotFallBackToPath()
    {
        var missing = Path.Combine(_directory, "missing.exe");
        var cli = new BitwardenCli(missing, _directory);
        Assert.Equal(missing, cli.ExePath);
        Assert.False(cli.IsAvailable());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ClearingServer_SelectsExplicitUsDefault(string? server)
        => Assert.Equal("https://vault.bitwarden.com", VaultSession.NormalizeServerUrl(server));

    private BitwardenCli FakeCli(string initial, string failedStage = "")
    {
        var path = Path.Combine(_directory, "bw.cmd");
        File.WriteAllText(path, $$"""
            @echo off
            echo %~1>>"%~dp0calls.txt"
            if "%~1"=="{{failedStage}}" (
              echo dummy-secret 1>&2
              exit /b 7
            )
            if "%~1"=="status" (
              if exist "%~dp0logged-in" (
                echo {"status":"locked"}
              ) else (
                echo {"status":"{{initial}}"}
              )
              exit /b 0
            )
            if "%~1"=="config" echo %~3>"%~dp0server.txt"
            if "%~1"=="login" (
              if not "%BW_CLIENTID%"=="dummy-client" exit /b 8
              if not "%BW_CLIENTSECRET%"=="dummy-secret" exit /b 9
              echo correct>"%~dp0credentials.txt"
              echo yes>"%~dp0logged-in"
            )
            exit /b 0
            """);
        return new BitwardenCli(path, Path.Combine(_directory, "plugin-profile"));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
