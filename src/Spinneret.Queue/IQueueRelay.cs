namespace Spinneret.Queue;

/// <summary>Runs one pass forwarding a storage's outbox to a push transport.</summary>
/// <remarks>Concurrent passes are safe: messages are leased and deduplicated by name, so each is forwarded once.</remarks>
public interface IQueueRelay
{
    /// <summary>
    /// Forwards messages due within the transport's horizon; failed sends stay for a later pass.
    /// </summary>
    Task<RelayResult> RelayAsync(CancellationToken ct);
}

/// <summary>What one <see cref="IQueueRelay.RelayAsync"/> pass did.</summary>
public sealed record RelayResult
{
    /// <summary>A pass that found nothing to forward.</summary>
    public static RelayResult Nothing { get; } = new();

    /// <summary>How many messages this pass handed to the transport.</summary>
    public int MessagesForwarded { get; init; }

    /// <summary>How many messages failed to send and were left for a later pass.</summary>
    public int MessagesFailed { get; init; }
}

// Supplied by the transport's options so the storage package needs no reference to the transport.
internal sealed record QueueRelaySettings(
    bool RunInBackground, TimeSpan PollInterval, int BatchSize, TimeSpan LeaseDuration, TimeSpan ForwardAhead);
