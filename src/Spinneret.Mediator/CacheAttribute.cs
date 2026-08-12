namespace Spinneret.Mediator;

[AttributeUsage(AttributeTargets.Class)]
public sealed class CacheAttribute(int seconds, params object[] tags) : Attribute
{
    public TimeSpan Duration { get; } = ValidateDuration(seconds);
    public IReadOnlyList<Enum> Tags { get; } = ValidateTags(tags);

    private static TimeSpan ValidateDuration(int seconds)
    {
        if (seconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "CacheAttribute duration must be greater than zero seconds.");
        return TimeSpan.FromSeconds(seconds);
    }

    private static Enum[] ValidateTags(object[] tags)
    {
        var result = new Enum[tags.Length];
        for (var i = 0; i < tags.Length; i++)
        {
            if (tags[i] is not Enum e)
                throw new ArgumentException($"CacheAttribute tags must be enum values (got {tags[i]?.GetType().Name ?? "null"}).", nameof(tags));
            result[i] = e;
        }
        return result;
    }
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class InvalidateCacheAttribute(params object[] tags) : Attribute
{
    public IReadOnlyList<Enum> Tags { get; } = ValidateTags(tags);

    private static IReadOnlyList<Enum> ValidateTags(object[] tags)
    {
        var result = new Enum[tags.Length];
        for (var i = 0; i < tags.Length; i++)
        {
            if (tags[i] is not Enum e)
                throw new ArgumentException($"InvalidateCacheAttribute tags must be enum values (got {tags[i]?.GetType().Name ?? "null"}).", nameof(tags));
            result[i] = e;
        }
        return result;
    }
}
