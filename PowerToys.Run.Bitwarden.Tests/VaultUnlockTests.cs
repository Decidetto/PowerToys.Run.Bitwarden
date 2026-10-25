using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using PowerToys.Run.Bitwarden.Cli;
using PowerToys.Run.Bitwarden.Security;
using PowerToys.Run.Bitwarden.Vault;
using Xunit;

namespace PowerToys.Run.Bitwarden.Tests;

public sealed class VaultUnlockTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bw-unlock-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _requests = [];

    [Theory]
    [InlineData("unlock", "The CLI rejected the master password.", "unlock,lock")]
    [InlineData("sync", "Sync vault failed", "unlock,sync,lock")]
    [InlineData("list/object/items", "Load vault items failed", "unlock,sync,list/object/items,lock")]
    public async Task Failure_PreservesStageAndRelocks(string failedPath, string expected, string requests)
    {
        using var session = Session(failedPath);
        using var password = BitwardenClientTests.Password();
        var result = await session.UnlockWithPasswordAsync(password, default);
        Assert.False(result.Success);
        Assert.Contains(expected, result.Error);
        Assert.Equal(result.Error, session.LastError);
        Assert.Equal(VaultState.Locked, session.State);
        Assert.Empty(session.Search(""));
        Assert.Equal(requests, string.Join(",", _requests));
    }

    [Fact]
    public async Task RelockFailure_DisposesClientAndFailsSession()
    {
        using var session = Session("sync", failLock: true);
        using var password = BitwardenClientTests.Password();
        var result = await session.UnlockWithPasswordAsync(password, default);
        Assert.Contains("Sync vault failed", result.Error);
        Assert.Equal(VaultState.Failed, session.State);
        Assert.Null(ClientField.GetValue(session));
        Assert.Empty(session.Search(""));
    }

    [Fact]
    public async Task Success_RequiresUnlockSyncAndValidItems()
    {
        using var session = Session(null);
        using var password = BitwardenClientTests.Password();
        var result = await session.UnlockWithPasswordAsync(password, default);
        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.Null(session.LastError);
        Assert.Equal(VaultState.Ready, session.State);
        Assert.Equal("unlock,sync,list/object/items", string.Join(",", _requests));
    }

    [Fact]
    public async Task MissingDaemon_IsNotReportedAsWrongPassword()
    {
        using var session = new VaultSession(_directory);
        using var password = BitwardenClientTests.Password();
        var result = await session.UnlockWithPasswordAsync(password, default);
        Assert.False(result.Success);
        Assert.Contains("not running", result.Error);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task PartialReplacement_DoesNotFallBackToStoredCredentials()
    {
        var store = new ApiCredentialStore(_directory);
        Assert.True(store.Save(new("old-dummy-client", "old-dummy-secret")));
        var fakeExe = Path.Combine(_directory, "bw.cmd");
        File.WriteAllText(fakeExe, "@echo off\r\nexit /b 99\r\n");
        using var session = new VaultSession(_directory, store);
        await session.InitializeAsync(PluginSettings.Empty with { ClientId = "new-dummy-client", BwExePathOverride = fakeExe }, default);
        Assert.Equal(VaultState.LoggedOut, session.State);
        Assert.Contains("required", session.LastError);
        Assert.False(Directory.Exists(Path.Combine(_directory, "cli")));
    }

    // Inject only the local HTTP transport into an otherwise real session. No CLI
    // processes, user's data directory, or credentials are accessed by these tests.
    private static readonly FieldInfo ClientField = typeof(VaultSession).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private VaultSession Session(string? failedPath, bool failLock = false)
    {
        var session = new VaultSession(_directory);
        var handler = new BitwardenClientTests.Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            _requests.Add(path);
            var failed = path == failedPath || (path == "lock" && failLock);
            var body = failed
                ? """{"success":false,"message":"Invalid master password."}"""
                : path == "list/object/items"
                    ? """{"success":true,"data":{"data":[]}}"""
                    : """{"success":true,"data":{}}""";
            return new HttpResponseMessage(failed ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        });
        ClientField.SetValue(session, new BitwardenClient(handler, new Uri("http://127.0.0.1/")));
        return session;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
