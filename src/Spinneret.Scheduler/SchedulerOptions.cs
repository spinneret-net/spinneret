namespace Spinneret.Scheduler;

public sealed class SchedulerOptions
{
    // static readonly, not const, so a rename isn't baked into consumers' assemblies.
    /// <summary>Configuration section for the in-host sweeper.</summary>
    public static readonly string SectionName = "Scheduler:Sweeper";

    /// <summary>
    /// How long <c>UseInHostSweeper()</c> waits between sweeps; read once at startup.
    /// </summary>
    /// <remarks>
    /// This is also the failure backoff that keeps a stalled provider from hot-looping, so don't set it very low.
    /// </remarks>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(15);
}
