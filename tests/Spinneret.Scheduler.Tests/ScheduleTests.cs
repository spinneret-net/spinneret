using NodaTime;
using NodaTime.TimeZones;

namespace Spinneret.Scheduler.Tests;

public class ScheduleTests
{
    private static readonly DateTimeZone Stockholm = DateTimeZoneProviders.Tzdb["Europe/Stockholm"];

    // ------------------------------------------------------------------------------ NextRun ---

    [Test]
    public async Task Next_run_is_the_next_matching_local_slot()
    {
        var schedule = Schedule.Cron(Stockholm, "0 3 * * *");
        var now = Instant.FromUtc(2026, 8, 12, 10, 0); // 12:00 CEST, past today's 03:00

        // 03:00 CEST on the 13th = 01:00 UTC.
        await Assert.That(schedule.NextRun(now)).IsEqualTo(Instant.FromUtc(2026, 8, 13, 1, 0));
    }

    [Test]
    public async Task Next_run_is_strictly_after_now()
    {
        var schedule = Schedule.Cron(Stockholm, "0 3 * * *");
        var slot = Instant.FromUtc(2026, 8, 12, 1, 0); // exactly 03:00 CEST

        // Exactly at the slot: a sweep leases by advancing to NextRun, so returning the same instant
        // would re-select the job forever.
        await Assert.That(schedule.NextRun(slot)).IsEqualTo(Instant.FromUtc(2026, 8, 13, 1, 0));
    }

    [Test]
    public async Task Six_fields_schedule_to_the_second()
    {
        var schedule = Schedule.Cron(Stockholm, "* * * * * *");
        var now = Instant.FromUtc(2026, 8, 12, 10, 0, 0);

        await Assert.That(schedule.NextRun(now)).IsEqualTo(Instant.FromUtc(2026, 8, 12, 10, 0, 1));
    }

    [Test]
    public async Task Spring_forward_gap_runs_when_the_clock_finishes_the_jump()
    {
        // Stockholm springs forward 2026-03-29 02:00 CET -> 03:00 CEST, so 02:30 never happens that
        // day. The slot runs at the moment the gap closes (03:00 CEST = 01:00 UTC) rather than being
        // skipped for the day.
        var schedule = Schedule.Cron(Stockholm, "30 2 * * *");
        var now = Instant.FromUtc(2026, 3, 29, 0, 0); // 01:00 CET, before the transition

        await Assert.That(schedule.NextRun(now)).IsEqualTo(Instant.FromUtc(2026, 3, 29, 1, 0));
        // And the day after, the slot is back to its ordinary local time (02:30 CEST = 00:30 UTC).
        await Assert.That(schedule.NextRun(Instant.FromUtc(2026, 3, 29, 1, 0)))
            .IsEqualTo(Instant.FromUtc(2026, 3, 30, 0, 30));
    }

    [Test]
    public async Task Fall_back_overlap_runs_a_fixed_slot_once()
    {
        // Stockholm falls back 2026-10-25 03:00 CEST -> 02:00 CET, so 02:30 happens twice. A slot
        // named once a day runs on the first pass only (02:30 CEST = 00:30 UTC).
        var schedule = Schedule.Cron(Stockholm, "30 2 * * *");

        await Assert.That(schedule.NextRun(Instant.FromUtc(2026, 10, 24, 23, 30)))
            .IsEqualTo(Instant.FromUtc(2026, 10, 25, 0, 30));

        // From the first occurrence the next run is the following day: the repeated hour must not
        // produce a second run of a once-a-day slot.
        await Assert.That(schedule.NextRun(Instant.FromUtc(2026, 10, 25, 0, 30)))
            .IsEqualTo(Instant.FromUtc(2026, 10, 26, 1, 30));
    }

    [Test]
    public async Task Fall_back_overlap_runs_a_recurring_slot_in_both_passes()
    {
        // The counterpart to the fixed slot: an expression that recurs within the hour keeps running
        // through the repeated hour, so 02:00 CET (01:00 UTC) follows 02:30 CEST (00:30 UTC).
        var schedule = Schedule.Cron(Stockholm, "*/30 * * * *");

        await Assert.That(schedule.NextRun(Instant.FromUtc(2026, 10, 25, 0, 30)))
            .IsEqualTo(Instant.FromUtc(2026, 10, 25, 1, 0));
    }

    // --------------------------------------------------------------------------- canonical ---

    [Test]
    public async Task Canonical_string_is_stable()
    {
        // Pins the wire format: persisted and configured schedules must stay parseable across versions.
        await Assert.That(Schedule.Cron(Stockholm, "0 3 * * *").ToString())
            .IsEqualTo("cron:Europe/Stockholm:0 3 * * *");
    }

    [Test]
    public async Task Expression_is_normalized_to_one_canonical_form()
    {
        // Providers compare the stored canonical string to decide whether a schedule changed, so
        // spacing and name casing must not read as a different schedule on the next deploy.
        var schedule = Schedule.Cron(Stockholm, "  0   3  *  *  mon\t");

        await Assert.That(schedule.Expression).IsEqualTo("0 3 * * MON");
        await Assert.That(schedule).IsEqualTo(Schedule.Cron(Stockholm, "0 3 * * MON"));
    }

    [Test]
    public async Task Round_trips_through_parse()
    {
        var schedule = Schedule.Cron(Stockholm, "15 4,16 * * 1-5");

        await Assert.That(Schedule.Parse(schedule.ToString())).IsEqualTo(schedule);
    }

    [Test]
    public async Task Zone_is_part_of_identity()
    {
        var oslo = DateTimeZoneProviders.Tzdb["Europe/Oslo"];

        await Assert.That(Schedule.Cron(Stockholm, "0 3 * * *"))
            .IsNotEqualTo(Schedule.Cron(oslo, "0 3 * * *"));
    }

    // ----------------------------------------------------------------------------- rejects ---

    [Test]
    [Arguments("* * * *")]
    [Arguments("* * * * * * *")]
    public async Task Wrong_field_count_throws_argument_exception(string expression)
    {
        await Assert.That(() => Schedule.Cron(Stockholm, expression)).Throws<ArgumentException>();
    }

    [Test]
    [Arguments("0 99 * * *")]
    [Arguments("banana")]
    [Arguments("0 3 * * NOTADAY")]
    public async Task Invalid_expression_throws_argument_exception(string expression)
    {
        await Assert.That(() => Schedule.Cron(Stockholm, expression)).Throws<ArgumentException>();
    }

    [Test]
    public async Task Expression_that_never_occurs_throws_argument_exception()
    {
        // Valid cron, but 30 February never arrives: a sweep could never advance the job past it.
        await Assert.That(() => Schedule.Cron(Stockholm, "0 0 30 2 *")).Throws<ArgumentException>();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Missing_expression_throws_argument_exception(string? expression)
    {
        await Assert.That(() => Schedule.Cron(Stockholm, expression!)).Throws<ArgumentException>();
    }

    [Test]
    public async Task Missing_zone_throws_argument_null()
    {
        await Assert.That(() => Schedule.Cron(null!, "0 3 * * *")).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Non_tzdb_zone_is_rejected()
    {
        // Only the zone id is persisted and rehydrated via TZDB; a zone TZDB cannot resolve by id —
        // a custom zone here, a Windows zone id on a BCL provider — would produce a schedule the
        // dispatch sweep can never parse back. The zone is hand-rolled rather than taken from the
        // BCL provider, whose ids are Windows-only, so the test asserts the same on every platform.
        await Assert.That(() => Schedule.Cron(new UnknownZone(), "0 3 * * *")).Throws<ArgumentException>();
    }

    // ------------------------------------------------------------------------------- Parse ---

    [Test]
    [Arguments("nonsense")]
    [Arguments("cron")]
    [Arguments("cron:Europe/Stockholm")]
    [Arguments("cron:0 3 * * *")]
    public async Task Parse_unrecognized_forms_throw_format_exception(string text)
    {
        await Assert.That(() => Schedule.Parse(text)).Throws<FormatException>();
    }

    [Test]
    [Arguments("every:0:00:15:00")]
    [Arguments("daily:Europe/Stockholm:07:00:00")]
    public async Task Parse_pre_cron_forms_throw_format_exception(string text)
    {
        // Stored by an older version: the sweep quarantines what it cannot parse, so the message has
        // to be the one an operator reads.
        await Assert.That(() => Schedule.Parse(text)).Throws<FormatException>()
            .WithMessageContaining("pre-cron");
    }

    [Test]
    public async Task Parse_unknown_time_zone_throws_format_exception()
    {
        await Assert.That(() => Schedule.Parse("cron:Mars/Olympus:0 3 * * *")).Throws<FormatException>();
    }

    [Test]
    [Arguments("cron:Europe/Stockholm:0 99 * * *")]
    [Arguments("cron:Europe/Stockholm:* * * *")]
    [Arguments("cron:Europe/Stockholm:0 0 30 2 *")]
    public async Task Parse_wraps_a_rejected_expression_as_format_exception(string text)
    {
        // Parse's contract is FormatException for any non-canonical text — an expression the schedule
        // rejects must not leak the ArgumentException from the factory.
        await Assert.That(() => Schedule.Parse(text)).Throws<FormatException>();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Parse_missing_text_throws_argument_exception(string? text)
    {
        await Assert.That(() => Schedule.Parse(text!)).Throws<ArgumentException>();
    }

    /// <summary>A valid zone carrying an id no TZDB lookup can resolve.</summary>
    private sealed class UnknownZone() : DateTimeZone("Mars/Olympus", true, Offset.Zero, Offset.Zero)
    {
        public override ZoneInterval GetZoneInterval(Instant instant) =>
            new(Id, null, null, Offset.Zero, Offset.Zero);
    }
}
