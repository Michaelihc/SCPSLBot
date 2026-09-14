using SCPSLBot.Warmup.Controls.Policy;

namespace SCPSLBot.PolicyTests;

public sealed class PendingPanelSelectionStoreTests
{
    [Fact]
    public void SelectionIsOnlyReturnedForMatchingExplicitAction()
    {
        PendingPanelSelectionStore store = new();
        store.Stage(7, "user@steam", PendingPanelAction.Role, "ChaosRifleman");

        Assert.True(store.TryGet(7, "user@steam", PendingPanelAction.Role, out string role));
        Assert.Equal("ChaosRifleman", role);
        Assert.False(store.TryGet(7, "user@steam", PendingPanelAction.Item, out _));
    }

    [Fact]
    public void ReusedPlayerIdCannotReadPreviousIdentitySelection()
    {
        PendingPanelSelectionStore store = new();
        store.Stage(7, "old@steam", PendingPanelAction.Item, "native.guncom15");

        Assert.False(store.TryGet(7, "new@steam", PendingPanelAction.Item, out _));
    }

    [Fact]
    public void StagingAReplacementKeepsOnlyLatestValue()
    {
        PendingPanelSelectionStore store = new();
        store.Stage(7, "user@steam", PendingPanelAction.Arena, "surface");
        store.Stage(7, "user@steam", PendingPanelAction.Arena, "lcz");

        Assert.True(store.TryGet(7, "user@steam", PendingPanelAction.Arena, out string arena));
        Assert.Equal("lcz", arena);
    }

    [Fact]
    public void ReadingForAButtonPressDoesNotConsumeVisibleSelection()
    {
        PendingPanelSelectionStore store = new();
        store.Stage(7, "user@steam", PendingPanelAction.Item, "native.medkit");

        Assert.True(store.TryGet(7, "user@steam", PendingPanelAction.Item, out string first));
        Assert.True(store.TryGet(7, "user@steam", PendingPanelAction.Item, out string second));
        Assert.Equal("native.medkit", first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void ClearAndDisconnectForgetPendingActions()
    {
        PendingPanelSelectionStore store = new();
        store.Stage(7, "user@steam", PendingPanelAction.Role, "ClassD");
        store.Stage(7, "user@steam", PendingPanelAction.Item, "native.medkit");
        store.Clear(7, "user@steam", PendingPanelAction.Role);

        Assert.False(store.TryGet(7, "user@steam", PendingPanelAction.Role, out _));
        Assert.True(store.TryGet(7, "user@steam", PendingPanelAction.Item, out _));

        store.ForgetPlayer(7);
        Assert.False(store.TryGet(7, "user@steam", PendingPanelAction.Item, out _));
    }
}
