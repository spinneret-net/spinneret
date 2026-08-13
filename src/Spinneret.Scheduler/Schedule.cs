using Cronos;
using NodaTime;

namespace Spinneret.Scheduler;

/// <summary>
/// When a recurring job runs: a cron expression evaluated in a fixed time zone, so a schedule keeps
/// its wall-clock slot across DST transitions. Providers persist a schedule as the canonical string
/// form (<see cref="ToString"/> / <see cref="Parse"/>) and rehydrate every stored schedule through
/// <see cref="Parse"/>, which is also how an application reads a schedule out of configuration.
/// </summary>
/// <remarks>
/// Occurrences are only as prompt as the provider's dispatch sweep: a slot finer than the sweep's
/// period is reached on the following sweep rather than at the slot itself.
/// </remarks>
public sealed record Schedule
{
    private readonly CronExpression _expression;

    // Occurrences are computed against the system zone database, not TZDB: the zone is TZDB-checked
    // for its id — that is what round-trips through Parse — but the DST rules applied to each
    // occurrence come from TimeZoneInfo. Both track IANA, so they can only disagree on a host whose
    // zone data is older than NodaTime's.
    private readonly TimeZoneInfo _zoneInfo;

    private Schedule(DateTimeZone zone, TimeZoneInfo zoneInfo, CronExpression expression, string expressionText)
    {
        Zone = zone;
        _zoneInfo = zoneInfo;
        _expression = expression;
        Expression = expressionText;
    }

    /// <summary>The zone the expression's fields are interpreted in.</summary>
    public DateTimeZone Zone { get; }

    /// <summary>The normalized cron expression: five fields, or six when the first is seconds.</summary>
    public string Expression { get; }

    /// <summary>
    /// A run at every occurrence of <paramref name="expression"/> — five cron fields, or six when
    /// the first is seconds — in local time in <paramref name="zone"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The expression is not valid cron, describes a date that never occurs, or the zone is not a
    /// TZDB zone this host can resolve.
    /// </exception>
    public static Schedule Cron(DateTimeZone zone, string expression)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);

        // Only the zone id is persisted, and Parse rehydrates it through TZDB — a BCL or custom zone
        // would produce a schedule the dispatch sweep can never parse back.
        if (DateTimeZoneProviders.Tzdb.GetZoneOrNull(zone.Id) is null)
            throw new ArgumentException(
                $"Time zone '{zone.Id}' is not a TZDB zone. Schedules persist only the zone id and "
                + "rehydrate it via DateTimeZoneProviders.Tzdb, so use a TZDB zone (e.g. 'Europe/Stockholm').",
                nameof(zone));

        var normalized = Normalize(expression);
        var fieldCount = normalized.Count(c => c == ' ') + 1;
        var format = fieldCount switch
        {
            5 => CronFormat.Standard,
            6 => CronFormat.IncludeSeconds,
            _ => throw new ArgumentException(
                $"Cron expression '{expression}' has {fieldCount} fields; expected five, or six when "
                + "the first field is seconds.", nameof(expression)),
        };

        CronExpression parsed;
        try
        {
            parsed = CronExpression.Parse(normalized, format);
        }
        catch (CronFormatException ex)
        {
            throw new ArgumentException($"Invalid cron expression '{expression}': {ex.Message}", nameof(expression), ex);
        }

        var zoneInfo = ResolveZoneInfo(zone);

        // A syntactically valid expression can still describe a date that never arrives (31 February).
        // Reject it here, where the registering code sees it, rather than letting it reach a job
        // document that no sweep can ever advance. Impossible dates are impossible at every instant,
        // so probing from the current one is enough to tell them apart.
        if (parsed.GetNextOccurrence(DateTimeOffset.UtcNow, zoneInfo) is null)
            throw new ArgumentException(
                $"Cron expression '{normalized}' has no future occurrence in '{zone.Id}'.", nameof(expression));

        return new Schedule(zone, zoneInfo, parsed, normalized);
    }

    /// <summary>
    /// The next run strictly after <paramref name="now"/>. Strictness is what makes a dispatch
    /// sweep's lease-by-advancing-the-run-time terminate: a NextRun that could return
    /// <paramref name="now"/> itself would re-select the job forever.
    /// </summary>
    public Instant NextRun(Instant now)
    {
        var next = _expression.GetNextOccurrence(now.ToDateTimeOffset(), _zoneInfo)
            ?? throw new InvalidOperationException($"No next run found for '{this}' after {now}.");

        return Instant.FromDateTimeOffset(next);
    }

    /// <summary>Canonical persistable form; round-trips through <see cref="Parse"/>.</summary>
    public override string ToString() => $"cron:{Zone.Id}:{Expression}";

    /// <summary>Rehydrates a schedule persisted or configured via <see cref="ToString"/>.</summary>
    /// <exception cref="FormatException">The text is not a canonical schedule.</exception>
    public static Schedule Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var parts = text.Split(':', 3);

        // Pre-cron schedules named their form in the same position, so say what to do about them
        // instead of reporting them as gibberish — a stored one surfaces through the sweep, far from
        // whoever wrote it.
        if (parts[0] is "every" or "daily")
            throw new FormatException(
                $"Schedule '{text}' is in the pre-cron form. Re-register the job to replace it with "
                + "'cron:<zone>:<expression>'.");

        if (parts.Length != 3 || parts[0] != "cron")
            throw new FormatException($"Unrecognized schedule '{text}'. Expected 'cron:<zone>:<expression>'.");

        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(parts[1])
            ?? throw new FormatException($"Unknown time zone '{parts[1]}' in schedule '{text}'.");

        try
        {
            return Cron(zone, parts[2]);
        }
        catch (ArgumentException ex)
        {
            // Parse promises FormatException for any non-canonical text — a well-formed wrapper
            // around an expression the schedule rejects is still non-canonical.
            throw new FormatException($"Invalid schedule '{text}': {ex.Message}", ex);
        }
    }

    public bool Equals(Schedule? other) =>
        other is not null && Zone.Id == other.Zone.Id && Expression == other.Expression;

    public override int GetHashCode() => HashCode.Combine(Zone.Id, Expression);

    /// <summary>
    /// Single-spaced and upper-cased, so expressions that differ only in whitespace or in the case
    /// of a name (<c>MON</c>, <c>JAN</c>) produce one canonical string — providers compare the
    /// stored string to decide whether a schedule changed.
    /// </summary>
    private static string Normalize(string expression) =>
        string.Join(' ', expression.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();

    private static TimeZoneInfo ResolveZoneInfo(DateTimeZone zone)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(zone.Id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ArgumentException(
                $"Time zone '{zone.Id}' is a TZDB zone this host cannot resolve, so cron occurrences "
                + "cannot be computed in it.", nameof(zone), ex);
        }
    }
}
