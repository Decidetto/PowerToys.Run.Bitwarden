using PowerToys.Run.Bitwarden.Native;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Wox.Plugin.Logger;

namespace PowerToys.Run.Bitwarden.Cli;

internal sealed class BitwardenServeProcess : IDisposable
{
    private const string LoopbackHost = "127.0.0.1";

    private readonly string _exePath;
    private readonly string _dataDirectory;
    private Process? _proc;
    private ChildProcessJob? _job;

    public int Port { get; private set; }
    public Uri BaseUri => new($"http://{LoopbackHost}:{Port}/");

    // bw serve starts in whatever auth state the CLI's data.json reflects (typically
    // "locked" after `bw login`). Callers then POST /unlock to move it to unlocked,
    // avoiding a separate `bw unlock` subprocess.
    public BitwardenServeProcess(string exePath, string dataDirectory)
    {
        _exePath = exePath;
        _dataDirectory = dataDirectory;
    }

    public void Start()
    {
        Port = PickFreeLoopbackPort();

        var psi = BitwardenProcess.CreateStartInfo(_exePath,
        [
            "serve",
            "--hostname",
            LoopbackHost,
            "--port",
            Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ], _dataDirectory);

        _job = new ChildProcessJob();
        _proc = new Process { StartInfo = psi };
        // Drain output without persisting it. Upstream diagnostics can include
        // credentials or decrypted data; request failures are surfaced by the client.
        _proc.OutputDataReceived += (_, _) => { };
        _proc.ErrorDataReceived += (_, _) => { };
        _proc.Start();
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();
        _job.AssignProcess(_proc);

        Log.Debug($"bw serve PID={_proc.Id} command={BitwardenProcess.Describe(psi)} listening on {LoopbackHost}:{Port}", typeof(BitwardenServeProcess));
    }

    private static int PickFreeLoopbackPort()
    {
        // Asking the kernel for port 0 returns a guaranteed-free ephemeral port.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    public void Dispose()
    {
        try { _proc?.Kill(entireProcessTree: true); } catch { /* ignore */ }
        _proc?.Dispose();
        _proc = null;
        // Releasing the job handle is the safety net. Kernel kills any process still in it.
        _job?.Dispose();
        _job = null;
    }
}
