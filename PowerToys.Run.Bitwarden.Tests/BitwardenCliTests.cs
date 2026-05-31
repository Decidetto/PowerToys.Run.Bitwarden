using PowerToys.Run.Bitwarden.Cli;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace PowerToys.Run.Bitwarden.Tests;

public class BitwardenCliTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "bw-cli-tests-" + Guid.NewGuid().ToString("N"));

    public BitwardenCliTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void ResolveFromPath_PrefersNativeExeOverNpmCommandShim()
    {
        var exe = Touch("bw.exe");
        Touch("bw.cmd");
        Touch("bw");

        Assert.Equal(exe, BitwardenCli.ResolveFromPath(_tempDir));
    }

    [Fact]
    public void ResolveFromPath_UsesNpmCommandShimBeforeExtensionlessShim()
    {
        var cmd = Touch("bw.cmd");
        Touch("bw");

        Assert.Equal(cmd, BitwardenCli.ResolveFromPath(_tempDir));
    }

    [Fact]
    public void Constructor_OverrideWithoutExtensionUsesSiblingCommandShim()
    {
        var extensionless = Touch("bw");
        var cmd = Touch("bw.cmd");

        var cli = new BitwardenCli(extensionless);

        Assert.Equal(cmd, cli.ExePath);
    }

    [Fact]
    public void CreateStartInfo_WrapsCommandShimInCmdExe()
    {
        var psi = BitwardenProcess.CreateStartInfo(@"C:\Users\Test User\AppData\Roaming\npm\bw.cmd", ["serve", "--port", "8080"]);

        Assert.EndsWith("cmd.exe", psi.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(psi.ArgumentList);
        Assert.Equal("/d /s /c \"\"C:\\Users\\Test User\\AppData\\Roaming\\npm\\bw.cmd\" \"serve\" \"--port\" \"8080\"\"", psi.Arguments);
    }

    [Fact]
    public async Task CreateStartInfo_ExecutesCommandShimWithSpacesAndArguments()
    {
        var shimDir = Path.Combine(_tempDir, "npm path");
        Directory.CreateDirectory(shimDir);
        var shim = Path.Combine(shimDir, "bw.cmd");
        File.WriteAllText(shim, """
            @echo off
            echo %~1^|%~2^|%~3
            """);

        var psi = BitwardenProcess.CreateStartInfo(shim, ["serve", "--port", "8080"]);
        using var proc = new Process { StartInfo = psi };

        proc.Start();
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        Assert.Equal(0, proc.ExitCode);
        Assert.Equal("serve|--port|8080", stdout.Trim());
        Assert.Equal(string.Empty, stderr.Trim());
    }

    private string Touch(string fileName)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }
}
