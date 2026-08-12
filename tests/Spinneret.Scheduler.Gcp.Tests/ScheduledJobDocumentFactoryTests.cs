using Google.Cloud.Firestore;
using NodaTime;
using Spinneret.Queue;

namespace Spinneret.Scheduler.Gcp.Tests;

public class ScheduledJobDocumentFactoryTests
{
    private static QueueTypeRegistry Registry => new([typeof(TestRequest).Assembly]);

    private static ScheduledJobDocumentFactory CreateFactory(FakePayloadSerializer? serializer = null) =>
        new(Registry, serializer ?? new FakePayloadSerializer());

    // ------------------------------------------------------------------------------- OneShot ---

    [Test]
    public async Task OneShot_maps_request_type_name_from_registry()
    {
        var factory = CreateFactory();

        var doc = factory.OneShot(new TestRequest("x"), Instant.FromUtc(2030, 1, 2, 3, 4, 5));

        await Assert.That((string)doc[ScheduledJob.Fields.RequestTypeName])
            .IsEqualTo(typeof(TestRequest).FullName!);
    }

    [Test]
    public async Task OneShot_serializes_payload_with_the_concrete_request_type()
    {
        var serializer = new FakePayloadSerializer { SerializeResult = """{"name":"x"}""" };
        var factory = CreateFactory(serializer);
        var request = new TestRequest("x");

        var doc = factory.OneShot(request, Instant.FromUtc(2030, 1, 2, 3, 4, 5));

        await Assert.That((string)doc[ScheduledJob.Fields.PayloadJson]).IsEqualTo("""{"name":"x"}""");
        await Assert.That(serializer.SerializeCalls.Count).IsEqualTo(1);
        await Assert.That(serializer.SerializeCalls[0].Request).IsSameReferenceAs(request);
        await Assert.That(serializer.SerializeCalls[0].RequestType).IsEqualTo(typeof(TestRequest));
    }

    [Test]
    public async Task OneShot_sets_status_pending()
    {
        var factory = CreateFactory();

        var doc = factory.OneShot(new TestRequest("x"), Instant.FromUtc(2030, 1, 2, 3, 4, 5));

        await Assert.That((string)doc[ScheduledJob.Fields.Status])
            .IsEqualTo(ScheduledJob.StatusValues.Pending);
    }

    [Test]
    public async Task OneShot_sets_execute_at_and_next_execute_at_to_the_given_instant()
    {
        var factory = CreateFactory();
        var executeAt = Instant.FromUtc(2030, 6, 15, 12, 30, 45);

        var doc = factory.OneShot(new TestRequest("x"), executeAt);

        var expected = Timestamp.FromDateTimeOffset(executeAt.ToDateTimeOffset());
        await Assert.That((Timestamp)doc[ScheduledJob.Fields.ExecuteAt]).IsEqualTo(expected);
        await Assert.That((Timestamp)doc[ScheduledJob.Fields.NextExecuteAt]).IsEqualTo(expected);
    }

    [Test]
    public async Task OneShot_preserves_subsecond_precision_of_the_execute_instant()
    {
        var factory = CreateFactory();
        var executeAt = Instant.FromUtc(2030, 6, 15, 12, 30, 45).PlusNanoseconds(123_456_700);

        var doc = factory.OneShot(new TestRequest("x"), executeAt);

        var expected = Timestamp.FromDateTimeOffset(executeAt.ToDateTimeOffset());
        await Assert.That((Timestamp)doc[ScheduledJob.Fields.ExecuteAt]).IsEqualTo(expected);
    }

    [Test]
    public async Task OneShot_sets_created_at_to_the_current_time()
    {
        var factory = CreateFactory();
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var doc = factory.OneShot(new TestRequest("x"), Instant.FromUtc(2030, 1, 2, 3, 4, 5));

        var after = DateTimeOffset.UtcNow.AddSeconds(1);
        var createdAt = ((Timestamp)doc[ScheduledJob.Fields.CreatedAt]).ToDateTimeOffset();
        await Assert.That(createdAt >= before).IsTrue();
        await Assert.That(createdAt <= after).IsTrue();
    }

    [Test]
    public async Task OneShot_omits_schedule_marking_the_job_as_one_shot()
    {
        // The dispatcher treats the absence of schedule as the one-shot marker.
        var factory = CreateFactory();

        var doc = factory.OneShot(new TestRequest("x"), Instant.FromUtc(2030, 1, 2, 3, 4, 5));

        await Assert.That(doc.ContainsKey(ScheduledJob.Fields.Schedule)).IsFalse();
        await Assert.That(doc.ContainsKey(ScheduledJob.Fields.LastRunAt)).IsFalse();
    }

    [Test]
    public async Task OneShot_unregistered_request_type_throws_invalid_operation()
    {
        // Registry built over no assemblies: TestRequest is unknown to it.
        var factory = new ScheduledJobDocumentFactory(new QueueTypeRegistry([]), new FakePayloadSerializer());

        await Assert.That(() => factory.OneShot(new TestRequest("x"), Instant.FromUtc(2030, 1, 1, 0, 0)))
            .Throws<InvalidOperationException>();
    }

    // --------------------------------------------------------------------- RecurringDefinition ---

    [Test]
    public async Task RecurringDefinition_writes_the_canonical_schedule_string()
    {
        var factory = CreateFactory();
        var schedule = Schedule.Every(Duration.FromMinutes(15));

        var doc = factory.RecurringDefinition(new TestRequest("x"), schedule);

        await Assert.That((string)doc[ScheduledJob.Fields.Schedule]).IsEqualTo(schedule.ToString());
    }

    [Test]
    public async Task RecurringDefinition_daily_schedule_writes_the_canonical_schedule_string()
    {
        var factory = CreateFactory();
        var zone = DateTimeZoneProviders.Tzdb["Europe/Stockholm"];

        var doc = factory.RecurringDefinition(new TestRequest("x"), Schedule.Daily(zone, new LocalTime(1, 0)));

        await Assert.That((string)doc[ScheduledJob.Fields.Schedule])
            .IsEqualTo("daily:Europe/Stockholm:01:00:00");
    }

    [Test]
    public async Task RecurringDefinition_maps_type_name_and_payload()
    {
        var serializer = new FakePayloadSerializer { SerializeResult = """{"number":7}""" };
        var factory = CreateFactory(serializer);
        var request = new OtherTestRequest(7);

        var doc = factory.RecurringDefinition(request, Schedule.Every(Duration.FromHours(1)));

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

        var doc = factory.RecurringDefinition(new TestRequest("x"), Schedule.Every(Duration.FromMinutes(1)));

        await Assert.That(doc.Keys).IsEquivalentTo([
            ScheduledJob.Fields.RequestTypeName,
            ScheduledJob.Fields.PayloadJson,
            ScheduledJob.Fields.Schedule
        ]);
    }
}
