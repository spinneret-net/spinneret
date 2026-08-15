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
