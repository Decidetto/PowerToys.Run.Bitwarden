namespace PowerToys.Run.Bitwarden.Cli;

internal interface IBitwardenCli
{
    string ExePath { get; }
    bool IsAvailable();
    Task<bool> ConfigServerAsync(string url, CancellationToken ct);
    Task<string> StatusAsync(CancellationToken ct);
    Task<bool> LoginApiKeyAsync(string clientId, string clientSecret, CancellationToken ct);
}
