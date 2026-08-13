namespace Spinneret.Scheduler.Tests;

public class ScheduleTests
{
    private const string Stockholm = "Europe/Stockholm";

    /// <summary>A UTC instant, so the expected local slots below can be written as the UTC they land on.</summary>
    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute, int second = 0) =>
        new(year, month, day, hour, minute, second, TimeSpan.Zero);

    // ------------------------------------------------------------------------------ NextRun ---

    [Test]
    public async Task Next_run_is_the_next_matching_local_slot()
    {
        var schedule = Schedule.Cron("0 3 * * *", Stockholm);
        var now = Utc(2026, 8, 12, 10, 0); // 12:00 CEST, past today's 03:00

        // 03:00 CEST on the 13th = 01:00 UTC.
        await Assert.That(schedule.NextRun(now)).IsEqualTo(Utc(2026, 8, 13, 1, 0));
    }

    [Test]
    public async Task Next_run_is_strictly_after_now()
    {
        var schedule = Schedule.Cron("0 3 * * *", Stockholm);
        var slot = Utc(2026, 8, 12, 1, 0); // exactly 03:00 CEST

        // Exactly at the slot: a sweep leases by advancing to NextRun, so returning the same instant
        // would re-select the job forever.
        await Assert.That(schedule.NextRun(slot)).IsEqualTo(Utc(2026, 8, 13, 1, 0));
    }

    [Test]
    public async Task Next_run_is_independent_of_the_offset_now_is_expressed_in()
    {
        // The same instant written with a different offset must produce the same run: the schedule's
        // zone decides local time, never the caller's.
        var schedule = Schedule.Cron("0 3 * * *", Stockholm);
        var utc = Utc(2026, 8, 12, 10, 0);
        var sameInstantElsewhere = new DateTimeOffset(2026, 8, 12, 16, 0, 0, TimeSpan.FromHours(6));

        await Assert.That(schedule.NextRun(sameInstantElsewhere)).IsEqualTo(schedule.NextRun(utc));
    }

    [Test]
    public async Task Six_fields_schedule_to_the_second()
    {
        var schedule = Schedule.Cron("* * * * * *", Stockholm);
        var now = Utc(2026, 8, 12, 10, 0, 0);

        await Assert.That(schedule.NextRun(now)).IsEqualTo(Utc(2026, 8, 12, 10, 0, 1));
    }

    [Test]
    public async Task Spring_forward_gap_runs_when_the_clock_finishes_the_jump()
    {
        // Stockholm springs forward 2026-03-29 02:00 CET -> 03:00 CEST, so 02:30 never happens that
        // day. The slot runs at the moment the gap closes (03:00 CEST = 01:00 UTC) rather than being
        // skipped for the day.
        var schedule = Schedule.Cron("30 2 * * *", Stockholm);
        var now = Utc(2026, 3, 29, 0, 0); // 01:00 CET, before the transition

        await Assert.That(schedule.NextRun(now)).IsEqualTo(Utc(2026, 3, 29, 1, 0));
        // And the day after, the slot is back to its ordinary local time (02:30 CEST = 00:30 UTC).
        await Assert.That(schedule.NextRun(Utc(2026, 3, 29, 1, 0))).IsEqualTo(Utc(2026, 3, 30, 0, 30));
    }

    [Test]
    public async Task Fall_back_overlap_runs_a_fixed_slot_once()
    {
        // Stockholm falls back 2026-10-25 03:00 CEST -> 02:00 CET, so 02:30 happens twice. A slot
        // named once a day runs on the first pass only (02:30 CEST = 00:30 UTC).
        var schedule = Schedule.Cron("30 2 * * *", Stockholm);

        await Assert.That(schedule.NextRun(Utc(2026, 10, 24, 23, 30))).IsEqualTo(Utc(2026, 10, 25, 0, 30));

        // From the first occurrence the next run is the following day: the repeated hour must not
        // produce a second run of a once-a-day slot.
        await Assert.That(schedule.NextRun(Utc(2026, 10, 25, 0, 30))).IsEqualTo(Utc(2026, 10, 26, 1, 30));
    }

    [Test]
    public async Task Fall_back_overlap_runs_a_recurring_slot_in_both_passes()
    {
        // The counterpart to the fixed slot: an expression that recurs within the hour keeps running
        // through the repeated hour, so 02:00 CET (01:00 UTC) follows 02:30 CEST (00:30 UTC).
        var schedule = Schedule.Cron("*/30 * * * *", Stockholm);

        await Assert.That(schedule.NextRun(Utc(2026, 10, 25, 0, 30))).IsEqualTo(Utc(2026, 10, 25, 1, 0));
    }

    // --------------------------------------------------------------------------- canonical ---

    [Test]
    public async Task Canonical_string_is_stable()
    {
        // Pins the wire format: persisted and configured schedules must stay parseable across versions.
        await Assert.That(Schedule.Cron("0 3 * * *", Stockholm).ToString())
            .IsEqualTo("cron:Europe/Stockholm:0 3 * * *");
    }

    [Test]
    public async Task Expression_is_normalized_to_one_canonical_form()
    {
        // Providers compare the stored canonical string to decide whether a schedule changed, so
        // spacing and name casing must not read as a different schedule on the next deploy.
        var schedule = Schedule.Cron("  0   3  *  *  mon\t", Stockholm);

        await Assert.That(schedule.Expression).IsEqualTo("0 3 * * MON");
        await Assert.That(schedule).IsEqualTo(Schedule.Cron("0 3 * * MON", Stockholm));
    }

    [Test]
    public async Task Round_trips_through_parse()
    {
        var schedule = Schedule.Cron("15 4,16 * * 1-5", Stockholm);

        await Assert.That(Schedule.Parse(schedule.ToString())).IsEqualTo(schedule);
    }

    [Test]
    public async Task Zone_is_part_of_identity()
    {
        await Assert.That(Schedule.Cron("0 3 * * *", Stockholm))
            .IsNotEqualTo(Schedule.Cron("0 3 * * *", "Europe/Oslo"));
    }

    [Test]
    public async Task Time_zone_id_is_the_iana_id()
    {
        await Assert.That(Schedule.Cron("0 3 * * *", Stockholm).TimeZoneId).IsEqualTo("Europe/Stockholm");
    }

    // ----------------------------------------------------------------------------- rejects ---

    [Test]
    [Arguments("* * * *")]
    [Arguments("* * * * * * *")]
    public async Task Wrong_field_count_throws_argument_exception(string expression)
    {
        await Assert.That(() => Schedule.Cron(expression, Stockholm)).Throws<ArgumentException>();
    }

    [Test]
    [Arguments("0 99 * * *")]
    [Arguments("banana")]
    [Arguments("0 3 * * NOTADAY")]
    public async Task Invalid_expression_throws_argument_exception(string expression)
    {
        await Assert.That(() => Schedule.Cron(expression, Stockholm)).Throws<ArgumentException>();
    }

    [Test]
    public async Task Expression_that_never_occurs_throws_argument_exception()
    {
        // Valid cron, but 30 February never arrives: a sweep could never advance the job past it.
        await Assert.That(() => Schedule.Cron("0 0 30 2 *", Stockholm)).Throws<ArgumentException>();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Missing_expression_throws_argument_exception(string? expression)
    {
        await Assert.That(() => Schedule.Cron(expression!, Stockholm)).Throws<ArgumentException>();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Missing_zone_id_throws_argument_exception(string? timeZoneId)
    {
        await Assert.That(() => Schedule.Cron("0 3 * * *", timeZoneId!)).Throws<ArgumentException>();
    }

    [Test]
    public async Task Null_zone_throws_argument_null()
    {
        await Assert.That(() => Schedule.Cron("0 3 * * *", (TimeZoneInfo)null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Unresolvable_zone_id_throws_argument_exception()
    {
        await Assert.That(() => Schedule.Cron("0 3 * * *", "Mars/Olympus")).Throws<ArgumentException>();
    }

    [Test]
    public async Task Non_iana_zone_is_rejected()
    {
        // Only the zone id is persisted, and any host running the sweep rehydrates it by id. A zone
        // without an IANA id — a custom zone here, a Windows id such as "W. Europe Standard Time" on
        // a Windows host — would produce a schedule other hosts cannot resolve. The zone is custom
        // rather than a real Windows id so the test asserts the same guard on every platform.
        var custom = TimeZoneInfo.CreateCustomTimeZone("Mars/Olympus", TimeSpan.Zero, "Olympus", "Olympus");

        await Assert.That(() => Schedule.Cron("0 3 * * *", custom)).Throws<ArgumentException>();
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

    [Test]
    public async Task TryParse_valid_text_returns_true_and_the_schedule()
    {
        var parsed = Schedule.TryParse("cron:Europe/Stockholm:0 3 * * *", out var schedule);

        await Assert.That(parsed).IsTrue();
        await Assert.That(schedule).IsEqualTo(Schedule.Cron("0 3 * * *", "Europe/Stockholm"));
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("gibberish")]
    [Arguments("cron:No/Zone:0 3 * * *")]
    [Arguments("cron:Europe/Stockholm:not cron")]
    public async Task TryParse_invalid_text_returns_false_without_throwing(string? text)
    {
        var parsed = Schedule.TryParse(text, out var schedule);

        await Assert.That(parsed).IsFalse();
        await Assert.That(schedule).IsNull();
    }
}
