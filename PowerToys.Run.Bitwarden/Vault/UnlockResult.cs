namespace PowerToys.Run.Bitwarden.Vault;

internal sealed record UnlockResult(bool Success, string? Error)
{
    public static readonly UnlockResult Unlocked = new(true, null);
    public static UnlockResult Failed(string error) => new(false, error);
}
