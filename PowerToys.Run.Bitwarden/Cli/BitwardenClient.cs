using PowerToys.Run.Bitwarden.Security;
using PowerToys.Run.Bitwarden.Vault;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security;
using System.Text.Json;
using Wox.Plugin.Logger;

namespace PowerToys.Run.Bitwarden.Cli;

internal sealed class BitwardenClient : IDisposable
{
    private static readonly TimeSpan HttpRequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public BitwardenClient(Uri baseUri)
        : this(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }, baseUri) { }

    internal BitwardenClient(HttpMessageHandler handler, Uri baseUri)
    {
        _http = new HttpClient(handler) { BaseAddress = baseUri, Timeout = HttpRequestTimeout };
    }

    public async Task<string> GetStatusAsync(CancellationToken ct)
    {
        try
        {
            var data = await RequestAsync<StatusData>(HttpMethod.Get, "status", "Read CLI status", null, ct);
            return data?.Template?.Status ?? BwStatus.Unreachable;
        }
        catch (BitwardenException) { return BwStatus.Unreachable; }
    }

    public async Task<ItemSummary[]> ListItemsAsync(CancellationToken ct)
    {
        var data = await RequestAsync<ListData<ItemSummary>>(HttpMethod.Get, "list/object/items", "Load vault items", null, ct);
        return data?.Data ?? throw new BitwardenException("Load vault items failed: the CLI returned an invalid response.");
    }

    public async Task<string?> GetPasswordAsync(string id, CancellationToken ct)
    {
        var data = await RequestAsync<ItemDetail>(HttpMethod.Get, $"object/item/{id}", "Read vault item", null, ct);
        return data?.Login?.Password;
    }

    public Task SyncAsync(CancellationToken ct)
        => RequestAsync<JsonElement>(HttpMethod.Post, "sync", "Sync vault", null, ct);

    public Task LockAsync(CancellationToken ct)
        => RequestAsync<JsonElement>(HttpMethod.Post, "lock", "Lock vault", null, ct);

    public async Task UnlockAsync(SecureString masterPassword, CancellationToken ct)
    {
        try
        {
            await PasswordHelper.UseAsString(masterPassword, pw =>
                RequestAsync<JsonElement>(HttpMethod.Post, "unlock", "Unlock vault",
                    JsonContent.Create(new { password = pw }), ct));
        }
        catch (BitwardenException ex) when (ex.IsCliInternalError)
        {
            // The CLI runs its encrypted-data migrations after the master password has
            // already unlocked the vault, and a migration failure escapes the route as a
            // plain HTTP 500. Servers without the user-key-ID backfill endpoint hit this
            // once per 24-hour cooldown. Wrong passwords fail earlier with HTTP 400.
            if (await GetStatusAsync(ct) != BwStatus.Unlocked) throw;
            Log.Warn("The CLI unlocked the vault but a post-unlock step failed (HTTP 500); continuing.", typeof(BitwardenClient));
        }
    }

    private async Task<T?> RequestAsync<T>(
        HttpMethod method, string path, string operation, HttpContent? content, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path) { Content = content };
            using var response = await _http.SendAsync(request, ct);
            var code = $"HTTP {(int)response.StatusCode}";
            // Only the top-level message is an error explanation. Never display data:
            // successful unlock responses include the session key.
            try
            {
                var envelope = await response.Content.ReadFromJsonAsync<Envelope<JsonElement>>(JsonOptions, ct);
                if (!response.IsSuccessStatusCode || envelope?.Success != true)
                    throw BitwardenException.Failure(operation, envelope?.Message, code, (int)response.StatusCode >= 500);
                return envelope.Data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                    ? default
                    : envelope.Data.Deserialize<T>(JsonOptions);
            }
            catch (JsonException)
            {
                // Koa returns plain text when an unhandled CLI exception escapes the
                // route (including post-unlock migrations on incompatible servers).
                // The HTTP failure is meaningful even though it has no JSON envelope.
                var internalError = (int)response.StatusCode >= 500;
                var reason = internalError
                    ? "an internal CLI error occurred. Check compatibility between your Bitwarden CLI and vault server."
                    : "the CLI returned an invalid response.";
                throw new BitwardenException($"{operation} failed ({code}): {reason}") { IsCliInternalError = internalError };
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new BitwardenException($"{operation} timed out waiting for the CLI. Try again.");
        }
        catch (HttpRequestException)
        {
            throw new BitwardenException($"{operation} failed: unable to communicate with the local CLI. Restart PowerToys.");
        }
    }

    public void Dispose() => _http.Dispose();
}
