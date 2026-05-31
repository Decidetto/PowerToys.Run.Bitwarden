using PowerToys.Run.Bitwarden.Security;
using System.IO;
using Xunit;

namespace PowerToys.Run.Bitwarden.Tests;

public sealed class ApiCredentialStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bw-api-creds-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Save_WhenCalledConcurrently_PersistsCredentials()
    {
        var store = new ApiCredentialStore(_dir);
        var credentials = new ApiCredentials("client-id", "client-secret");

        var saves = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => Assert.True(store.Save(credentials))));

        await Task.WhenAll(saves);

        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal(credentials.ClientId, loaded.ClientId);
        Assert.Equal(credentials.ClientSecret, loaded.ClientSecret);
        Assert.Empty(Directory.EnumerateFiles(_dir, "api-credentials.json.*.tmp"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
