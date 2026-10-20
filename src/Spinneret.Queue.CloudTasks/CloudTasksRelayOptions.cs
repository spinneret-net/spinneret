namespace Spinneret.Queue.CloudTasks;

/// <summary>How a SQL storage's outbox is forwarded to Cloud Tasks; ignored without a storage.</summary>
public sealed class CloudTasksRelayOptions
{
    /// <summary>The furthest ahead Cloud Tasks accepts a task's schedule time.</summary>
    public static readonly TimeSpan MaxForwardAhead = TimeSpan.FromDays(29);

    /// <summary>
    /// Whether this host polls the outbox on a timer; turn off on scale-to-zero hosts and call <c>MapQueueRelay</c> instead.
    /// </summary>
    /// <remarks>Messages committed outside the application's own transactions are forwarded on commit either way.</remarks>
    public bool RunInBackground { get; set; } = true;

    /// <summary>How long an idle background relay waits before looking at the outbox again.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How many messages one pass leases at a time.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>How long a leased message is hidden from other relays while it is sent.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How far ahead of its due time a delayed message is handed to Cloud Tasks; at most <see cref="MaxForwardAhead"/>.
    /// </summary>
    public TimeSpan ForwardAhead { get; set; } = TimeSpan.FromDays(1);
}
