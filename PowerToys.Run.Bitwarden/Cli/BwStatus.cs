namespace PowerToys.Run.Bitwarden.Cli;

// String constants for `bw status` / `bw serve GET /status`. The first three come
// from bw; "unreachable" is synthetic, used for HTTP failures against bw serve.
internal static class BwStatus
{
    public const string Unauthenticated = "unauthenticated";
    public const string Locked = "locked";
    public const string Unlocked = "unlocked";
    public const string Unreachable = "unreachable";
}
