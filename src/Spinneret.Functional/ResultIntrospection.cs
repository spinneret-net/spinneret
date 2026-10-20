namespace Spinneret.Functional;

/// <summary>Reads the state of a boxed <see cref="Result{TError}"/> or <see cref="Result{TOk,TError}"/>, unwrapping nested results.</summary>
public static class ResultIntrospection
{
    /// <summary>True when <paramref name="value"/> is a <c>Result</c> of either shape.</summary>
    public static bool IsResult(object? value) =>
        value is not null
        && (IsInstanceOfGenericType(value, typeof(Result<>)) || IsInstanceOfGenericType(value, typeof(Result<,>)));

    /// <summary>The boxed error of a failed result; <c>null</c> for Ok or non-results.</summary>
    public static object? TryGetError(object? value)
    {
        if (value is null)
            return null;

        if (IsInstanceOfGenericType(value, typeof(Result<>)))
            return ErrorOf((dynamic)value);

        if (IsInstanceOfGenericType(value, typeof(Result<,>)))
            return ErrorOf2((dynamic)value);

        return null;
    }

    /// <summary>True when <paramref name="value"/> is an Ok result; <paramref name="okValue"/> is its payload, if any.</summary>
    public static bool TryGetOk(object? value, out object? okValue)
    {
        okValue = null;
        if (value is null)
            return false;

        if (IsInstanceOfGenericType(value, typeof(Result<>)))
            return (bool)OkOf((dynamic)value);

        if (IsInstanceOfGenericType(value, typeof(Result<,>)))
        {
            (bool Ok, object? Value) outcome = OkOf2((dynamic)value);
            okValue = outcome.Value;
            return outcome.Ok;
        }

        return false;
    }

    private static object? ErrorOf<TError>(Result<TError> res) =>
        res.Match<object?>(() => null, e => e);

    private static object? ErrorOf2<TOk, TError>(Result<TOk, TError> res) =>
        res.Match(ok => TryGetError(ok), e => (object?)e);

    private static bool OkOf<TError>(Result<TError> res) =>
        res.Match(() => true, _ => false);

    private static (bool Ok, object? Value) OkOf2<TOk, TError>(Result<TOk, TError> res) =>
        res.Match(
            ok => IsResult(ok)
                ? (TryGetOk(ok, out var inner), inner)
                : (true, (object?)ok),
            _ => (false, null));

    private static bool IsInstanceOfGenericType(object obj, Type genericType)
    {
        var type = obj.GetType();
        return type.IsGenericType && type.GetGenericTypeDefinition() == genericType;
    }
}
