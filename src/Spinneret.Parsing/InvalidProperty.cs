namespace Spinneret.Parsing;

public class InvalidProperty<T>
{
    public required string PropertyName { get; init; }
    public required T Error { get; init; }
}