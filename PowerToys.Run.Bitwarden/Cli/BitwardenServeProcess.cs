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
    private Process? _proc;
    private ChildProcessJob? _job;

    public int Port { get; private set; }
    public Uri BaseUri => new($"http://{LoopbackHost}:{Port}/");

    // bw serve starts in whatever auth state the CLI's data.json reflects (typically
    // "locked" after `bw login`). Callers then POST /unlock to move it to unlocked,
    // avoiding a separate `bw unlock` subprocess.
    public BitwardenServeProcess(string exePath)
    {
        _exePath = exePath;
    }

    public void Start()
    {
        Port = PickFreeLoopbackPort();

        var psi = new ProcessStartInfo
        {
            FileName = _exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("serve");
        psi.ArgumentList.Add("--hostname");
        psi.ArgumentList.Add(LoopbackHost);
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(Port.ToString(System.Globalization.CultureInfo.InvariantCulture));

        _job = new ChildProcessJob();
        _proc = new Process { StartInfo = psi };
        _proc.Start();
        _job.AssignProcess(_proc);

        Log.Debug($"bw serve PID={_proc.Id} listening on {LoopbackHost}:{Port}", typeof(BitwardenServeProcess));
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
