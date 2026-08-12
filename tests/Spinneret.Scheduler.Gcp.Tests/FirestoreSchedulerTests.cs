using Microsoft.Extensions.Options;
using NodaTime;
using Spinneret.Queue;

namespace Spinneret.Scheduler.Gcp.Tests;

/// <summary>
/// Covers the argument validation of <see cref="FirestoreScheduler.RegisterAsync"/>, which runs
/// before any Firestore access — proven by constructing the scheduler with a null FirestoreDb.
/// The transactional register-or-refresh behaviour itself requires a Firestore emulator and is
/// intentionally out of scope.
/// </summary>
public class FirestoreSchedulerTests
{
    private static FirestoreScheduler CreateScheduler() =>
        new(
            db: null!,
            Options.Create(new GcpSchedulerOptions()),
            new ScheduledJobDocumentFactory(new QueueTypeRegistry([]), new FakePayloadSerializer()));

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task RegisterAsync_missing_key_throws_argument_exception(string? key)
    {
        var scheduler = CreateScheduler();

        await Assert.That(() => scheduler.RegisterAsync(key!, new TestRequest("x"), Duration.FromMinutes(1)))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task RegisterAsync_zero_interval_throws_argument_out_of_range()
    {
        var scheduler = CreateScheduler();

        await Assert.That(() => scheduler.RegisterAsync("job", new TestRequest("x"), Duration.Zero))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task RegisterAsync_negative_interval_throws_argument_out_of_range()
    {
        var scheduler = CreateScheduler();

        await Assert.That(() => scheduler.RegisterAsync("job", new TestRequest("x"), Duration.FromSeconds(-1)))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments(500)]
    [Arguments(999)]
    public async Task RegisterAsync_subsecond_interval_throws_argument_out_of_range(int milliseconds)
    {
        // Sub-second intervals would persist as intervalSeconds = 0 and silently degrade the
        // recurring job to a one-shot, so they are rejected up front.
        var scheduler = CreateScheduler();

        await Assert.That(() =>
                scheduler.RegisterAsync("job", new TestRequest("x"), Duration.FromMilliseconds(milliseconds)))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task RegisterAsync_one_second_interval_passes_validation()
    {
        // With a null FirestoreDb the first Firestore access fails with a NullReferenceException,
        // proving that exactly one second clears the argument validation.
        var scheduler = CreateScheduler();

        await Assert.That(() => scheduler.RegisterAsync("job", new TestRequest("x"), Duration.FromSeconds(1)))
            .Throws<NullReferenceException>();
    }
}
