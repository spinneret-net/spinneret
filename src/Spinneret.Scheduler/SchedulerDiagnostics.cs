using System.Diagnostics;

namespace Spinneret.Scheduler;

/// <summary>
/// Identifiers for the scheduler's tracing instrumentation.
/// </summary>
/// <remarks>
/// A job runs in the sweep's trace, never the one that scheduled it: a recurring job's booking is a
/// host bootstrap from a year ago, and a one-shot can fire months later — a parent that far back
/// describes an operation nothing performed, and is long past any backend's retention. The join back
/// to the scheduling side is the job key, which the sweep logs.
/// </remarks>
public static class SchedulerDiagnostics
{
    /// <summary>
    /// Name of the <see cref="ActivitySource"/> carrying the sweep and per-job spans. Pass it to an
    /// <see cref="ActivityListener"/>, or to OpenTelemetry's <c>AddSource</c>, to record them.
    /// </summary>
    public const string ActivitySourceName = "Spinneret.Scheduler";
}
