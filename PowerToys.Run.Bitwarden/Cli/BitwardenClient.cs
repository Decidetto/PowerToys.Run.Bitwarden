using PowerToys.Run.Bitwarden.Security;
using PowerToys.Run.Bitwarden.Vault;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security;
using System.Text.Json;

namespace PowerToys.Run.Bitwarden.Cli;

internal sealed class BitwardenClient : IDisposable
{
    private static readonly TimeSpan HttpRequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _http;

    public BitwardenClient(Uri baseUri)
    {
        _http = new HttpClient { BaseAddress = baseUri, Timeout = HttpRequestTimeout };
    }

    public async Task<string> GetStatusAsync(CancellationToken ct)
    {
        try
        {
            var env = await _http.GetFromJsonAsync<Envelope<StatusData>>("status", JsonOptions, ct);
            return env?.Data?.Template?.Status ?? BwStatus.Unreachable;
        }
        catch
        {
            return BwStatus.Unreachable;
        }
    }

    public async Task<ItemSummary[]> ListItemsAsync(string search, CancellationToken ct)
    {
        var path = string.IsNullOrEmpty(search)
            ? "list/object/items"
            : $"list/object/items?search={Uri.EscapeDataString(search)}";
        var env = await _http.GetFromJsonAsync<Envelope<ListData<ItemSummary>>>(path, JsonOptions, ct);
        return env?.Data?.Data ?? [];
    }

    public async Task<string?> GetPasswordAsync(string id, CancellationToken ct)
    {
        var env = await _http.GetFromJsonAsync<Envelope<ItemDetail>>($"object/item/{id}", JsonOptions, ct);
        return env?.Data?.Login?.Password;
    }

    public async Task<bool> SyncAsync(CancellationToken ct)
    {
        using var resp = await _http.PostAsync("sync", content: null, ct);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> LockAsync(CancellationToken ct)
    {
        using var resp = await _http.PostAsync("lock", content: null, ct);
        return resp.IsSuccessStatusCode;
    }

    public Task<bool> UnlockAsync(SecureString masterPassword, CancellationToken ct)
    {
        return PasswordHelper.UseAsString(masterPassword, async pw =>
        {
            using var resp = await _http.PostAsJsonAsync("unlock", new { password = pw }, ct);
            return resp.IsSuccessStatusCode;
        });
    }

    public void Dispose() => _http.Dispose();
}
