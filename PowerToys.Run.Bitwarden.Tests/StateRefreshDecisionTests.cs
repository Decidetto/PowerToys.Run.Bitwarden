using PowerToys.Run.Bitwarden.Vault;
using Xunit;

namespace PowerToys.Run.Bitwarden.Tests;

public class StateRefreshDecisionTests
{
    private const string Keyword = "bw";
    private const string DefaultQuery = "bw ";

    // (1) Post-unlock path: pending query trumps everything else, including PT Run being
    //     hidden (the unlock dialog hides PT Run while the user types their password).
    [Fact]
    public void Unlocked_WithPending_RestoresPending_EvenWhenPtHidden()
    {
        var (refresh, query) = StateRefreshDecision.Decide(
            VaultState.Ready, hasBeenReady: true, hasFinishedInitialBoot: true,
            pendingUnlockQuery: "bw github", currentPtRunInput: null,
            actionKeyword: Keyword, defaultQuery: DefaultQuery);
        Assert.True(refresh);
        Assert.Equal("bw github", query);
    }

    [Fact]
    public void Unlocked_WithPending_RestoresPending_EvenWhenInputUnrelated()
    {
        var (refresh, query) = StateRefreshDecision.Decide(
            VaultState.Ready, hasBeenReady: true, hasFinishedInitialBoot: true,
            pendingUnlockQuery: "bw github", currentPtRunInput: "calc 1+1",
            actionKeyword: Keyword, defaultQuery: DefaultQuery);
        Assert.True(refresh);
        Assert.Equal("bw github", query);
    }

    // (2) Trigger transitions WITHOUT pending: gated on user being in our context.

    [Fact]
    public void InitialBoot_InContext_Refreshes()
    {
        var (refresh, query) = StateRefreshDecision.Decide(
            VaultState.Locked, hasBeenReady: false, hasFinishedInitialBoot: false,
            pendingUnlockQuery: null, currentPtRunInput: "bw ",
            actionKeyword: Keyword, defaultQuery: DefaultQuery);
        Assert.True(refresh);
        Assert.Equal(DefaultQuery, query);
    }

    [Fact]
    public void InitialBoot_OutOfContext_DoesNotClobber()
    {
        var (refresh, _) = StateRefreshDecision.Decide(
            VaultState.Locked, hasBeenReady: false, hasFinishedInitialBoot: false,
            pendingUnlockQuery: null, currentPtRunInput: "calc 1+1",
            actionKeyword: Keyword, defaultQuery: DefaultQuery);
        Assert.False(refresh);
    }

    [Fact]
    public void InitialBoot_PtRunHidden_DoesNotRefresh()
    {
        var (refresh, _) = StateRefreshDecision.Decide(
            VaultState.Locked, hasBeenReady: false, hasFinishedInitialBoot: false,
            pendingUnlockQuery: null, currentPtRunInput: null,
            actionKeyword: Keyword, defaultQuery: DefaultQuery);
        Assert.False(refresh);
    }

    [Fact]
    public void AutoLockFromReady_InContext_Refreshes()
    {
        var (refresh, query) = StateRefreshDecision.Decide(
            VaultState.Locked, hasBeenReady: true, hasFinishedInitialBoot: true,
            pendingUnlockQuery: null, currentPtRunInput: "bw github",
            actionKeyword: Keyword, defaultQuery: DefaultQuery);
        Assert.True(refresh);
        Assert.Equal(DefaultQuery, query);
    }

    [Fact]
    public void AutoLockFromReady_OutOfContext_DoesNotClobber()
    {
        var (refresh, _) = StateRefreshDecision.Decide(
            VaultState.Locked, hasBeenReady: true, hasFinishedInitialBoot: true,
            pendingUnlockQuery: null, currentPtRunInput: "calc 1+1",
            actionKeyword: Keyword, defaultQuery: DefaultQuery);
        Assert.False(refresh);
    }

    [Fact]
    public void Unlocked_WithoutPending_InContext_Refreshes()
    {
        var (refresh, query) = StateRefreshDecision.Decide(
            VaultState.Ready, hasBeenReady: true, hasFinishedInitialBoot: true,
            pendingUnlockQuery: null, currentPtRunInput: "bw something",
            actionKeyword: Keyword, defaultQuery: DefaultQuery);
        Assert.True(refresh);
        Assert.Equal(DefaultQuery, query);
    }

    // (3) Transitions that should NEVER fire a refresh, regardless of context.

    [Fact]
    public void IdleStateTransition_AfterBoot_DoesNotRefresh()
    {
        // hasFinishedInitialBoot=true, hasBeenReady=false, newState=Failed
        // -> not initial-boot-completing, not unlocked, not locking-from-ready.
        var (refresh, _) = StateRefreshDecision.Decide(
            VaultState.Failed, hasBeenReady: false, hasFinishedInitialBoot: true,
            pendingUnlockQuery: null, currentPtRunInput: "bw github",
            actionKeyword: Keyword, defaultQuery: DefaultQuery);
        Assert.False(refresh);
    }

    [Fact]
    public void StillInitializing_DoesNotRefresh()
    {
        var (refresh, _) = StateRefreshDecision.Decide(
            VaultState.Initializing, hasBeenReady: false, hasFinishedInitialBoot: false,
            pendingUnlockQuery: null, currentPtRunInput: "bw ",
            actionKeyword: Keyword, defaultQuery: DefaultQuery);
        Assert.False(refresh);
    }

    // (4) Keyword matching edge cases.

    [Fact]
    public void ContextMatch_IsCaseInsensitive()
    {
        var (refresh, _) = StateRefreshDecision.Decide(
            VaultState.Locked, hasBeenReady: false, hasFinishedInitialBoot: false,
            pendingUnlockQuery: null, currentPtRunInput: "BW Github",
            actionKeyword: Keyword, defaultQuery: DefaultQuery);
        Assert.True(refresh);
    }

    [Fact]
    public void ContextMatch_NoSpaceAfterKeyword_StillMatches()
    {
        // PT Run doesn't require a space after the keyword for routing.
        var (refresh, _) = StateRefreshDecision.Decide(
            VaultState.Locked, hasBeenReady: false, hasFinishedInitialBoot: false,
            pendingUnlockQuery: null, currentPtRunInput: "bwgithub",
            actionKeyword: Keyword, defaultQuery: DefaultQuery);
        Assert.True(refresh);
    }

    [Fact]
    public void EmptyKeyword_MatchesAnyInput_GlobalMode()
    {
        var (refresh, _) = StateRefreshDecision.Decide(
            VaultState.Locked, hasBeenReady: false, hasFinishedInitialBoot: false,
            pendingUnlockQuery: null, currentPtRunInput: "anything at all",
            actionKeyword: string.Empty, defaultQuery: string.Empty);
        Assert.True(refresh);
    }

    [Fact]
    public void InputIsPrefixOfKeyword_DoesNotMatch()
    {
        // User typed "b" — not enough to be in our context yet.
        var (refresh, _) = StateRefreshDecision.Decide(
            VaultState.Locked, hasBeenReady: false, hasFinishedInitialBoot: false,
            pendingUnlockQuery: null, currentPtRunInput: "b",
            actionKeyword: Keyword, defaultQuery: DefaultQuery);
        Assert.False(refresh);
    }
}
