namespace PowerToys.Run.Bitwarden.Vault;

internal sealed record VaultEntry(string Id, string Name, string? Username, string? FirstUri);
