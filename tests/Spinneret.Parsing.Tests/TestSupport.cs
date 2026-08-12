using Spinneret.Functional;

namespace Spinneret.Parsing.Tests;

/// <summary>
/// Helpers to unwrap <see cref="Result{TOk,TError}"/> values in tests,
/// failing the test with a clear message when the wrong branch is hit.
/// </summary>
internal static class Expect
{
    public static TOk Ok<TOk, TError>(Result<TOk, TError> result)
    {
        return result.Reduce(
            v => v,
            e => throw new InvalidOperationException($"Expected Ok. Got error: {Describe(e)}"));
    }

    public static TError Error<TOk, TError>(Result<TOk, TError> result)
    {
        return result.Reduce(
            v => throw new InvalidOperationException($"Expected Error. Got ok: {v?.ToString() ?? "<null>"}"),
            e => e);
    }

    public static T Single<T>(IEnumerable<T> values)
    {
        var items = values.ToList();

        if (items.Count != 1)
        {
            throw new InvalidOperationException($"Expected exactly one item but got {items.Count}.");
        }

        return items[0];
    }

    public static InvalidProperty<TError> SingleError<TParsed, TError>(
        Result<TParsed, IEnumerable<InvalidProperty<TError>>> result)
    {
        return Single(Error(result));
    }

    private static string Describe<TError>(TError error)
    {
        if (error is IEnumerable<InvalidProperty<string>> invalidProperties)
        {
            return string.Join(", ", invalidProperties.Select(x => $"{x.PropertyName}: {x.Error}"));
        }

        return error?.ToString() ?? "<null>";
    }
}

/// <summary>Shorthand factories for <see cref="Result{TOk,TError}"/> with string errors.</summary>
internal static class TestParseResult
{
    public static Result<T, string> Ok<T>(T value)
    {
        return Result<T, string>.Ok(value);
    }
}

internal static class TestParseResult<T>
{
    public static Result<T, string> Ok(T value)
    {
        return Result<T, string>.Ok(value);
    }

    public static Result<T, string> Error(string error)
    {
        return Result<T, string>.Error(error);
    }
}

internal class TestObject
{
    public string StringProperty { get; init; } = null!;
    public string? NullableStringProperty { get; init; }
    public int StructProperty { get; init; }
    public int? NullableStructProperty { get; init; }
    public string[] ManyProperty { get; init; } = null!;
}

internal class NestedTestObject
{
    public ChildClass OnlyChildClass { get; init; } = null!;
    public ChildStruct OnlyChildStruct { get; init; }
    public ChildStruct? OptionalChildStruct { get; init; }
    public ChildClass[] ClassChildren { get; init; } = null!;
}

internal class DeepNestedTestObject
{
    public GroupEditorObject GroupEditor { get; init; } = null!;
}

internal class GroupEditorObject
{
    public ChildClass Label { get; init; } = null!;
}

internal class GroupWithFieldsTestObject
{
    public ChildClass[] Fields { get; init; } = [];
}

internal class ChildClass
{
    public string StringProperty { get; init; } = null!;
}

internal readonly struct ChildStruct
{
    public string StringProperty { get; init; }
}
