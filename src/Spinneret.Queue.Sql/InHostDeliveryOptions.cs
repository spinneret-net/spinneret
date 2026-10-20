namespace Spinneret.Queue.Sql;

/// <summary>How the in-host worker polls and delivers from the SQL outbox.</summary>
public sealed class InHostDeliveryOptions
{
    public static readonly string SectionName = "Queue:InHost";

    /// <summary>How long an idle worker waits before polling its channel again.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Concurrent deliveries per channel (default 1); an undeclared channel fails startup.</summary>
    public Dictionary<string, int> ChannelParallelism { get; set; } = new(StringComparer.Ordinal);
}
