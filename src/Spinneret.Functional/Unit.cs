namespace Spinneret.Functional;

/// <summary>
/// The type with exactly one value, used where a type argument is required but no
/// information is carried — e.g. a request that produces no response.
/// </summary>
public readonly record struct Unit
{
    /// <summary>The single value of the type.</summary>
    public static Unit Value => default;
}
