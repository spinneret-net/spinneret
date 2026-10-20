namespace Spinneret.Functional;

/// <summary>A type with a single value, used where a type argument is required but carries nothing.</summary>
public readonly record struct Unit
{
    /// <summary>The single value of the type.</summary>
    public static Unit Value => default;
}
