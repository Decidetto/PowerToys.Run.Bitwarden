using System.Net;
using System.Net.Http;
using System.Security;
using System.Text;
using PowerToys.Run.Bitwarden.Cli;
using Xunit;

namespace PowerToys.Run.Bitwarden.Tests;

public class BitwardenClientTests
{
    [Theory]
    [InlineData(400, "Invalid master password.", "rejected the master password")]
    [InlineData(400, "No active account found", "no active login")]
    [InlineData(403, "Forbidden", "HTTP 403")]
    [InlineData(500, "Internal Server Error", "HTTP 500")]
    [InlineData(200, "Vault is locked.", "still locked")]
    public async Task Unlock_SurfacesOperationAndSafeExplanation(int status, string message, string expected)
    {
        using var client = Client((HttpStatusCode)status,
            System.Text.Json.JsonSerializer.Serialize(new { success = false, message }));
        using var password = Password();
        var error = await Assert.ThrowsAsync<BitwardenException>(() => client.UnlockAsync(password, default));
        Assert.Contains("Unlock vault failed", error.Message);
        Assert.Contains(expected, error.Message);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(500, "<html>dummy-password secret-token</html>")]
    [InlineData(400, """{"success":false,"message":"dummy-password secret-token","data":{"raw":"session-token"}}""")]
    [InlineData(200, """{"success":false,"message":"dummy-password"}""")]
    [InlineData(200, "null")]
    public async Task Unlock_DoesNotExposeRawErrorsOrSecrets(int status, string body)
    {
        using var client = Client((HttpStatusCode)status, body);
        using var password = Password();
        var error = await Assert.ThrowsAsync<BitwardenException>(() => client.UnlockAsync(password, default));
        Assert.DoesNotContain("dummy-password", error.ToString());
        Assert.DoesNotContain("secret-token", error.ToString());
        Assert.DoesNotContain("session-token", error.ToString());
        Assert.Contains($"HTTP {status}", error.Message);
    }

    [Theory]
    [InlineData("Internal Server Error")]
    [InlineData("<html>secret-token</html>")]
    public async Task Unlock_PlainText500IsAnInternalCliError(string body)
    {
        using var client = Client(HttpStatusCode.InternalServerError, body);
        using var password = Password();
        var error = await Assert.ThrowsAsync<BitwardenException>(() => client.UnlockAsync(password, default));
        Assert.Contains("HTTP 500", error.Message);
        Assert.Contains("internal CLI error", error.Message);
        Assert.DoesNotContain("invalid response", error.Message);
        Assert.DoesNotContain("secret-token", error.ToString());
    }

    [Theory]
    [InlineData("unlocked", true)]
    [InlineData("locked", false)]
    public async Task Unlock_PostUnlockMigrationFailureSucceedsOnlyWhenCliIsUnlocked(string status, bool succeeds)
    {
        // bw serve unlocks first, then a failing migration escapes the route as a plain 500.
        using var client = new BitwardenClient(new Handler(request => request.RequestUri!.AbsolutePath == "/unlock"
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("Internal Server Error") }
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(
                    new { success = true, data = new { template = new { status } } }), Encoding.UTF8, "application/json")
            }), new Uri("http://127.0.0.1/"));
        using var password = Password();
        if (succeeds)
            await client.UnlockAsync(password, default);
        else
            Assert.Contains("HTTP 500", (await Assert.ThrowsAsync<BitwardenException>(() => client.UnlockAsync(password, default))).Message);
    }

    [Fact]
    public async Task Unlock_RejectedPasswordIsNotRescuedByUnlockedStatus()
    {
        using var client = new BitwardenClient(new Handler(request => request.RequestUri!.AbsolutePath == "/unlock"
            ? new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"success":false,"message":"Invalid master password."}""", Encoding.UTF8, "application/json")
            }
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"success":true,"data":{"template":{"status":"unlocked"}}}""", Encoding.UTF8, "application/json")
            }), new Uri("http://127.0.0.1/"));
        using var password = Password();
        await Assert.ThrowsAsync<BitwardenException>(() => client.UnlockAsync(password, default));
    }

    [Fact]
    public async Task Unlock_SuccessDoesNotSurfaceSessionKey()
    {
        using var client = Client(HttpStatusCode.OK,
            """{"success":true,"data":{"raw":"fake-session-key","message":"fake-session-key"}}""");
        using var password = Password();
        await client.UnlockAsync(password, default);
    }

    [Fact]
    public async Task Unlock_ConnectionFailureIsNotAPasswordError()
    {
        using var client = new BitwardenClient(new Handler(_ => throw new HttpRequestException("secret diagnostic")),
            new Uri("http://127.0.0.1/"));
        using var password = Password();
        var error = await Assert.ThrowsAsync<BitwardenException>(() => client.UnlockAsync(password, default));
        Assert.Contains("unable to communicate", error.Message);
        Assert.DoesNotContain("secret diagnostic", error.ToString());
    }

    [Fact]
    public async Task Unlock_TimeoutIsDistinguishedFromCallerCancellation()
    {
        using var client = new BitwardenClient(new Handler(_ => throw new TaskCanceledException("secret diagnostic")),
            new Uri("http://127.0.0.1/"));
        using var password = Password();
        var error = await Assert.ThrowsAsync<BitwardenException>(() => client.UnlockAsync(password, default));
        Assert.Contains("timed out", error.Message);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.UnlockAsync(password, cancellation.Token));
    }

    [Fact]
    public async Task ListItems_DoesNotTreatFailureAsAnEmptyVault()
    {
        using var client = Client(HttpStatusCode.OK, """{"success":false,"message":"Vault is locked."}""");
        var error = await Assert.ThrowsAsync<BitwardenException>(() => client.ListItemsAsync(default));
        Assert.Contains("Load vault items", error.Message);
        Assert.Contains("still locked", error.Message);
    }

    [Fact]
    public async Task ListItems_MissingDataIsAnError()
    {
        using var client = Client(HttpStatusCode.OK, """{"success":true}""");
        await Assert.ThrowsAsync<BitwardenException>(() => client.ListItemsAsync(default));
    }

    [Fact]
    public async Task ListItems_ErrorDataShapeDoesNotHideExplanation()
    {
        using var client = Client(HttpStatusCode.BadRequest,
            """{"success":false,"message":"Vault is locked.","data":"opaque-dummy-secret"}""");
        var error = await Assert.ThrowsAsync<BitwardenException>(() => client.ListItemsAsync(default));
        Assert.Contains("still locked", error.Message);
        Assert.DoesNotContain("opaque-dummy-secret", error.ToString());
    }

    [Fact]
    public async Task Sync_SurfacesItsOwnFailure()
    {
        using var client = Client(HttpStatusCode.BadRequest, """{"success":false,"message":"You are not logged in."}""");
        var error = await Assert.ThrowsAsync<BitwardenException>(() => client.SyncAsync(default));
        Assert.Contains("Sync vault", error.Message);
        Assert.Contains("no active login", error.Message);
    }

    internal static BitwardenClient Client(HttpStatusCode status, string body)
        => new(new Handler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }), new Uri("http://127.0.0.1/"));

    internal static SecureString Password()
    {
        var password = new SecureString();
        foreach (var c in "dummy-password") password.AppendChar(c);
        return password;
    }

    internal sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
}
