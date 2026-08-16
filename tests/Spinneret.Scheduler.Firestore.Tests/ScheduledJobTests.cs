namespace Spinneret.Scheduler.Firestore.Tests;

/// <summary>
/// Pins the persisted document schema. These names are the wire format of the scheduler
/// collection — renaming any of them breaks every already-persisted job document.
/// </summary>
public class ScheduledJobTests
{
    [Test]
    public async Task Fields_names_match_the_persisted_document_schema()
    {
        await Assert.That(ScheduledJob.Fields.RequestTypeName).IsEqualTo("requestTypeName");
        await Assert.That(ScheduledJob.Fields.PayloadJson).IsEqualTo("payloadJson");
        await Assert.That(ScheduledJob.Fields.Status).IsEqualTo("status");
        await Assert.That(ScheduledJob.Fields.NextExecuteAt).IsEqualTo("nextExecuteAt");
        await Assert.That(ScheduledJob.Fields.CreatedAt).IsEqualTo("createdAt");
        await Assert.That(ScheduledJob.Fields.Schedule).IsEqualTo("schedule");
        await Assert.That(ScheduledJob.Fields.LastRunAt).IsEqualTo("lastRunAt");
    }

    [Test]
    public async Task StatusValues_match_the_persisted_status_strings()
    {
        await Assert.That(ScheduledJob.StatusValues.Pending).IsEqualTo("pending");
        await Assert.That(ScheduledJob.StatusValues.Cancelled).IsEqualTo("cancelled");
        await Assert.That(ScheduledJob.StatusValues.Enqueued).IsEqualTo("enqueued");
        await Assert.That(ScheduledJob.StatusValues.Failed).IsEqualTo("failed");
    }
}
