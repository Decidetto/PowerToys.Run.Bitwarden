using System.Diagnostics;
using System.IO;
using System.Text.Json;
using PowerToys.Run.Bitwarden.Security;
using System.ComponentModel;

namespace PowerToys.Run.Bitwarden.Cli;

internal sealed class BitwardenCli
{
    // Env var names the bw CLI itself recognises
    private const string ClientIdEnvVar = "BW_CLIENTID";
    private const string ClientSecretEnvVar = "BW_CLIENTSECRET";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    public string ExePath { get; }
    public string DataDirectory { get; }

    public BitwardenCli(string? overridePath, string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        DataDirectory = Path.GetFullPath(dataDirectory);
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
        if (!File.Exists(path)) return path;

        if (ext.Length == 0) return path;
        if (ext.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            var cmdShim = Path.ChangeExtension(path, ".cmd");
            if (File.Exists(cmdShim)) return cmdShim;
        }

        return path;
    }

    public bool IsAvailable() => File.Exists(ExePath);

    // Always replace only the plugin-owned login. No persisted fingerprint or
    // ambient CLI status may bypass authentication with the configured credentials.
    public async Task AuthenticateAsync(string serverUrl, ApiCredentials credentials, CancellationToken ct)
    {
        if (!credentials.IsComplete)
            throw new BitwardenException("API client_id and client_secret are required in the plugin settings.");
        Directory.CreateDirectory(DataDirectory);
        if (await StatusAsync(ct) != BwStatus.Unauthenticated)
            await ExecuteAsync(["logout"], "Replace plugin CLI login", null, ct);
        await ExecuteAsync(["config", "server", serverUrl], "Configure Bitwarden server", null, ct);
        var env = new Dictionary<string, string>
        {
            [ClientIdEnvVar] = credentials.ClientId,
            [ClientSecretEnvVar] = credentials.ClientSecret,
        };
        await ExecuteAsync(["login", "--apikey"], "API login", env, ct);
        if (await StatusAsync(ct) != BwStatus.Locked)
            throw new BitwardenException("API login did not leave the plugin CLI in the expected locked state.");
    }

    private async Task ExecuteAsync(string[] args, string operation, IDictionary<string, string>? env, CancellationToken ct)
    {
        var (rc, _, stderr) = await RunAsync(args, env, ct);
        if (rc != 0) throw BitwardenException.Failure(operation, stderr, $"exit {rc}");
    }

    private async Task<string> StatusAsync(CancellationToken ct)
    {
        var (rc, stdout, stderr) = await RunAsync(["status"], null, ct);
        if (rc != 0) throw BitwardenException.Failure("Read CLI status", stderr, $"exit {rc}");
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String &&
                status.GetString() is { } value &&
                value is BwStatus.Unauthenticated or BwStatus.Locked or BwStatus.Unlocked)
                return value;
        }
        catch (JsonException) { }
        throw new BitwardenException("Read CLI status failed: the CLI returned an invalid response.");
    }

    private async Task<(int rc, string stdout, string stderr)> RunAsync(
        string[] args,
        IDictionary<string, string>? env,
        CancellationToken ct)
    {
        var psi = BitwardenProcess.CreateStartInfo(ExePath, args, DataDirectory);
        if (env != null)
        {
            foreach (var kv in env)
                psi.Environment[kv.Key] = kv.Value;
        }

        using var proc = new Process { StartInfo = psi };
        ct.ThrowIfCancellationRequested();
        try { proc.Start(); }
        catch (Win32Exception)
        {
            throw new BitwardenException("Could not start the Bitwarden CLI. Check the plugin's CLI path.");
        }
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
            throw new BitwardenException($"Bitwarden CLI {args[0]} timed out after {CommandTimeout.TotalSeconds:F0} seconds.");
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
