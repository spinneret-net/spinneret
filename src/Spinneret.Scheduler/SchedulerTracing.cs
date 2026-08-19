using System.Diagnostics;

namespace Spinneret.Scheduler;

/// <summary>Tag keys the scheduler's spans carry. Declared once, shared by every storage provider.</summary>
internal static class SchedulerTags
{
    internal const string JobsDispatched = "scheduler.jobs_dispatched";
    internal const string JobId = "scheduler.job_id";
    internal const string JobKind = "scheduler.job_kind";
    internal const string Outcome = "scheduler.outcome";
}

/// <summary>Values of the <see cref="SchedulerTags.Outcome"/> tag, documented in docs/scheduler.md.</summary>
internal static class SchedulerJobOutcome
{
    internal const string Enqueued = "enqueued";
    internal const string Skipped = "skipped";
    internal const string Quarantined = "quarantined";
}

/// <summary>Values of the <see cref="SchedulerTags.JobKind"/> tag.</summary>
internal static class SchedulerJobKind
{
    internal const string Recurring = "recurring";
    internal const string OneShot = "oneshot";
}

/// <summary>
/// The scheduler's spans, shared by the storage providers so the two cannot emit different shapes for
/// the same sweep.
/// </summary>
internal static class SchedulerTracing
{
    private static readonly string? Version = typeof(SchedulerTracing).Assembly.GetName().Version?.ToString();

    private static readonly ActivitySource Source = new(SchedulerDiagnostics.ActivitySourceName, Version);

    internal static Activity? StartSweep() => Source.StartActivity("scheduler sweep", ActivityKind.Internal);

    /// <summary>
    /// Starts the span for one claimed job. Call it after the claim: a span per poll would emit one
    /// for the pass that finds nothing, which is most of them.
    /// </summary>
    internal static Activity? StartJob(string jobId) =>
        Source.StartActivity("scheduler job", ActivityKind.Internal, default(ActivityContext),
            new ActivityTagsCollection { [SchedulerTags.JobId] = jobId });

    internal static void SetOutcome(this Activity? activity, string outcome, string? error = null)
    {
        if (activity is null)
            return;

        activity.SetTag(SchedulerTags.Outcome, outcome);
        if (error is not null)
            activity.SetStatus(ActivityStatusCode.Error, error);
    }
}
