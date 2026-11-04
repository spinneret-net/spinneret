namespace Spinneret.Scheduler.Firestore.Tests;

public class FirestoreSchedulerOptionsTests
{
    [Test]
    public async Task Defaults_collection_is_scheduled_jobs()
    {
        var options = new FirestoreSchedulerOptions();

        await Assert.That(options.Collection).IsEqualTo("scheduled_jobs");
    }

    [Test]
    public async Task Defaults_one_shot_lease_window_is_five_minutes()
    {
        var options = new FirestoreSchedulerOptions();

        await Assert.That(options.OneShotLeaseWindow).IsEqualTo(TimeSpan.FromMinutes(5));
    }

    [Test]
    public async Task Defaults_enqueue_retry_window_is_one_hour()
    {
        // Cloud Tasks remembers a task name for about an hour; past it a retry may deliver twice.
        var options = new FirestoreSchedulerOptions();

        await Assert.That(options.EnqueueRetryWindow).IsEqualTo(TimeSpan.FromHours(1));
    }

    [Test]
    public async Task SectionName_is_the_scheduler_gcp_configuration_path()
    {
        await Assert.That(FirestoreSchedulerOptions.SectionName).IsEqualTo("Scheduler:Firestore");
    }

    [Test]
    public async Task Properties_round_trip_assigned_values()
    {
        var options = new FirestoreSchedulerOptions
        {
            Collection = "custom_jobs",
            OneShotLeaseWindow = TimeSpan.FromSeconds(90),
        };

        await Assert.That(options.Collection).IsEqualTo("custom_jobs");
        await Assert.That(options.OneShotLeaseWindow).IsEqualTo(TimeSpan.FromSeconds(90));
    }
}
