namespace PowerToys.Run.Bitwarden.Vault;

// Pure-logic decision for whether OnSessionStateChanged should force a PT Run requery,
// and what query to set if so. Gates the requery on whether the user is currently in
// our context (determined from PT Run's input box text, passed in as `currentPtRunInput`)
// so background state transitions don't clobber a query they've typed for another plugin.
internal static class StateRefreshDecision
{
    // currentPtRunInput is null when PT Run is hidden or the input control couldn't be
    // read; that's treated as "not in our context" and suppresses the refresh.
    public static (bool refresh, string query) Decide(
        VaultState newState,
        bool hasBeenReady,
        bool hasFinishedInitialBoot,
        string? pendingUnlockQuery,
        string? currentPtRunInput,
        string actionKeyword,
        string defaultQuery)
    {
        var unlocked = newState == VaultState.Ready;

        // (1) Post-unlock with a pending query: always restore. The user explicitly
        //     initiated the unlock flow; PT Run is hidden during the password dialog,
        //     so the context check below would otherwise fail this case spuriously.
        if (unlocked && !string.IsNullOrEmpty(pendingUnlockQuery))
            return (true, pendingUnlockQuery!);

        // Filter to transitions that actually need a forced refresh.
        var initialBootCompleting = !hasFinishedInitialBoot
            && newState is not (VaultState.Uninitialized or VaultState.Initializing);
        var lockingFromReady = newState == VaultState.Locked && hasBeenReady;
        if (!initialBootCompleting && !unlocked && !lockingFromReady)
            return (false, string.Empty);

        // (2) Only requery if the user is currently looking at us. PT Run hidden or
        //     input not starting with our keyword -> different context -> leave it alone.
        if (currentPtRunInput is null) return (false, string.Empty);
        var inOurContext = currentPtRunInput.StartsWith(actionKeyword, StringComparison.OrdinalIgnoreCase);
        return inOurContext ? (true, defaultQuery) : (false, string.Empty);
    }

    // After an explicit user action (`:sync`, `:lock`), PT Run is reliably hidden by the
    // time the operation completes (Enter on the result returned true). The Decide path
    // above would skip the refresh on that null input.
    public static bool ShouldRefreshAfterExplicitAction(string? currentPtRunInput, string actionKeyword)
    {
        if (currentPtRunInput is null) return true;
        return currentPtRunInput.StartsWith(actionKeyword, StringComparison.OrdinalIgnoreCase);
    }
}
