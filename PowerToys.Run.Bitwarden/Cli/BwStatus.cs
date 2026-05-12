namespace PowerToys.Run.Bitwarden.Cli;

// String constants for the three `bw status` / `bw serve GET /status` states,
// plus our own synthetic "unreachable" for HTTP failures. Keeps the magic strings
// in one place since they're matched in both BitwardenCli (CLI JSON) and
// BitwardenClient (HTTP response).
internal static class BwStatus
{
    public const string Unauthenticated = "unauthenticated";
    public const string Locked = "locked";
    public const string Unlocked = "unlocked";
    public const string Unreachable = "unreachable";
}
