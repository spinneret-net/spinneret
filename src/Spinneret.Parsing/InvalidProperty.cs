namespace Spinneret.Parsing;

/// <summary>
/// One invalid property from a parse: the dotted property path ("Address.Street",
/// "Items[3].Name") and the error that occurred there.
/// </summary>
public sealed record InvalidProperty<T>
{
    public required string PropertyName { get; init; }
    public required T Error { get; init; }
}
