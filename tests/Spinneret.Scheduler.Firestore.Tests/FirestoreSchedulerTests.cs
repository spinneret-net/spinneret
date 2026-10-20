using Microsoft.Extensions.Options;
using Spinneret.Queue;

namespace Spinneret.Scheduler.Firestore.Tests;

public class FirestoreSchedulerTests
{
    private const string Stockholm = "Europe/Stockholm";

    private static FirestoreScheduler CreateScheduler() =>
        new(
            db: null!,
            Options.Create(new FirestoreSchedulerOptions()),
            new ScheduledJobDocumentFactory(
                new QueueTypeRegistry([]), new FakePayloadSerializer(), TimeProvider.System),
            TimeProvider.System);

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task RegisterAsync_missing_key_throws_argument_exception(string? key)
    {
        var scheduler = CreateScheduler();

        await Assert.That(() =>
                scheduler.RegisterAsync(key!, new TestRequest("x"), Schedule.Cron("* * * * *", Stockholm)))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task RegisterAsync_null_schedule_throws_argument_null()
    {
        var scheduler = CreateScheduler();

        await Assert.That(() => scheduler.RegisterAsync("job", new TestRequest("x"), null!))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task RegisterAsync_valid_schedule_passes_validation()
    {
        // A null FirestoreDb throws NullReferenceException on first access, i.e. after validation passed.
        var scheduler = CreateScheduler();

        await Assert.That(() =>
                scheduler.RegisterAsync("job", new TestRequest("x"), Schedule.Cron("* * * * * *", Stockholm)))
            .Throws<NullReferenceException>();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task UnregisterAsync_missing_key_throws_argument_exception(string? key)
    {
        var scheduler = CreateScheduler();

        await Assert.That(() => scheduler.UnregisterAsync(key!)).Throws<ArgumentException>();
    }

    [Test]
    public async Task UnregisterAsync_valid_key_passes_validation()
    {
        var scheduler = CreateScheduler();

        await Assert.That(() => scheduler.UnregisterAsync("job")).Throws<NullReferenceException>();
    }
}
