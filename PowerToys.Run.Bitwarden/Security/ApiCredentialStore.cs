using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wox.Plugin.Logger;

namespace PowerToys.Run.Bitwarden.Security;

internal sealed class ApiCredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PowerToys.Run.Bitwarden.ApiCredentials.v1");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string _path;

    public ApiCredentialStore(string dataDir)
    {
        _path = Path.Combine(dataDir, "api-credentials.json");
    }

    public bool Exists => File.Exists(_path);

    public ApiCredentials? Load()
    {
        try
        {
            lock (_gate)
            {
                if (!File.Exists(_path)) return null;
                var json = File.ReadAllText(_path);
                var file = JsonSerializer.Deserialize<CredentialFile>(json);
                if (file is null) return null;
                var clientId = Unprotect(file.ClientId);
                var clientSecret = Unprotect(file.ClientSecret);
                var creds = new ApiCredentials(clientId, clientSecret);
                return creds.IsComplete ? creds : null;
            }
        }
        catch (Exception ex)
        {
            Log.Exception("ApiCredentialStore.Load failed", ex, typeof(ApiCredentialStore));
            return null;
        }
    }

    public bool Save(ApiCredentials credentials)
    {
        if (!credentials.IsComplete) return false;
        string? tmp = null;
        try
        {
            lock (_gate)
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var now = DateTimeOffset.UtcNow;
                var existing = ReadMetadata();
                var file = new CredentialFile(
                    Protect(credentials.ClientId),
                    Protect(credentials.ClientSecret),
                    existing?.CreatedUtc ?? now,
                    now);

                tmp = Path.Combine(
                    dir ?? string.Empty,
                    $"{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
                File.WriteAllText(tmp, JsonSerializer.Serialize(file, JsonOptions));
                File.Move(tmp, _path, overwrite: true);
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Exception("ApiCredentialStore.Save failed", ex, typeof(ApiCredentialStore));
            return false;
        }
        finally
        {
            TryDeleteTempFile(tmp);
        }
    }

    public void Delete()
    {
        try
        {
            lock (_gate)
            {
                if (File.Exists(_path)) File.Delete(_path);
            }
        }
        catch (Exception ex)
        {
            Log.Exception("ApiCredentialStore.Delete failed", ex, typeof(ApiCredentialStore));
        }
    }

    private CredentialFile? ReadMetadata()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<CredentialFile>(File.ReadAllText(_path))
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TryDeleteTempFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A stale temp file is harmless; the next successful save writes a fresh one.
        }
    }

    private static string Protect(string value)
    {
        var plain = Encoding.UTF8.GetBytes(value);
        var protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(plain);
        return Convert.ToBase64String(protectedBytes);
    }

    private static string Unprotect(string protectedValue)
    {
        var protectedBytes = Convert.FromBase64String(protectedValue);
        var plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private sealed record CredentialFile(
        string ClientId,
        string ClientSecret,
        DateTimeOffset CreatedUtc,
        DateTimeOffset UpdatedUtc);
}
