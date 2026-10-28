using System.Collections.Immutable;

namespace ExecutionLab;

// Synthetic portfolio model, not TT integration or a general portfolio-risk engine.
// All quantities are non-negative lots in ONE instrument and ONE direction.
public sealed record OpenOrder(long Remaining, bool CancelRequested = false);
public sealed record SnapshotOrder(string Id, long Remaining);
public sealed record GateState(
    long Position,
    ImmutableDictionary<string, OpenOrder> Orders,
    ImmutableHashSet<string> SeenEvents,
    ImmutableHashSet<string> SeenExecutions,
    ImmutableHashSet<string> UsedOrderIds,
    long Sequence,
    bool Reconciled,
    bool Stopped)
{
    public static GateState Empty { get; } = new(0,
        ImmutableDictionary<string, OpenOrder>.Empty,
        ImmutableHashSet<string>.Empty, ImmutableHashSet<string>.Empty,
        ImmutableHashSet<string>.Empty, 0, false, true);
    public long Pending => Orders.Values.Aggregate(0L, (n, o) => checked(n + o.Remaining));
    public long ReservedExposure => checked(Position + Pending);
}

// Sequence belongs to a single, normalized adapter stream, NOT to TT/CME wire feeds.
public abstract record GateEvent(string EventId, long Sequence);
public sealed record Snapshot(string EventId, long Sequence, long Position,
    ImmutableArray<SnapshotOrder> Orders) : GateEvent(EventId, Sequence);
public sealed record Resume(string EventId, long Sequence) : GateEvent(EventId, Sequence);
public sealed record Reserve(string EventId, long Sequence, string OrderId, long Quantity) : GateEvent(EventId, Sequence);
public sealed record Fill(string EventId, long Sequence, string OrderId, string ExecutionId, long Quantity) : GateEvent(EventId, Sequence);
public sealed record RequestCancel(string EventId, long Sequence, string OrderId) : GateEvent(EventId, Sequence);
public sealed record ConfirmCancel(string EventId, long Sequence, string OrderId) : GateEvent(EventId, Sequence);
public sealed record RejectOrder(string EventId, long Sequence, string OrderId) : GateEvent(EventId, Sequence);
public sealed record Disconnect(string EventId, long Sequence) : GateEvent(EventId, Sequence);
public sealed record Stop(string EventId, long Sequence) : GateEvent(EventId, Sequence);
public sealed record Decision(bool Accepted, string Reason, GateState State);

public static class OrderGate
{
    private static bool Id(string? value) => !string.IsNullOrWhiteSpace(value);

    public static Decision Apply(GateState state, GateEvent? input, long limit)
    {
        ArgumentNullException.ThrowIfNull(state);
        Decision Reject(string reason) => new(false, reason, state);
        Decision Halt(string reason) => new(false, reason, state with { Reconciled = false, Stopped = true });
        if (input is null || !Id(input.EventId) || input.Sequence <= 0 || limit < 0)
            return Reject("Invalid envelope or limit");
        if (state.SeenEvents.Contains(input.EventId)) return Reject("Duplicate envelope");
        if (input.Sequence <= state.Sequence) return Reject("Stale sequence");
        // Snapshot is an externally obtained authoritative baseline; it NEVER resumes trading.
        if (input is not Snapshot && (state.Sequence == long.MaxValue || input.Sequence != state.Sequence + 1))
            return Halt("Sequence gap: snapshot required");
        // A well-formed but denied command still consumes its normalized envelope.
        Decision Deny(string reason) => new(false, reason, state with {
            Sequence = input.Sequence, SeenEvents = state.SeenEvents.Add(input.EventId) });
        try
        {
            var next = state;
            switch (input)
            {
                case Snapshot s:
                    if (s.Position < 0 || s.Orders.IsDefault) return Halt("Invalid snapshot");
                    var orders = ImmutableDictionary<string, OpenOrder>.Empty;
                    foreach (var order in s.Orders)
                    {
                        if (order is null || !Id(order.Id) || order.Remaining <= 0 || orders.ContainsKey(order.Id))
                            return Halt("Invalid snapshot order");
                        orders = orders.Add(order.Id, new(order.Remaining));
                    }
                    next = state with { Position = s.Position, Orders = orders,
                        UsedOrderIds = state.UsedOrderIds.Union(orders.Keys), Reconciled = true, Stopped = true };
                    _ = next.ReservedExposure; // checked aggregation rejects overflow
                    break;
                case Resume:
                    if (!state.Reconciled || state.ReservedExposure > limit) return Deny("Not reconciled or over limit");
                    next = state with { Stopped = false };
                    break;
                case Reserve r:
                    if (!state.Reconciled || state.Stopped) return Deny("Not ready");
                    if (!Id(r.OrderId) || r.Quantity <= 0 || state.UsedOrderIds.Contains(r.OrderId))
                        return Deny("Invalid or reused order ID");
                    if (checked(state.ReservedExposure + r.Quantity) > limit) return Deny("Exposure limit");
                    next = state with { Orders = state.Orders.Add(r.OrderId, new(r.Quantity)),
                        UsedOrderIds = state.UsedOrderIds.Add(r.OrderId) };
                    break;
                case Fill f:
                    if (!Id(f.ExecutionId) || !Id(f.OrderId) || f.Quantity <= 0) return Halt("Invalid fill");
                    // A redelivered execution can have a new transport envelope. Consume sequence, not quantity.
                    if (state.SeenExecutions.Contains(f.ExecutionId)) break;
                    if (!state.Orders.TryGetValue(f.OrderId, out var filled) || f.Quantity > filled.Remaining)
                        return Halt("Unknown or inconsistent fill: reconcile");
                    var remaining = filled.Remaining - f.Quantity;
                    next = state with { Position = checked(state.Position + f.Quantity),
                        Orders = remaining == 0 ? state.Orders.Remove(f.OrderId)
                            : state.Orders.SetItem(f.OrderId, filled with { Remaining = remaining }),
                        SeenExecutions = state.SeenExecutions.Add(f.ExecutionId) };
                    break;
                case RequestCancel c:
                    if (!Id(c.OrderId) || !state.Orders.TryGetValue(c.OrderId, out var cancel)) return Deny("Unknown order");
                    next = state with { Orders = state.Orders.SetItem(c.OrderId, cancel with { CancelRequested = true }) };
                    break;
                case ConfirmCancel c:
                    if (!Id(c.OrderId) || !state.Orders.TryGetValue(c.OrderId, out var confirmed) || !confirmed.CancelRequested)
                        return Halt("Unsolicited cancellation: reconcile");
                    next = state with { Orders = state.Orders.Remove(c.OrderId) };
                    break;
                case RejectOrder r:
                    if (!Id(r.OrderId) || !state.Orders.ContainsKey(r.OrderId)) return Halt("Unknown rejection: reconcile");
                    next = state with { Orders = state.Orders.Remove(r.OrderId) };
                    break;
                case Disconnect:
                    next = state with { Reconciled = false, Stopped = true };
                    break;
                case Stop:
                    next = state with { Stopped = true };
                    break;
                default: return Reject("Unsupported event");
            }
            return new(true, "Applied", next with { Sequence = input.Sequence, SeenEvents = next.SeenEvents.Add(input.EventId) });
        }
        catch (OverflowException)
        {
            return Halt("Quantity overflow: reconcile");
        }
    }
}
