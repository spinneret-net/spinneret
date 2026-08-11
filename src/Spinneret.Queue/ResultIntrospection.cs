using Spinneret.Functional;

namespace Spinneret.Queue;

/// <summary>
/// Reads the error state of a <see cref="Result{TError}"/> or <see cref="Result{TOk,TError}"/>
/// whose concrete generic types are only known at runtime. Uses the public
/// <c>Reduce</c> method via DLR dispatch -no changes to <c>Result.cs</c>, no
/// hand-rolled reflection.
/// </summary>
internal static class ResultIntrospection
{
    /// <summary>
    /// Returns the boxed error if <paramref name="res"/> is a Result-in-error,
    /// otherwise <c>null</c>. Non-Result types always return <c>null</c>.
    /// Nested results are unwrapped: any error anywhere in the chain surfaces.
    /// </summary>
    public static object? TryGetError<TRes>(TRes res)
    {
        if (res is null)
            return null;

        if (IsInstanceOfGenericType(res, typeof(Result<>)))
            return GetErrorFromResult((dynamic)res);

        if (IsInstanceOfGenericType(res, typeof(Result<,>)))
            return GetErrorFromResult2((dynamic)res);

        return null;
    }

    private static object? GetErrorFromResult<TError>(Result<TError> res) =>
        res.Reduce<object?>(() => null, e => e);

    private static object? GetErrorFromResult2<TOk, TError>(Result<TOk, TError> res) =>
        res.Reduce(TryGetError, e => (object?)e);

    private static bool IsInstanceOfGenericType(object obj, Type genericType)
    {
        var type = obj.GetType();
        return type.IsGenericType && type.GetGenericTypeDefinition() == genericType;
    }
}
