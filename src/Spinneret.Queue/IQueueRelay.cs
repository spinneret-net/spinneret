namespace Spinneret.Queue;

/// <summary>
/// One pass of forwarding a storage's outbox to a push transport — the seam a trigger drives when a
/// SQL storage feeds Cloud Tasks. The relay runs in the background by default; a host that scales to
/// zero, and so has no thread to tick, maps an HTTP trigger for it instead and has an external
/// scheduler call it. Registered only when the queue has both a storage and a push transport.
/// </summary>
/// <remarks>
/// Passes race safely, across hosts and within one: a message is leased before it is sent, and the
/// transport deduplicates on the message's name, so a message is forwarded once however many passes
/// see it.
/// </remarks>
public interface IQueueRelay
{
    /// <summary>
    /// Forwards the messages that are due within the transport's horizon now. A message that fails to
    /// send stays in the outbox and is retried by a later pass; an exception out of here means the
    /// pass itself could not run.
    /// </summary>
    Task<RelayResult> RelayAsync(CancellationToken ct);
}

/// <summary>
/// What one <see cref="IQueueRelay.RelayAsync"/> pass did. A result object rather than a bare
/// <c>Task</c> so the pass can report more later without breaking callers.
/// </summary>
/// <remarks>New members must be optional (non-required) init-only properties.</remarks>
public sealed record RelayResult
{
    /// <summary>A pass that found nothing to forward.</summary>
    public static RelayResult Nothing { get; } = new();

    /// <summary>How many messages this pass handed to the transport.</summary>
    public int MessagesForwarded { get; init; }

    /// <summary>How many messages failed to send and were left for a later pass.</summary>
    public int MessagesFailed { get; init; }
}

/// <summary>
/// How a storage's relay drives the push transport, supplied by the transport's own options
/// (e.g. <c>Queue:CloudTasks:Relay</c>) so the storage package needs no reference to it.
/// </summary>
/// <param name="RunInBackground">Whether this host polls the outbox itself, or leaves it to an HTTP trigger.</param>
/// <param name="PollInterval">How long an idle background relay waits before looking again.</param>
/// <param name="BatchSize">How many messages one lease takes.</param>
/// <param name="LeaseDuration">How long a leased message is hidden from other relays while it is sent.</param>
/// <param name="ForwardAhead">
/// How far ahead of its delivery time a delayed message is handed to the transport. Messages due
/// later wait in the outbox, within the transport's own limit on scheduling ahead.
/// </param>
internal sealed record QueueRelaySettings(
    bool RunInBackground, TimeSpan PollInterval, int BatchSize, TimeSpan LeaseDuration, TimeSpan ForwardAhead);
