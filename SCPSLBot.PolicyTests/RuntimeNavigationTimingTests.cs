using SCPSLBot.Navigation.Runtime;

namespace SCPSLBot.PolicyTests;

public sealed class RuntimeNavigationTimingTests : IDisposable
{
    public RuntimeNavigationTimingTests() => RuntimeNavigationTiming.Reset();
    public void Dispose() => RuntimeNavigationTiming.Reset();

    [Fact]
    public void UnobservedScopesHaveNoClockOrHandlerState()
    {
        var scope = RuntimeNavigationTiming.Begin("SourceCollection", 42);
        Assert.Null(scope.Observers);
        Assert.Equal(0, scope.StartTimestamp);
        scope.Complete(100, false);
        Assert.Equal(0, RuntimeNavigationTiming.FailedObservers);
    }

    [Fact]
    public void ObservedScopesReportCorrelatableReadOnlySamples()
    {
        int count = 0;
        RuntimeNavigationTiming.Sampled += (operation, frame, timestamp, elapsed, sources, changed) =>
        {
            count++;
            Assert.Equal("SourceCollection", operation);
            Assert.Equal(42, frame);
            Assert.True(timestamp > 0);
            Assert.True(elapsed >= 0 && double.IsFinite(elapsed));
            Assert.Equal(1200, sources);
            Assert.True(changed);
        };
        RuntimeNavigationTiming.Begin("SourceCollection", 42).Complete(1200, true);
        Assert.Equal(1, count);
        Assert.Equal(0, RuntimeNavigationTiming.FailedObservers);
    }

    [Fact]
    public void FaultingObserverIsRemovedAndDoesNotStopOtherObservers()
    {
        int bad = 0, good = 0;
        RuntimeNavigationTiming.Sampled += (_, _, _, _, _, _) => { bad++; throw new InvalidOperationException(); };
        RuntimeNavigationTiming.Sampled += (_, _, _, _, _, _) => good++;
        RuntimeNavigationTiming.Begin("ReconcileStart", 4).Complete(20, false);
        RuntimeNavigationTiming.Begin("ReconcileStart", 5).Complete(20, false);
        Assert.Equal(1, bad);
        Assert.Equal(2, good);
        Assert.Equal(1, RuntimeNavigationTiming.FailedObservers);
    }

    [Fact]
    public void ObserverCapacityRejectsAnEntireExcessRegistration()
    {
        int called = 0;
        Action<string, int, long, double, int, bool> observer = (_, _, _, _, _, _) => called++;
        for (int i = 0; i < RuntimeNavigationTiming.MaximumObservers; i++) RuntimeNavigationTiming.Sampled += observer;
        Assert.Throws<InvalidOperationException>(() => RuntimeNavigationTiming.Sampled += observer);
        RuntimeNavigationTiming.Begin("SourceCollection", 9).Complete(20, false);
        Assert.Equal(RuntimeNavigationTiming.MaximumObservers, called);
    }

    [Fact]
    public void DisposedAndResetObserversDoNotReceivePendingSamples()
    {
        int calls = 0;
        Action<string, int, long, double, int, bool> observer = (_, _, _, _, _, _) => calls++;
        RuntimeNavigationTiming.Sampled += observer;
        var removed = RuntimeNavigationTiming.Begin("SourceCollection", 2);
        RuntimeNavigationTiming.Sampled -= observer;
        removed.Complete(20, false);
        RuntimeNavigationTiming.Sampled += observer;
        var stale = RuntimeNavigationTiming.Begin("SourceCollection", 3);
        RuntimeNavigationTiming.Reset();
        RuntimeNavigationTiming.Sampled += observer;
        stale.Complete(20, false);
        Assert.Equal(0, calls);
        RuntimeNavigationTiming.Begin("SourceCollection", 4).Complete(20, false);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ResetDuringDispatchEndsTheOldLifecycle()
    {
        int second = 0;
        RuntimeNavigationTiming.Sampled += (_, _, _, _, _, _) => RuntimeNavigationTiming.Reset();
        RuntimeNavigationTiming.Sampled += (_, _, _, _, _, _) => second++;
        RuntimeNavigationTiming.Begin("SourceCollection", 3).Complete(20, false);
        Assert.Equal(0, second);
        Assert.Null(RuntimeNavigationTiming.Begin("SourceCollection", 4).Observers);
    }
}
