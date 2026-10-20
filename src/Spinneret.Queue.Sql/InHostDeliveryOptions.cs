namespace Spinneret.Queue.Sql;

/// <summary>
/// How the in-host worker drains the SQL outbox: one polling loop per channel × parallelism, each
/// delivering one message per database transaction.
/// </summary>
public sealed class InHostDeliveryOptions
{
    public static readonly string SectionName = "Queue:InHost";

    /// <summary>How long an idle worker waits before polling its channel again.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Concurrent deliveries per channel; a channel not listed here is consumed one message at a
    /// time. Keys must be channels declared by a [QueuePolicy] (or the default channel), so a typo
    /// fails the host at boot instead of silently configuring a channel that never runs.
    /// </summary>
    public Dictionary<string, int> ChannelParallelism { get; set; } = new(StringComparer.Ordinal);
}
