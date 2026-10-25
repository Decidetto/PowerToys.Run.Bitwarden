namespace PowerToys.Run.Bitwarden.Cli;

// Only controlled text is safe to display or log. Never retain raw output or inner
// exceptions: CLI responses can contain passwords, tokens, and vault data.
internal sealed class BitwardenException : Exception
{
    public BitwardenException(string message) : base(message) { }

    public static BitwardenException Failure(string operation, string? explanation, string code)
        => new($"{operation} failed ({code}). {Explain(explanation)}");

    internal static string Explain(string? message)
    {
        // Map upstream explanations to safe text. Redacting arbitrary text cannot
        // reliably cover every possible secret; unknown errors retain operation/code.
        var text = message?.Trim() ?? string.Empty;
        if (text.Contains("KeyIdBackfillError", StringComparison.Ordinal) ||
            text.Equals("API call failed during user key id backfill", StringComparison.Ordinal))
            return "The CLI's user-key-ID migration failed. Check CLI/server compatibility; Vaultwarden 1.37.3 lacks the required endpoint.";
        if (text.Equals("Internal Server Error", StringComparison.OrdinalIgnoreCase))
            return "An internal CLI error occurred. Check compatibility between your Bitwarden CLI and vault server.";
        if (text.Equals("Invalid master password.", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("Invalid password.", StringComparison.OrdinalIgnoreCase))
            return "The CLI rejected the master password.";
        if (text.Equals("No active account found", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("You are not logged in.", StringComparison.OrdinalIgnoreCase))
            return "The CLI has no active login. Reapply the plugin's API credentials.";
        if (text.Equals("Vault is locked.", StringComparison.OrdinalIgnoreCase))
            return "The CLI reports that the vault is still locked.";
        if (text.Equals("invalid_client", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("Invalid client", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("Invalid API key.", StringComparison.OrdinalIgnoreCase))
            return "The server rejected the API credentials. Check the plugin's client ID, secret, and server.";
        if (text.Contains("The decryption operation failed", StringComparison.OrdinalIgnoreCase))
            return "The CLI could not decrypt the vault. Check the master password and account configuration.";
        if (text.Contains("ECONNREFUSED", StringComparison.Ordinal) ||
            text.Contains("ENOTFOUND", StringComparison.Ordinal) ||
            text.Equals("fetch failed", StringComparison.OrdinalIgnoreCase))
            return "The CLI could not reach the server. Check the server URL and connection.";
        if (text.Contains("CERT_HAS_EXPIRED", StringComparison.Ordinal) ||
            text.Contains("UNABLE_TO_VERIFY_LEAF_SIGNATURE", StringComparison.Ordinal) ||
            text.Contains("SELF_SIGNED_CERT_IN_CHAIN", StringComparison.Ordinal))
            return "The CLI could not validate the server's TLS certificate.";
        return "The CLI returned an unrecognized error; its raw details were withheld to protect secrets.";
    }
}
