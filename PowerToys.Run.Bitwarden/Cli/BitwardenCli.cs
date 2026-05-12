using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Wox.Plugin.Logger;

namespace PowerToys.Run.Bitwarden.Cli;

internal sealed class BitwardenCli
{
    // Env var names the bw CLI itself recognises
    private const string ClientIdEnvVar = "BW_CLIENTID";
    private const string ClientSecretEnvVar = "BW_CLIENTSECRET";

    public string ExePath { get; }

    public BitwardenCli(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            ExePath = overridePath!;
        }
        else
        {
            ExePath = ResolveFromPath() ?? "bw";
        }
    }

    private static string? ResolveFromPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            foreach (var name in new[] { "bw.exe", "bw.cmd", "bw" })
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { /* skip malformed PATH entries */ }
            }
        }
        return null;
    }

    public bool IsAvailable() => File.Exists(ExePath);

    public async Task<bool> ConfigServerAsync(string url, CancellationToken ct)
    {
        var (rc, _, stderr) = await RunAsync(new[] { "config", "server", url }, env: null, ct);
        if (rc != 0) Log.Warn($"bw config server failed: {stderr}", typeof(BitwardenCli));
        return rc == 0;
    }

    public async Task<string> StatusAsync(CancellationToken ct)
    {
        var (rc, stdout, _) = await RunAsync(["status"], env: null, ct);
        if (rc != 0) return BwStatus.Unauthenticated;
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.TryGetProperty("status", out var s))
                return s.GetString() ?? BwStatus.Unauthenticated;
        }
        catch { /* fall through */ }
        return BwStatus.Unauthenticated;
    }

    public async Task<bool> LoginApiKeyAsync(string clientId, string clientSecret, CancellationToken ct)
    {
        var env = new Dictionary<string, string>
        {
            [ClientIdEnvVar] = clientId,
            [ClientSecretEnvVar] = clientSecret,
        };
        var (rc, _, stderr) = await RunAsync(["login", "--apikey"], env, ct);
        if (rc != 0) Log.Warn($"bw login --apikey failed: {stderr}", typeof(BitwardenCli));
        return rc == 0;
    }

    private async Task<(int rc, string stdout, string stderr)> RunAsync(
        string[] args,
        IDictionary<string, string>? env,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ExePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null)
        {
            foreach (var kv in env)
                psi.Environment[kv.Key] = kv.Value;
        }

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        finally
        {
            // Drop our references to any sensitive env values now that the child has its
            // own copy. psi is local but the strings would otherwise linger on the heap
            // until GC takes psi out of scope.
            if (env != null)
                foreach (var kv in env)
                    psi.Environment.Remove(kv.Key);
        }
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return (proc.ExitCode, await stdoutTask, await stderrTask);
    }
}
