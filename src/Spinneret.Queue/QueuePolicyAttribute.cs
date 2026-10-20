using System.Globalization;

namespace Spinneret.Queue;

/// <summary>Declares a command type's <see cref="QueuePolicy"/>; durations are invariant <see cref="TimeSpan"/> strings.</summary>
/// <remarks>Parsed at startup, so an invalid value fails the host at boot.</remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class QueuePolicyAttribute : Attribute
{
    public string? Channel { get; set; }
    public int MaxAttempts { get; set; } = QueuePolicy.DefaultMaxAttempts;
    public string MaxAge { get; set; } = QueuePolicy.DefaultMaxAge;
    public string MinBackoff { get; set; } = QueuePolicy.DefaultMinBackoff;
    public string MaxBackoff { get; set; } = QueuePolicy.DefaultMaxBackoff;
    public ErrorResultAction OnErrorResult { get; set; } = ErrorResultAction.DeadLetter;
    public ExhaustedAction OnExhausted { get; set; } = ExhaustedAction.DeadLetter;

    internal QueuePolicy ToPolicy()
    {
        if (MaxAttempts < 1)
            throw new FormatException($"{nameof(MaxAttempts)} must be at least 1, was {MaxAttempts}.");

        var minBackoff = Parse(MinBackoff, nameof(MinBackoff));
        var maxBackoff = Parse(MaxBackoff, nameof(MaxBackoff));
        if (minBackoff > maxBackoff)
            throw new FormatException(
                $"{nameof(MinBackoff)} ({MinBackoff}) must not exceed {nameof(MaxBackoff)} ({MaxBackoff}).");

        return new QueuePolicy
        {
            Channel = Channel,
            MaxAttempts = MaxAttempts,
            MaxAge = Parse(MaxAge, nameof(MaxAge)),
            MinBackoff = minBackoff,
            MaxBackoff = maxBackoff,
            OnErrorResult = OnErrorResult,
            OnExhausted = OnExhausted,
        };
    }

    private static TimeSpan Parse(string value, string property)
    {
        if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed) || parsed <= TimeSpan.Zero)
            throw new FormatException($"{property} must be a positive TimeSpan, was '{value}'.");

        return parsed;
    }
}
