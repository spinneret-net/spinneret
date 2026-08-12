using NodaTime;
using NodaTime.TimeZones;

namespace Spinneret.Scheduler.Tests;

public class ScheduleTests
{
    private static readonly DateTimeZone Stockholm = DateTimeZoneProviders.Tzdb["Europe/Stockholm"];

    // ------------------------------------------------------------------------------ Every ---

    [Test]
    public async Task Every_next_run_adds_the_interval()
    {
        var schedule = Schedule.Every(Duration.FromMinutes(15));
        var now = Instant.FromUtc(2026, 8, 12, 10, 0);

        await Assert.That(schedule.NextRun(now)).IsEqualTo(now + Duration.FromMinutes(15));
    }

    [Test]
    public async Task Every_one_second_is_the_minimum_interval()
    {
        var schedule = Schedule.Every(Duration.FromSeconds(1));
        var now = Instant.FromUtc(2026, 8, 12, 10, 0);

        await Assert.That(schedule.NextRun(now)).IsEqualTo(now + Duration.FromSeconds(1));
    }

    [Test]
    [Arguments(0)]
    [Arguments(500)]
    [Arguments(999)]
    [Arguments(-1000)]
    public async Task Every_below_one_second_throws_argument_out_of_range(int milliseconds)
    {
        await Assert.That(() => Schedule.Every(Duration.FromMilliseconds(milliseconds)))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Every_canonical_string_is_stable()
    {
        // Pins the wire format: persisted schedules must stay parseable across versions.
        await Assert.That(Schedule.Every(Duration.FromMinutes(15)).ToString())
            .IsEqualTo("every:0:00:15:00");
    }

    [Test]
    public async Task Every_round_trips_through_parse()
    {
        var schedule = Schedule.Every(Duration.FromHours(3) + Duration.FromSeconds(7));

        await Assert.That(Schedule.Parse(schedule.ToString())).IsEqualTo(schedule);
    }

    // ------------------------------------------------------------------------------ Daily ---

    [Test]
    public async Task Daily_runs_later_the_same_day_when_a_time_is_still_ahead()
    {
        var schedule = Schedule.Daily(Stockholm, new LocalTime(1, 0));
        // 2026-08-11 23:30 UTC = 2026-08-12 01:30 CEST... use a moment clearly before 01:00 local:
        // 2026-08-12 10:00 UTC = 12:00 local, so next run is tomorrow 01:00 CEST = 23:00 UTC today+0.
        var now = Instant.FromUtc(2026, 8, 11, 20, 0); // 22:00 local, before next day's 01:00

        // Next 01:00 Stockholm (CEST, UTC+2) after 2026-08-11 22:00 local is 2026-08-12 01:00 local
        // = 2026-08-11 23:00 UTC.
        await Assert.That(schedule.NextRun(now)).IsEqualTo(Instant.FromUtc(2026, 8, 11, 23, 0));
    }

    [Test]
    public async Task Daily_wraps_to_the_first_time_of_the_next_day()
    {
        var schedule = Schedule.Daily(Stockholm, new LocalTime(1, 0));
        var now = Instant.FromUtc(2026, 8, 12, 10, 0); // 12:00 local, past 01:00

        await Assert.That(schedule.NextRun(now)).IsEqualTo(Instant.FromUtc(2026, 8, 12, 23, 0));
    }

    [Test]
    public async Task Daily_picks_the_nearest_of_multiple_times()
    {
        var schedule = Schedule.Daily(Stockholm, new LocalTime(7, 0), new LocalTime(20, 0));
        var now = Instant.FromUtc(2026, 8, 12, 10, 0); // 12:00 local: 07:00 passed, 20:00 ahead

        await Assert.That(schedule.NextRun(now)).IsEqualTo(Instant.FromUtc(2026, 8, 12, 18, 0));
    }

    [Test]
    public async Task Daily_normalizes_unsorted_and_duplicate_times()
    {
        var schedule = Schedule.Daily(Stockholm, new LocalTime(20, 0), new LocalTime(7, 0), new LocalTime(7, 0));

        await Assert.That(schedule.ToString()).IsEqualTo("daily:Europe/Stockholm:07:00:00,20:00:00");
        await Assert.That(schedule)
            .IsEqualTo(Schedule.Daily(Stockholm, new LocalTime(7, 0), new LocalTime(20, 0)));
    }

    [Test]
    public async Task Daily_next_run_is_strictly_after_now()
    {
        var schedule = Schedule.Daily(Stockholm, new LocalTime(1, 0));
        var slot = Instant.FromUtc(2026, 8, 11, 23, 0); // exactly 01:00 local

        // Exactly at the slot: the sweep leases by advancing to NextRun, so returning the same
        // instant would re-select the job forever.
        await Assert.That(schedule.NextRun(slot)).IsEqualTo(Instant.FromUtc(2026, 8, 12, 23, 0));
    }

    [Test]
    public async Task Daily_spring_forward_gap_shifts_the_run_past_the_gap()
    {
        // Stockholm springs forward 2026-03-29 02:00 CET -> 03:00 CEST: 02:30 does not exist that
        // day. The lenient mapping runs it shifted past the gap (03:30 CEST = 01:30 UTC) instead of
        // skipping the day or throwing.
        var schedule = Schedule.Daily(Stockholm, new LocalTime(2, 30));
        var now = Instant.FromUtc(2026, 3, 29, 0, 0); // 01:00 CET, before the transition

        await Assert.That(schedule.NextRun(now)).IsEqualTo(Instant.FromUtc(2026, 3, 29, 1, 30));
    }

    [Test]
    public async Task Daily_fall_back_overlap_runs_once_at_the_first_occurrence()
    {
        // Stockholm falls back 2026-10-25 03:00 CEST -> 02:00 CET: 02:30 occurs twice. The lenient
        // mapping takes the earlier occurrence (02:30 CEST = 00:30 UTC) — once, not twice.
        var schedule = Schedule.Daily(Stockholm, new LocalTime(2, 30));
        var now = Instant.FromUtc(2026, 10, 24, 23, 30); // 01:30 CEST on the 25th

        await Assert.That(schedule.NextRun(now)).IsEqualTo(Instant.FromUtc(2026, 10, 25, 0, 30));

        // And from just after the first occurrence, the next run is the following day — the
        // repeated hour must not yield a second run.
        var justAfter = Instant.FromUtc(2026, 10, 25, 0, 31);
        await Assert.That(schedule.NextRun(justAfter)).IsEqualTo(Instant.FromUtc(2026, 10, 26, 1, 30));
    }

    [Test]
    public async Task Daily_requires_at_least_one_time()
    {
        await Assert.That(() => Schedule.Daily(Stockholm)).Throws<ArgumentException>();
    }

    [Test]
    public async Task Daily_requires_a_zone()
    {
        await Assert.That(() => Schedule.Daily(null!, new LocalTime(1, 0))).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Daily_round_trips_through_parse()
    {
        var schedule = Schedule.Daily(Stockholm, new LocalTime(1, 0), new LocalTime(13, 30));

        await Assert.That(Schedule.Parse(schedule.ToString())).IsEqualTo(schedule);
    }

    // ------------------------------------------------------------------------------ Parse ---

    [Test]
    [Arguments("cron:* * * * *")]
    [Arguments("every")]
    [Arguments("daily")]
    [Arguments("nonsense")]
    public async Task Parse_unrecognized_forms_throw_format_exception(string text)
    {
        await Assert.That(() => Schedule.Parse(text)).Throws<FormatException>();
    }

    [Test]
    public async Task Parse_invalid_interval_duration_throws_format_exception()
    {
        await Assert.That(() => Schedule.Parse("every:banana")).Throws<FormatException>();
    }

    [Test]
    [Arguments("every:-0:00:05:00")]
    [Arguments("every:0:00:00:00.5")]
    public async Task Parse_parseable_but_out_of_range_interval_throws_format_exception(string text)
    {
        // Parse's contract is FormatException for any non-canonical text — a duration the pattern
        // accepts but the schedule rejects (negative, sub-second) must not leak a different type.
        await Assert.That(() => Schedule.Parse(text)).Throws<FormatException>();
    }

    [Test]
    public async Task Daily_rejects_non_tzdb_zones()
    {
        // Only the zone id is persisted and rehydrated via TZDB; a zone TZDB cannot resolve by id —
        // a custom zone here, a Windows zone id on a BCL provider — would produce a schedule the
        // dispatch sweep can never parse back. The zone is hand-rolled rather than taken from the
        // BCL provider, whose ids are Windows-only, so the test asserts the same on every platform.
        await Assert.That(() => Schedule.Daily(new UnknownZone(), new LocalTime(7, 0)))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Parse_unknown_time_zone_throws_format_exception()
    {
        await Assert.That(() => Schedule.Parse("daily:Mars/Olympus:01:00:00")).Throws<FormatException>();
    }

    [Test]
    public async Task Parse_invalid_time_of_day_throws_format_exception()
    {
        await Assert.That(() => Schedule.Parse("daily:Europe/Stockholm:25:99:00")).Throws<FormatException>();
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
