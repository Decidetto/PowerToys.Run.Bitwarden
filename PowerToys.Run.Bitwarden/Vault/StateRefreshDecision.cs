namespace PowerToys.Run.Bitwarden.Vault;

// Pure-logic decision for whether OnSessionStateChanged should force a PT Run requery,
// and what query to set if so. Split out from Main.cs so it can be tested without WPF.
//
// The decision exists to fix a UX bug: vault state transitions (initial boot finishing,
// auto-lock firing, etc.) used to unconditionally call ChangeQuery, which clobbers the
// user's input if they've moved on to another plugin's context. We gate the requery on
// "is the user currently in our context", which is determined from PT Run's input box
// text — captured by the caller and passed in here as `currentPtRunInput`.
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

        // Filter to transitions that actually need a forced refresh — others resolve
        // naturally on the user's next keystroke if/when they come back to us.
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
}
