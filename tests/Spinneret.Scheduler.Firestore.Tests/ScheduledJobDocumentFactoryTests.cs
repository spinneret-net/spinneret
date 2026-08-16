using Google.Cloud.Firestore;
using Spinneret.Queue;

namespace Spinneret.Scheduler.Firestore.Tests;

public class ScheduledJobDocumentFactoryTests
{
    private const string Stockholm = "Europe/Stockholm";

    private static QueueTypeRegistry Registry => new([typeof(TestRequest).Assembly]);

    /// <summary>A fixed clock, so <c>createdAt</c> is an assertable value rather than "now".</summary>
    internal static readonly DateTimeOffset Now = new(2026, 8, 15, 9, 0, 0, TimeSpan.Zero);

    private static TimeProvider FixedClock => new FixedTimeProvider(Now);

    private static ScheduledJobDocumentFactory CreateFactory(FakePayloadSerializer? serializer = null) =>
        new(Registry, serializer ?? new FakePayloadSerializer(), FixedClock);

    // ------------------------------------------------------------------------------- OneShot ---

    [Test]
    public async Task OneShot_maps_request_type_name_from_registry()
    {
        var factory = CreateFactory();

        var doc = factory.OneShot(new TestRequest("x"), new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));

        await Assert.That((string)doc[ScheduledJob.Fields.RequestTypeName])
            .IsEqualTo(typeof(TestRequest).FullName!);
    }

    [Test]
    public async Task OneShot_serializes_payload_with_the_concrete_request_type()
    {
        var serializer = new FakePayloadSerializer { SerializeResult = """{"name":"x"}""" };
        var factory = CreateFactory(serializer);
        var request = new TestRequest("x");

        var doc = factory.OneShot(request, new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));

        await Assert.That((string)doc[ScheduledJob.Fields.PayloadJson]).IsEqualTo("""{"name":"x"}""");
        await Assert.That(serializer.SerializeCalls.Count).IsEqualTo(1);
        await Assert.That(serializer.SerializeCalls[0].Request).IsSameReferenceAs(request);
        await Assert.That(serializer.SerializeCalls[0].RequestType).IsEqualTo(typeof(TestRequest));
    }

    [Test]
    public async Task OneShot_writes_no_status_field()
    {
        // Existence is the status: a job is deleted once it has run, so there is nothing to record.
        var factory = CreateFactory();

        var doc = factory.OneShot(new TestRequest("x"), new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));

        await Assert.That(doc.Keys).DoesNotContain("status");
    }

    [Test]
    public async Task OneShot_sets_next_execute_at_to_the_given_instant()
    {
        var factory = CreateFactory();
        var executeAt = new DateTimeOffset(2030, 6, 15, 12, 30, 45, TimeSpan.Zero);

        var doc = factory.OneShot(new TestRequest("x"), executeAt);

        var expected = Timestamp.FromDateTimeOffset(executeAt);
        await Assert.That((Timestamp)doc[ScheduledJob.Fields.NextExecuteAt]).IsEqualTo(expected);
    }

    [Test]
    public async Task OneShot_preserves_subsecond_precision_of_the_execute_instant()
    {
        var factory = CreateFactory();
        var executeAt = new DateTimeOffset(2030, 6, 15, 12, 30, 45, TimeSpan.Zero).AddTicks(1_234_567);

        var doc = factory.OneShot(new TestRequest("x"), executeAt);

        var expected = Timestamp.FromDateTimeOffset(executeAt);
        await Assert.That((Timestamp)doc[ScheduledJob.Fields.NextExecuteAt]).IsEqualTo(expected);
    }

    [Test]
    public async Task OneShot_stamps_created_at_from_the_injected_clock()
    {
        // Exact rather than a window: the factory takes a TimeProvider, so the value is determined
        // by the caller's clock and not by whenever the test happened to run.
        var factory = CreateFactory();

        var doc = factory.OneShot(new TestRequest("x"), new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));

        var createdAt = ((Timestamp)doc[ScheduledJob.Fields.CreatedAt]).ToDateTimeOffset();
        await Assert.That(createdAt).IsEqualTo(Now);
    }

    [Test]
    public async Task OneShot_omits_schedule_marking_the_job_as_one_shot()
    {
        // The dispatcher treats the absence of schedule as the one-shot marker.
        var factory = CreateFactory();

        var doc = factory.OneShot(new TestRequest("x"), new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));

        await Assert.That(doc.ContainsKey(ScheduledJob.Fields.Schedule)).IsFalse();
        await Assert.That(doc.ContainsKey(ScheduledJob.Fields.LastRunAt)).IsFalse();
    }

    [Test]
    public async Task OneShot_unregistered_request_type_throws_invalid_operation()
    {
        // Registry built over no assemblies: TestRequest is unknown to it.
        var factory = new ScheduledJobDocumentFactory(
            new QueueTypeRegistry([]), new FakePayloadSerializer(), TimeProvider.System);

        await Assert.That(() => factory.OneShot(new TestRequest("x"), new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)))
            .Throws<InvalidOperationException>();
    }

    // --------------------------------------------------------------------- RecurringDefinition ---

    [Test]
    public async Task RecurringDefinition_writes_the_canonical_schedule_string()
    {
        var factory = CreateFactory();
        var schedule = Schedule.Cron("*/15 * * * *", Stockholm);

        var doc = factory.RecurringDefinition(new TestRequest("x"), schedule);

        await Assert.That((string)doc[ScheduledJob.Fields.Schedule]).IsEqualTo(schedule.ToString());
    }

    [Test]
    public async Task RecurringDefinition_writes_the_zone_with_the_expression()
    {
        // The stored string is the whole schedule: a zone lost here would silently re-interpret every
        // slot in UTC when the sweep rehydrates it.
        var factory = CreateFactory();

        var doc = factory.RecurringDefinition(new TestRequest("x"), Schedule.Cron("0 1 * * *", Stockholm));

        await Assert.That((string)doc[ScheduledJob.Fields.Schedule])
            .IsEqualTo("cron:Europe/Stockholm:0 1 * * *");
    }

    [Test]
    public async Task RecurringDefinition_maps_type_name_and_payload()
    {
        var serializer = new FakePayloadSerializer { SerializeResult = """{"number":7}""" };
        var factory = CreateFactory(serializer);
        var request = new OtherTestRequest(7);

        var doc = factory.RecurringDefinition(request, Schedule.Cron("0 * * * *", Stockholm));

        await Assert.That((string)doc[ScheduledJob.Fields.RequestTypeName])
            .IsEqualTo(typeof(OtherTestRequest).FullName!);
        await Assert.That((string)doc[ScheduledJob.Fields.PayloadJson]).IsEqualTo("""{"number":7}""");
        await Assert.That(serializer.SerializeCalls[0].RequestType).IsEqualTo(typeof(OtherTestRequest));
    }

    [Test]
    public async Task RecurringDefinition_contains_only_definition_fields()
    {
        // Status and run timestamps are owned by the caller's register-or-refresh logic — the
        // definition must never carry them, or re-registering would disturb a live schedule.
        var factory = CreateFactory();

        var doc = factory.RecurringDefinition(new TestRequest("x"), Schedule.Cron("* * * * *", Stockholm));

        await Assert.That(doc.Keys).IsEquivalentTo([
            ScheduledJob.Fields.RequestTypeName,
            ScheduledJob.Fields.PayloadJson,
            ScheduledJob.Fields.Schedule
        ]);
    }
}
