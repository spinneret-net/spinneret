using Cronos;

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
    private readonly TimeZoneInfo _zone;

    private Schedule(TimeZoneInfo zone, CronExpression expression, string expressionText)
    {
        _zone = zone;
        _expression = expression;
        TimeZoneId = zone.Id;
        Expression = expressionText;
    }

    /// <summary>The IANA id of the zone the expression's fields are interpreted in.</summary>
    public string TimeZoneId { get; }

    /// <summary>The normalized cron expression: five fields, or six when the first is seconds.</summary>
    public string Expression { get; }

    /// <summary>
    /// A run at every occurrence of <paramref name="expression"/> — five cron fields, or six when
    /// the first is seconds — in local time in the zone named by <paramref name="timeZoneId"/>.
    /// </summary>
    /// <param name="expression">The cron expression, e.g. <c>0 3 * * *</c>.</param>
    /// <param name="timeZoneId">An IANA time zone id, e.g. <c>Europe/Stockholm</c>.</param>
    /// <exception cref="ArgumentException">
    /// The expression is not valid cron, describes a date that never occurs, or the id is not an
    /// IANA zone this host can resolve.
    /// </exception>
    public static Schedule Cron(string expression, string timeZoneId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);

        return Cron(expression, ResolveZone(timeZoneId));
    }

    /// <summary>
    /// A run at every occurrence of <paramref name="expression"/> in local time in
    /// <paramref name="zone"/>, which must be an IANA zone — see <see cref="Cron(string, string)"/>.
    /// </summary>
    public static Schedule Cron(string expression, TimeZoneInfo zone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        ArgumentNullException.ThrowIfNull(zone);

        // Only the zone id is persisted, and Parse rehydrates it by id on whichever host runs the
        // dispatch sweep. Windows ids resolve on Windows but nowhere else, so a schedule registered
        // with one would be unreadable to a Linux sweep — reject it here, where the caller sees it.
        if (!zone.HasIanaId)
            throw new ArgumentException(
                $"Time zone '{zone.Id}' is not an IANA zone. Schedules persist only the zone id and "
                + "rehydrate it by id on any host, so use an IANA id (e.g. 'Europe/Stockholm'). "
                + "TimeZoneInfo.TryConvertWindowsIdToIanaId converts a Windows id.",
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

        // A syntactically valid expression can still describe a date that never arrives (31 February).
        // Reject it here, where the registering code sees it, rather than letting it reach a job
        // document that no sweep can ever advance. Impossible dates are impossible at every instant,
        // so probing from the current one is enough to tell them apart.
        if (parsed.GetNextOccurrence(DateTimeOffset.UtcNow, zone) is null)
            throw new ArgumentException(
                $"Cron expression '{normalized}' has no future occurrence in '{zone.Id}'.", nameof(expression));

        return new Schedule(zone, parsed, normalized);
    }

    /// <summary>
    /// The next run strictly after <paramref name="now"/>. Strictness is what makes a dispatch
    /// sweep's lease-by-advancing-the-run-time terminate: a NextRun that could return
    /// <paramref name="now"/> itself would re-select the job forever.
    /// </summary>
    public DateTimeOffset NextRun(DateTimeOffset now) =>
        _expression.GetNextOccurrence(now, _zone)
        ?? throw new InvalidOperationException($"No next run found for '{this}' after {now:O}.");

    /// <summary>Canonical persistable form; round-trips through <see cref="Parse"/>.</summary>
    public override string ToString() => $"cron:{TimeZoneId}:{Expression}";

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

        TimeZoneInfo zone;
        try
        {
            zone = ResolveZone(parts[1]);
        }
        catch (ArgumentException ex)
        {
            throw new FormatException($"Unknown time zone '{parts[1]}' in schedule '{text}'.", ex);
        }

        try
        {
            return Cron(parts[2], zone);
        }
        catch (ArgumentException ex)
        {
            // Parse promises FormatException for any non-canonical text — a well-formed wrapper
            // around an expression the schedule rejects is still non-canonical.
            throw new FormatException($"Invalid schedule '{text}': {ex.Message}", ex);
        }
    }

    public bool Equals(Schedule? other) =>
        other is not null && TimeZoneId == other.TimeZoneId && Expression == other.Expression;

    public override int GetHashCode() => HashCode.Combine(TimeZoneId, Expression);

    /// <summary>
    /// Single-spaced and upper-cased, so expressions that differ only in whitespace or in the case
    /// of a name (<c>MON</c>, <c>JAN</c>) produce one canonical string — providers compare the
    /// stored string to decide whether a schedule changed.
    /// </summary>
    private static string Normalize(string expression) =>
        string.Join(' ', expression.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();

    private static TimeZoneInfo ResolveZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ArgumentException(
                $"Time zone '{timeZoneId}' cannot be resolved on this host, so cron occurrences cannot "
                + "be computed in it. Use an IANA id (e.g. 'Europe/Stockholm'); note that resolving "
                + "IANA ids on Windows requires ICU, which globalization-invariant mode disables.",
                nameof(timeZoneId), ex);
        }
    }
}
