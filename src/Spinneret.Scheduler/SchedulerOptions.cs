namespace Spinneret.Scheduler;

public sealed class SchedulerOptions
{
    /// <summary>
    /// Configuration section for the timer trigger. Nested under <c>Scheduler</c> rather than owning
    /// it, so that key stays a pure namespace alongside <c>Scheduler:Firestore</c> and
    /// <c>Scheduler:Mssql</c> — a sweep interval sitting beside provider objects reads as though it
    /// belonged to one of them.
    /// </summary>
    /// <remarks>
    /// static readonly, not const: a const is copied into every consumer's compiled assembly, so
    /// renaming the section later would leave already-built hosts binding the old one in silence.
    /// </remarks>
    public static readonly string SectionName = "Scheduler:Sweeper";

    /// <summary>
    /// How long <c>AddSchedulerSweeper()</c> waits between sweeps. Sweeps race safely across hosts,
    /// so every instance may run its own.
    /// </summary>
    /// <remarks>
    /// This is also the failure backoff. A provider whose sweep cannot make progress — a store that
    /// is unreachable, a job whose booking keeps failing — returns rather than spinning, and this
    /// delay is the only thing standing between that and a hot loop. Setting it very low costs more
    /// than wasted queries. Read once, when the host starts.
    /// </remarks>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(15);
}
