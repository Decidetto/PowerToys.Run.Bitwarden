namespace PowerToys.Run.Bitwarden.Security;

internal sealed record ApiCredentials(string ClientId, string ClientSecret)
{
    public bool IsComplete => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
