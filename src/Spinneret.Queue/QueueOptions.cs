namespace Spinneret.Queue;

public sealed record QueueOptions
{
    /// <summary>
    /// Delay the first dispatch by this duration. Maps to Cloud Tasks <c>ScheduleTime</c>.
    /// </summary>
    public TimeSpan? Delay { get; init; }

    /// <summary>
    /// Stable identifier used by the transport to deduplicate tasks. When set, enqueueing
    /// the same key twice in close succession results in only one delivery. Maps to the
    /// Cloud Tasks task name.
    /// </summary>
    public string? DedupeKey { get; init; }

    /// <summary>
    /// Optional human-readable description of what this task is for (e.g. "EmploymentTerminated →
    /// FortnoxSyncSubscriber"). Carried on the envelope purely for observability and surfaced on the
    /// dead-letter page so a failed task is identifiable beyond its command type. Never affects dispatch.
    /// </summary>
    public string? Description { get; init; }
}
