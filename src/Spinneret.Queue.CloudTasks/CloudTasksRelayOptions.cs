namespace Spinneret.Queue.CloudTasks;

/// <summary>
/// How a SQL storage's outbox is forwarded to Cloud Tasks, when the queue has both. Bound from
/// <c>Queue:CloudTasks:Relay</c>; ignored when Cloud Tasks is the queue's only component, since each
/// enqueue then goes to Cloud Tasks directly.
/// </summary>
public sealed class CloudTasksRelayOptions
{
    /// <summary>
    /// The furthest ahead Cloud Tasks accepts a task's schedule time. A delayed message is forwarded
    /// no earlier than this before it is due; until then it waits in the outbox.
    /// </summary>
    public static readonly TimeSpan MaxForwardAhead = TimeSpan.FromDays(29);

    /// <summary>
    /// Whether this host forwards the outbox on a timer. Turn it off on a host that scales to zero or
    /// is CPU-throttled outside requests (Cloud Run's default), map <c>MapQueueRelay</c> instead, and
    /// have Cloud Scheduler call it. Messages committed by a delivery, a scheduler sweep, a resend or
    /// an enqueue outside a transaction are forwarded as they commit either way; only those committed
    /// in the application's own transactions wait for a pass.
    /// </summary>
    public bool RunInBackground { get; set; } = true;

    /// <summary>How long an idle background relay waits before looking at the outbox again.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How many messages one pass leases at a time.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// How long a leased message is hidden from other relays while it is sent. A relay that dies
    /// mid-send leaves it to be sent again once this runs out — which Cloud Tasks then refuses as a
    /// duplicate, so the message is not delivered twice.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How far ahead of its delivery time a delayed message is handed to Cloud Tasks. Messages due
    /// later stay in the outbox — queryable, and cancellable by deleting the row — and are forwarded
    /// as they come within this window. At most <see cref="MaxForwardAhead"/>.
    /// </summary>
    public TimeSpan ForwardAhead { get; set; } = TimeSpan.FromDays(1);
}
