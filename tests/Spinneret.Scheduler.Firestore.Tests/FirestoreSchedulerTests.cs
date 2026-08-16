using Microsoft.Extensions.Options;
using Spinneret.Queue;

namespace Spinneret.Scheduler.Firestore.Tests;

/// <summary>
/// Covers the argument validation of <see cref="FirestoreScheduler.RegisterAsync"/>, which runs
/// before any Firestore access — proven by constructing the scheduler with a null FirestoreDb.
/// The transactional register-or-refresh behaviour itself is covered against the Firestore emulator
/// by <see cref="FirestoreSchedulerIntegrationTests"/>. Schedule construction rules (cron validity,
/// zone) are covered by the Spinneret.Scheduler test suite.
/// </summary>
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
        // With a null FirestoreDb the first Firestore access fails with a NullReferenceException,
        // proving that a valid key and schedule clear the argument validation.
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
