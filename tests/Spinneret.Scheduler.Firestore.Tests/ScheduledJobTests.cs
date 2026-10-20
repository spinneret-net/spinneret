namespace Spinneret.Scheduler.Firestore.Tests;

// These names are the stored wire format; renaming one breaks every persisted job.
public class ScheduledJobTests
{
    [Test]
    public async Task Fields_names_match_the_persisted_document_schema()
    {
        await Assert.That(ScheduledJob.Fields.RequestTypeName).IsEqualTo("requestTypeName");
        await Assert.That(ScheduledJob.Fields.PayloadJson).IsEqualTo("payloadJson");
        await Assert.That(ScheduledJob.Fields.NextExecuteAt).IsEqualTo("nextExecuteAt");
        await Assert.That(ScheduledJob.Fields.CreatedAt).IsEqualTo("createdAt");
        await Assert.That(ScheduledJob.Fields.Schedule).IsEqualTo("schedule");
        await Assert.That(ScheduledJob.Fields.LastRunAt).IsEqualTo("lastRunAt");
    }

    [Test]
    public async Task One_shot_handles_carry_the_prefix_that_distinguishes_them_from_recurring_keys()
    {
        // The prefix lets CancelJob reject a recurring key without a read, keeping it write-only.
        await Assert.That(ScheduledJob.OneShotHandlePrefix).IsEqualTo("oneshot-");
        await Assert.That(ScheduledJob.NewOneShotHandle()).StartsWith("oneshot-");
        await Assert.That(ScheduledJob.IsOneShotHandle(ScheduledJob.NewOneShotHandle())).IsTrue();
        await Assert.That(ScheduledJob.IsOneShotHandle("nightly-cleanup")).IsFalse();
    }

    [Test]
    public async Task One_shot_handles_are_unique()
    {
        await Assert.That(ScheduledJob.NewOneShotHandle()).IsNotEqualTo(ScheduledJob.NewOneShotHandle());
    }
}
