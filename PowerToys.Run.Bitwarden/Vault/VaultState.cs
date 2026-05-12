namespace PowerToys.Run.Bitwarden.Vault;

internal enum VaultState
{
    Uninitialized,
    Initializing,
    Ready,
    Locked,
    LoggedOut,
    BwMissing,
    Failed,
}
