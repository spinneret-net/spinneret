using System.Diagnostics;
using System.Text.Json.Serialization;

namespace Spinneret.Functional;

/// <summary>
/// A value that is exactly one of two types. Unlike <see cref="Result{TOk, TError}"/> the two
/// cases are peers — neither is the "failure". Commonly used to union two error types.
/// </summary>
public sealed record Either<T1, T2>
{
    [JsonInclude]
    private readonly int tag;

    [JsonInclude]
    private readonly T1? value1;

    [JsonInclude]
    private readonly T2? value2;

    [JsonConstructor]
#pragma warning disable IDE0051 // Private member is used for deserialization only
    private Either(int tag, T1? value1, T2? value2)
#pragma warning restore IDE0051
    {
        this.tag = tag;
        this.value1 = value1;
        this.value2 = value2;
    }

    public Either(T1 value)
    {
        tag = 1;
        value1 = value;
        value2 = default;
    }

    public Either(T2 value)
    {
        tag = 2;
        value1 = default;
        value2 = value;
    }

    /// <summary>Creates an Either holding the first case. Use when the constructors are ambiguous.</summary>
    public static Either<T1, T2> First(T1 value)
    {
        return new Either<T1, T2>(value);
    }

    /// <summary>Creates an Either holding the second case. Use when the constructors are ambiguous.</summary>
    public static Either<T1, T2> Second(T2 value)
    {
        return new Either<T1, T2>(value);
    }

    /// <summary>Collapses both cases into a single value.</summary>
    public T Match<T>(
        Func<T1, T> f1,
        Func<T2, T> f2
    )
    {
        return tag switch
        {
            1 => f1(value1!),
            2 => f2(value2!),
            _ => throw new UnreachableException($"Either tag {tag} is neither 1 nor 2."),
        };
    }

    /// <summary>Runs exactly one of the two actions, depending on the case.</summary>
    public void Switch(
        Action<T1> f1,
        Action<T2> f2
    )
    {
        switch (tag)
        {
            case 1:
                f1(value1!);
                return;
            case 2:
                f2(value2!);
                return;
            default:
                throw new UnreachableException($"Either tag {tag} is neither 1 nor 2.");
        }
    }

    /// <summary>Transforms whichever case is present.</summary>
    public Either<T3, T4> Map<T3, T4>(
        Func<T1, T3> f1,
        Func<T2, T4> f2
    )
    {
        return tag switch
        {
            1 => new Either<T3, T4>(f1(value1!)),
            2 => new Either<T3, T4>(f2(value2!)),
            _ => throw new UnreachableException($"Either tag {tag} is neither 1 nor 2."),
        };
    }

    /// <summary>Swaps the two cases.</summary>
    public Either<T2, T1> Reverse()
    {
        return new(tag == 1 ? 2 : 1, value2, value1);
    }

    /// <summary>Transforms whichever case is present with a result-producing function, short-circuiting on error.</summary>
    public Result<Either<T3, T4>, TError> TraverseResult<T3, T4, TError>(
        Func<T1, Result<T3, TError>> f1,
        Func<T2, Result<T4, TError>> f2
    )
    {
        return tag switch
        {
            1 => f1(value1!).Map(x => new Either<T3, T4>(x)),
            2 => f2(value2!).Map(x => new Either<T3, T4>(x)),
            _ => throw new UnreachableException($"Either tag {tag} is neither 1 nor 2."),
        };
    }

    public override string ToString()
    {
        return tag == 1 ? $"First({value1})" : $"Second({value2})";
    }
}
