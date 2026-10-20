namespace Spinneret.Queue;

public sealed record QueueOptions
{
    /// <summary>Delays the first dispatch by this duration.</summary>
    public TimeSpan? Delay { get; init; }

    /// <summary>Deduplication key: enqueueing the same key twice in close succession delivers once.</summary>
    public string? DedupeKey { get; init; }

    /// <summary>Optional description shown on the dead-letter page; never affects dispatch.</summary>
    public string? Description { get; init; }
}
