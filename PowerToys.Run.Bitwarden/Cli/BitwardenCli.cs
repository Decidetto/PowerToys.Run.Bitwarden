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
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    public string ExePath { get; }

    public BitwardenCli(string? overridePath)
    {
        var normalizedOverride = NormalizeOverridePath(overridePath);
        if (normalizedOverride is not null)
        {
            ExePath = normalizedOverride;
        }
        else
        {
            ExePath = ResolveFromPath(Environment.GetEnvironmentVariable("PATH") ?? string.Empty) ?? "bw";
        }
    }

    internal static string? ResolveFromPath(string path)
    {
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            foreach (var name in new[] { "bw.exe", "bw.cmd", "bw.bat", "bw" })
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim().Trim('"'), name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { /* skip malformed PATH entries */ }
            }
        }
        return null;
    }

    private static string? NormalizeOverridePath(string? overridePath)
    {
        if (string.IsNullOrWhiteSpace(overridePath)) return null;

        var path = overridePath.Trim().Trim('"');
        var ext = Path.GetExtension(path);
        if (ext.Length == 0)
        {
            foreach (var shimExt in new[] { ".exe", ".cmd", ".bat" })
            {
                var shim = path + shimExt;
                if (File.Exists(shim)) return shim;
            }
        }
        if (!File.Exists(path)) return null;

        if (ext.Length == 0) return path;
        if (ext.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            var cmdShim = Path.ChangeExtension(path, ".cmd");
            if (File.Exists(cmdShim)) return cmdShim;
        }

        return path;
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
        var psi = BitwardenProcess.CreateStartInfo(ExePath, args);
        if (env != null)
        {
            foreach (var kv in env)
                psi.Environment[kv.Key] = kv.Value;
        }

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        using var timeoutCts = new CancellationTokenSource(CommandTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        try
        {
            await proc.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); }
            catch { /* process may have exited after timeout fired */ }
            try { await proc.WaitForExitAsync(CancellationToken.None); }
            catch { /* best-effort cleanup */ }
            Log.Warn($"bw command timed out after {CommandTimeout.TotalSeconds:F0}s: {BitwardenProcess.Describe(psi)}", typeof(BitwardenCli));
            return (-1, string.Empty, "Timed out waiting for bw command to exit.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); }
            catch { /* process may have exited after cancellation fired */ }
            throw;
        }

        return (proc.ExitCode, await stdoutTask, await stderrTask);
    }
}
