using System.Diagnostics;

namespace Spinneret.Scheduler;

/// <summary>
/// Identifiers for the scheduler's tracing instrumentation.
/// </summary>
/// <remarks>
/// A job runs in the sweep's trace, not the one that scheduled it; correlate by job key.
/// </remarks>
public static class SchedulerDiagnostics
{
    /// <summary>
    /// Name of the <see cref="ActivitySource"/> carrying the sweep and per-job spans.
    /// </summary>
    public const string ActivitySourceName = "Spinneret.Scheduler";
}
