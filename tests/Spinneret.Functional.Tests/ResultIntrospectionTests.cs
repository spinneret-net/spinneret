namespace Spinneret.Functional.Tests;

public class ResultIntrospectionTests
{
    [Test]
    public async Task A_unit_result_in_ok_state_is_ok_with_no_payload()
    {
        object boxed = Result.Ok<string>();

        await Assert.That(ResultIntrospection.IsResult(boxed)).IsTrue();
        await Assert.That(ResultIntrospection.TryGetError(boxed)).IsNull();
        await Assert.That(ResultIntrospection.TryGetOk(boxed, out var payload)).IsTrue();
        await Assert.That(payload).IsNull();
    }

    [Test]
    public async Task A_unit_result_in_error_state_exposes_the_error()
    {
        object boxed = Result.Error("boom");

        await Assert.That(ResultIntrospection.TryGetError(boxed)).IsEqualTo("boom");
        await Assert.That(ResultIntrospection.TryGetOk(boxed, out var payload)).IsFalse();
        await Assert.That(payload).IsNull();
    }

    [Test]
    public async Task A_valued_result_in_ok_state_exposes_the_value()
    {
        object boxed = Result.Ok<int, string>(42);

        await Assert.That(ResultIntrospection.IsResult(boxed)).IsTrue();
        await Assert.That(ResultIntrospection.TryGetError(boxed)).IsNull();
        await Assert.That(ResultIntrospection.TryGetOk(boxed, out var payload)).IsTrue();
        await Assert.That(payload).IsEqualTo(42);
    }

    [Test]
    public async Task A_valued_result_in_error_state_exposes_the_error()
    {
        object boxed = Result.Error<int, string>("boom");

        await Assert.That(ResultIntrospection.TryGetError(boxed)).IsEqualTo("boom");
        await Assert.That(ResultIntrospection.TryGetOk(boxed, out _)).IsFalse();
    }

    [Test]
    public async Task Nested_results_are_unwrapped_on_both_branches()
    {
        object nestedError = Result.Ok<Result<int, string>, string>(Result.Error<int, string>("inner"));
        object nestedOk = Result.Ok<Result<int, string>, string>(Result.Ok<int, string>(7));

        await Assert.That(ResultIntrospection.TryGetError(nestedError)).IsEqualTo("inner");
        await Assert.That(ResultIntrospection.TryGetOk(nestedError, out _)).IsFalse();
        await Assert.That(ResultIntrospection.TryGetOk(nestedOk, out var payload)).IsTrue();
        await Assert.That(payload).IsEqualTo(7);
    }

    [Test]
    public async Task Non_results_and_null_are_reported_as_not_a_result()
    {
        await Assert.That(ResultIntrospection.IsResult("plain")).IsFalse();
        await Assert.That(ResultIntrospection.IsResult(null)).IsFalse();
        await Assert.That(ResultIntrospection.TryGetError("plain")).IsNull();
        await Assert.That(ResultIntrospection.TryGetError(null)).IsNull();
        await Assert.That(ResultIntrospection.TryGetOk("plain", out var payload)).IsFalse();
        await Assert.That(payload).IsNull();
        await Assert.That(ResultIntrospection.TryGetOk(null, out _)).IsFalse();
    }
}
