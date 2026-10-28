using System.Collections.Immutable;
using ExecutionLab;
using Xunit;

public sealed class OrderGateTests
{
    private static GateState Apply(GateState s, GateEvent e, long limit = 10)
    {
        var d = OrderGate.Apply(s, e, limit);
        Assert.True(d.Accepted, d.Reason);
        return d.State;
    }
    private static GateState Ready(long position = 6, long pending = 4)
    {
        var orders = pending == 0 ? ImmutableArray<SnapshotOrder>.Empty : [new SnapshotOrder("a", pending)];
        var s = Apply(GateState.Empty, new Snapshot("s", 1, position, orders));
        return Apply(s, new Resume("r", 2));
    }
    [Fact] public void InitialStateBlocksReservations() => Assert.False(OrderGate.Apply(GateState.Empty, new Reserve("a", 1, "a", 1), 10).Accepted);
    [Fact] public void SnapshotDoesNotResume() => Assert.True(Apply(GateState.Empty, new Snapshot("s", 8, 0, [])).Stopped);
    [Fact] public void FilledPlusPendingBlocksNewOrder()
    {
        var s = Ready(); var d = OrderGate.Apply(s, new Reserve("b", 3, "b", 1), 10);
        Assert.False(d.Accepted); Assert.Equal(10, d.State.ReservedExposure); Assert.Equal(3, d.State.Sequence);
    }
    [Fact] public void ExactLimitIsAllowed() => Assert.Equal(10, Apply(Ready(6, 3), new Reserve("b", 3, "b", 1)).ReservedExposure);
    [Fact] public void RiskDenialDoesNotCreateFalseSequenceGap()
    {
        var denied = OrderGate.Apply(Ready(), new Reserve("denied", 3, "b", 1), 10);
        var next = Apply(denied.State, new RequestCancel("cancel", 4, "a"));
        Assert.True(next.Reconciled); Assert.True(next.Orders["a"].CancelRequested);
    }
    [Fact] public void PartialFillPreservesReservedExposure()
    {
        var s = Apply(Ready(), new Fill("f", 3, "a", "exec-1", 1));
        Assert.Equal(7, s.Position); Assert.Equal(3, s.Pending); Assert.Equal(10, s.ReservedExposure);
    }
    [Fact] public void CancelRequestDoesNotReleaseExposure() => Assert.Equal(4, Apply(Ready(), new RequestCancel("c", 3, "a")).Pending);
    [Fact] public void FillDuringCancelPendingStillCounts()
    {
        var s = Apply(Ready(), new RequestCancel("c", 3, "a"));
        s = Apply(s, new Fill("f", 4, "a", "exec-1", 1));
        Assert.Equal(10, s.ReservedExposure); Assert.True(s.Orders["a"].CancelRequested);
        s = Apply(s, new ConfirmCancel("cc", 5, "a")); Assert.Equal(7, s.ReservedExposure);
    }
    [Fact] public void UnsolicitedCancelFailsClosed() => Assert.True(OrderGate.Apply(Ready(), new ConfirmCancel("c", 3, "a"), 10).State.Stopped);
    [Fact] public void RejectionReleasesOutstanding() => Assert.Equal(0, Apply(Ready(), new RejectOrder("x", 3, "a")).Pending);
    [Fact] public void FullFillRemovesOutstandingAndPreventsIdReuse()
    {
        var s = Apply(Ready(0, 4), new Fill("f", 3, "a", "exec-1", 4));
        Assert.Empty(s.Orders); Assert.False(OrderGate.Apply(s, new Reserve("new", 4, "a", 1), 10).Accepted);
    }
    [Fact] public void DuplicateEnvelopeDoesNotApplyTwice()
    {
        var e = new Fill("f", 3, "a", "exec-1", 1); var s = Apply(Ready(), e);
        var d = OrderGate.Apply(s, e, 10); Assert.False(d.Accepted); Assert.Equal(7, d.State.Position);
    }
    [Fact] public void DuplicateExecutionWithNewEnvelopeConsumesOnlySequence()
    {
        var s = Apply(Ready(), new Fill("f", 3, "a", "exec-1", 1));
        s = Apply(s, new Fill("f-new", 4, "a", "exec-1", 1));
        Assert.Equal(7, s.Position); Assert.Equal(4, s.Sequence);
    }
    [Fact] public void StaleSequenceDoesNotMutate() => Assert.False(OrderGate.Apply(Ready(), new Stop("old", 2), 10).Accepted);
    [Fact] public void GapHaltsUntilSnapshotAndExplicitResume()
    {
        var d = OrderGate.Apply(Ready(), new Reserve("gap", 4, "b", 1), 10);
        Assert.False(d.Accepted); Assert.True(d.State.Stopped); Assert.False(d.State.Reconciled);
        Assert.False(OrderGate.Apply(d.State, new Resume("r2", 3), 10).Accepted);
        var s = Apply(d.State, new Snapshot("fresh", 9, 2, [])); Assert.True(s.Stopped);
        Assert.False(Apply(s, new Resume("r3", 10)).Stopped);
    }
    [Fact] public void DisconnectAndStopDoNotPretendToCancelOrFlatten()
    {
        var s = Apply(Ready(), new Disconnect("d", 3));
        Assert.True(s.Stopped); Assert.False(s.Reconciled); Assert.Equal(6, s.Position); Assert.Equal(4, s.Pending);
    }
    [Fact] public void StopBlocksNewOrdersButStillAccountsFills()
    {
        var s = Apply(Ready(), new Stop("stop", 3));
        s = Apply(s, new Fill("f", 4, "a", "exec-1", 1));
        Assert.Equal(7, s.Position); Assert.False(OrderGate.Apply(s, new Reserve("b", 5, "b", 1), 10).Accepted);
    }
    [Fact] public void OverfillAndUnknownFillFailClosed()
    {
        Assert.False(OrderGate.Apply(Ready(), new Fill("f", 3, "a", "exec-1", 5), 10).State.Reconciled);
        Assert.False(OrderGate.Apply(Ready(), new Fill("f", 3, "unknown", "exec-1", 1), 10).State.Reconciled);
    }
    [Theory] [InlineData(0)] [InlineData(-1)] public void InvalidReservationQuantityIsRejected(long q) => Assert.False(OrderGate.Apply(Ready(0, 0), new Reserve("x", 3, "x", q), 10).Accepted);
    [Fact] public void DuplicateSnapshotIdsAreRejected() => Assert.False(OrderGate.Apply(GateState.Empty, new Snapshot("s", 1, 0, [new("a", 1), new("a", 2)]), 10).Accepted);
    [Fact] public void OverLimitSnapshotCannotResume()
    {
        var s = Apply(GateState.Empty, new Snapshot("s", 1, 11, []));
        Assert.False(OrderGate.Apply(s, new Resume("r", 2), 10).Accepted);
    }
    [Fact] public void OverflowIsCaughtWithoutMutation()
    {
        var s = Apply(GateState.Empty, new Snapshot("s", 1, long.MaxValue, []));
        s = Apply(s, new Resume("r", 2), long.MaxValue);
        var d = OrderGate.Apply(s, new Reserve("x", 3, "x", 1), long.MaxValue);
        Assert.False(d.Accepted); Assert.True(d.State.Stopped); Assert.Empty(d.State.Orders);
    }
    [Fact] public void InputStateIsImmutable()
    {
        var s = Ready(); var n = Apply(s, new Fill("f", 3, "a", "exec-1", 1));
        Assert.Equal(6, s.Position); Assert.Equal(4, s.Pending); Assert.Equal(7, n.Position);
    }
}
