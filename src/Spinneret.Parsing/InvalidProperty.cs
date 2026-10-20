namespace Spinneret.Parsing;

/// <summary>An invalid property: its dotted path (e.g. "Items[3].Name") and its error.</summary>
public sealed record InvalidProperty<T>
{
    public required string PropertyName { get; init; }
    public required T Error { get; init; }
}
