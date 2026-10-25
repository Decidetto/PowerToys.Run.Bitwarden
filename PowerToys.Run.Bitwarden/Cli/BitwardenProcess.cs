using System.Diagnostics;
using System.IO;

namespace PowerToys.Run.Bitwarden.Cli;

internal static class BitwardenProcess
{
    public static ProcessStartInfo CreateStartInfo(string exePath, IEnumerable<string> args, string dataDirectory)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        ConfigureEnvironment(psi, dataDirectory);

        if (IsCommandShim(exePath))
        {
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            // cmd.exe does not parse quote characters escaped by ProcessStartInfo.ArgumentList
            // the same way native argv consumers do. Give cmd its /c payload as a raw
            // command line so npm's bw.cmd shim can be launched from paths with spaces.
            psi.Arguments = $"/d /s /c {BuildCmdCommand(exePath, args)}";
        }
        else
        {
            psi.FileName = exePath;
            foreach (var arg in args) psi.ArgumentList.Add(arg);
        }

        return psi;
    }

    internal static void ConfigureEnvironment(ProcessStartInfo psi, string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        // A parent shell's session, credentials, or CLI flags must never choose the
        // plugin's account. This applies equally to short commands and bw serve.
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("BW_", StringComparison.OrdinalIgnoreCase)).ToArray())
            psi.Environment.Remove(key);
        psi.Environment["BITWARDENCLI_APPDATA_DIR"] = Path.GetFullPath(dataDirectory);
        psi.Environment["BW_NOINTERACTION"] = "true";
    }

    public static string Describe(ProcessStartInfo psi)
    {
        if (!string.IsNullOrEmpty(psi.Arguments)) return psi.FileName + " " + psi.Arguments;
        return psi.FileName + " " + string.Join(" ", psi.ArgumentList.Select(QuoteForLog));
    }

    private static bool IsCommandShim(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildCmdCommand(string exePath, IEnumerable<string> args)
    {
        var command = QuoteForCmd(exePath);
        foreach (var arg in args) command += " " + QuoteForCmd(arg);
        return "\"" + command + "\"";
    }

    private static string QuoteForCmd(string value)
    {
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static string QuoteForLog(string value)
    {
        return value.Any(char.IsWhiteSpace) ? "\"" + value + "\"" : value;
    }
}
