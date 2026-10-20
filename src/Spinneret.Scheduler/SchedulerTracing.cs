using System.Diagnostics;

namespace Spinneret.Scheduler;

internal static class SchedulerTags
{
    internal const string JobsDispatched = "scheduler.jobs_dispatched";
    internal const string JobId = "scheduler.job_id";
    internal const string JobKind = "scheduler.job_kind";
    internal const string Outcome = "scheduler.outcome";
}

// Documented in docs/scheduler.md.
internal static class SchedulerJobOutcome
{
    internal const string Enqueued = "enqueued";
    internal const string Skipped = "skipped";
    internal const string Quarantined = "quarantined";
}

internal static class SchedulerJobKind
{
    internal const string Recurring = "recurring";
    internal const string OneShot = "oneshot";
}

// Shared by all storage providers so their spans cannot drift apart.
internal static class SchedulerTracing
{
    private static readonly string? Version = typeof(SchedulerTracing).Assembly.GetName().Version?.ToString();

    private static readonly ActivitySource Source = new(SchedulerDiagnostics.ActivitySourceName, Version);

    internal static Activity? StartSweep() => Source.StartActivity("scheduler sweep", ActivityKind.Internal);

    // Call after the claim, so empty polls emit no span.
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
