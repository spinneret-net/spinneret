using System.Diagnostics.CodeAnalysis;
using Cronos;

namespace Spinneret.Scheduler;

/// <summary>
/// A cron expression evaluated in a fixed IANA time zone, keeping its wall-clock slot across DST.
/// </summary>
/// <remarks>
/// Occurrences are only as prompt as the dispatch sweep's interval.
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
    /// Runs at every occurrence of <paramref name="expression"/> in local time in <paramref name="timeZoneId"/>.
    /// </summary>
    /// <param name="expression">Five cron fields, or six when the first is seconds, e.g. <c>0 3 * * *</c>.</param>
    /// <param name="timeZoneId">An IANA time zone id, e.g. <c>Europe/Stockholm</c>.</param>
    /// <exception cref="ArgumentException">
    /// Invalid cron, a date that never occurs, or an unresolvable IANA zone.
    /// </exception>
    public static Schedule Cron(string expression, string timeZoneId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);

        return Cron(expression, ResolveZone(timeZoneId));
    }

    /// <summary>
    /// Runs at every occurrence of <paramref name="expression"/> in <paramref name="zone"/>, which must be an IANA zone.
    /// </summary>
    public static Schedule Cron(string expression, TimeZoneInfo zone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        ArgumentNullException.ThrowIfNull(zone);

        // Only the id is persisted; a Windows id would be unreadable to a sweep on Linux.
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

        // Reject dates that never arrive (31 February), which no sweep could ever advance.
        if (parsed.GetNextOccurrence(DateTimeOffset.UtcNow, zone) is null)
            throw new ArgumentException(
                $"Cron expression '{normalized}' has no future occurrence in '{zone.Id}'.", nameof(expression));

        return new Schedule(zone, parsed, normalized);
    }

    /// <summary>The next run strictly after <paramref name="now"/>.</summary>
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
            // Parse promises FormatException for any non-canonical text.
            throw new FormatException($"Invalid schedule '{text}': {ex.Message}", ex);
        }
    }

    /// <summary>Non-throwing counterpart of <see cref="Parse"/>.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out Schedule? schedule)
    {
        schedule = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        try
        {
            schedule = Parse(text);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public bool Equals(Schedule? other) =>
        other is not null && TimeZoneId == other.TimeZoneId && Expression == other.Expression;

    public override int GetHashCode() => HashCode.Combine(TimeZoneId, Expression);

    // Canonical form matters: providers compare the stored string to detect a schedule change.
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
