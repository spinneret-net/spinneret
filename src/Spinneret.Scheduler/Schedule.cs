using NodaTime;
using NodaTime.Text;

namespace Spinneret.Scheduler;

/// <summary>
/// When a recurring job runs: either every fixed interval, or at fixed local times of day in a
/// time zone (DST-aware). The hierarchy is closed — providers persist a schedule as the canonical
/// string form (<see cref="ToString"/> / <see cref="Parse"/>), so an open hierarchy would let an
/// application register a schedule the dispatch sweep could not rehydrate.
/// </summary>
public abstract record Schedule
{
    private protected Schedule() { }

    /// <summary>A run every <paramref name="interval"/>, measured from the previous run.</summary>
    public static Schedule Every(Duration interval) => new IntervalSchedule(interval);

    /// <summary>
    /// A run at each of <paramref name="times"/> (local times of day in <paramref name="zone"/>)
    /// every day. Times are DST-aware: a time that falls in a spring-forward gap runs shifted
    /// past the gap, and a time repeated by a fall-back overlap runs once, at its first occurrence.
    /// </summary>
    public static Schedule Daily(DateTimeZone zone, params LocalTime[] times) => new DailySchedule(zone, times);

    /// <summary>
    /// The next run strictly after <paramref name="now"/>. Strictness is what makes the dispatch
    /// sweep's lease-by-advancing-the-run-time terminate: a NextRun that could return
    /// <paramref name="now"/> itself would re-select the job forever.
    /// </summary>
    public abstract Instant NextRun(Instant now);

    /// <summary>Canonical persistable form; round-trips through <see cref="Parse"/>.</summary>
    public abstract override string ToString();

    /// <summary>Rehydrates a schedule persisted via <see cref="ToString"/>.</summary>
    /// <exception cref="FormatException">The text is not a canonical schedule.</exception>
    public static Schedule Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var parts = text.Split(':', 2);
        return parts[0] switch
        {
            "every" when parts.Length == 2 => IntervalSchedule.ParseBody(parts[1]),
            "daily" when parts.Length == 2 => DailySchedule.ParseBody(parts[1]),
            _ => throw new FormatException(
                $"Unrecognized schedule '{text}'. Expected 'every:<duration>' or 'daily:<zone>:<time>[,<time>...]'."),
        };
    }
}

public sealed record IntervalSchedule : Schedule
{
    private static readonly DurationPattern Pattern = DurationPattern.Roundtrip;

    public IntervalSchedule(Duration interval)
    {
        if (interval < Duration.FromSeconds(1))
            throw new ArgumentOutOfRangeException(nameof(interval),
                "A recurring interval must be at least one second: providers persist run times with "
                + "second precision, so a sub-second interval could re-select a job in a tight loop.");

        Interval = interval;
    }

    public Duration Interval { get; }

    public override Instant NextRun(Instant now) => now + Interval;

    public override string ToString() => $"every:{Pattern.Format(Interval)}";

    internal static Schedule ParseBody(string body)
    {
        var result = Pattern.Parse(body);
        if (!result.Success)
            throw new FormatException($"Invalid interval duration '{body}'.", result.Exception);

        try
        {
            return new IntervalSchedule(result.Value);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            // Parse promises FormatException for any non-canonical text — a parseable but
            // out-of-range duration (negative, sub-second) is still non-canonical.
            throw new FormatException($"Invalid interval duration '{body}': {ex.Message}", ex);
        }
    }
}

public sealed record DailySchedule : Schedule
{
    private static readonly LocalTimePattern TimePattern = LocalTimePattern.CreateWithInvariantCulture("HH:mm:ss");

    private readonly LocalTime[] _times;

    public DailySchedule(DateTimeZone zone, LocalTime[] times)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(times);
        if (times.Length == 0)
            throw new ArgumentException("A daily schedule requires at least one time of day.", nameof(times));
        // Only the zone id is persisted, and Parse rehydrates it through TZDB — a BCL or custom
        // zone would produce a schedule the dispatch sweep can never parse back.
        if (DateTimeZoneProviders.Tzdb.GetZoneOrNull(zone.Id) is null)
            throw new ArgumentException(
                $"Time zone '{zone.Id}' is not a TZDB zone. Daily schedules persist only the zone id "
                + "and rehydrate it via DateTimeZoneProviders.Tzdb, so use a TZDB zone (e.g. 'Europe/Stockholm').",
                nameof(zone));

        Zone = zone;
        _times = times.Distinct().Order().ToArray();
    }

    public DateTimeZone Zone { get; }

    public IReadOnlyList<LocalTime> Times => _times;

    public override Instant NextRun(Instant now)
    {
        // Walk the local slots in order and return the first that maps strictly after now. Slots are
        // mapped leniently, so a DST transition never throws: a slot in a spring-forward gap shifts
        // past the gap, and a slot in a fall-back overlap takes its first occurrence. Both mappings
        // can land at or before now (a shifted slot can collide with the next one; an overlap's first
        // occurrence lies in the past during the repeated hour), which is why the filter is on the
        // mapped instant rather than on the local time-of-day.
        var today = now.InZone(Zone).Date;

        for (var day = 0; day <= 2; day++)
        {
            var date = today.PlusDays(day);
            foreach (var time in _times)
            {
                var instant = Zone.AtLeniently(date.At(time)).ToInstant();
                if (instant > now)
                    return instant;
            }
        }

        // Unreachable: tomorrow's slots always map after now (no transition spans a whole day).
        throw new InvalidOperationException($"No next run found for '{this}' after {now}.");
    }

    public bool Equals(DailySchedule? other) =>
        other is not null && Zone.Id == other.Zone.Id && _times.SequenceEqual(other._times);

    public override int GetHashCode() => HashCode.Combine(Zone.Id, _times.Length, _times[0]);

    public override string ToString() =>
        $"daily:{Zone.Id}:{string.Join(',', _times.Select(TimePattern.Format))}";

    internal static Schedule ParseBody(string body)
    {
        var parts = body.Split(':', 2);
        if (parts.Length != 2)
            throw new FormatException($"Invalid daily schedule '{body}'. Expected '<zone>:<time>[,<time>...]'.");

        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(parts[0])
            ?? throw new FormatException($"Unknown time zone '{parts[0]}' in daily schedule.");

        var times = parts[1].Split(',').Select(t =>
        {
            var result = TimePattern.Parse(t);
            if (!result.Success)
                throw new FormatException($"Invalid time of day '{t}' in daily schedule.", result.Exception);
            return result.Value;
        }).ToArray();

        try
        {
            return new DailySchedule(zone, times);
        }
        catch (ArgumentException ex)
        {
            throw new FormatException($"Invalid daily schedule '{body}': {ex.Message}", ex);
        }
    }
}
