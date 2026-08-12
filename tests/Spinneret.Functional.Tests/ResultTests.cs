namespace Spinneret.Functional.Tests;

public class ResultTests
{
    [Test]
    public async Task Ok_reduces_to_ok_branch()
    {
        var result = Result.Ok<int, string>(42);

        var reduced = result.Reduce(ok => $"ok:{ok}", error => $"error:{error}");

        await Assert.That(reduced).IsEqualTo("ok:42");
    }

    [Test]
    public async Task Error_reduces_to_error_branch()
    {
        var result = Result.Error<int, string>("boom");

        var reduced = result.Reduce(ok => $"ok:{ok}", error => $"error:{error}");

        await Assert.That(reduced).IsEqualTo("error:boom");
    }

    [Test]
    public async Task Map_on_ok_result_transforms_value()
    {
        var result = Result.Ok<int, string>(21);

        var mapped = result.Map(x => x * 2);

        await Assert.That(mapped).IsEqualTo(Result.Ok<int, string>(42));
    }

    [Test]
    public async Task Map_on_error_result_propagates_error_without_invoking_mapper()
    {
        var result = Result.Error<int, string>("boom");
        var invoked = false;

        var mapped = result.Map(x =>
        {
            invoked = true;
            return x * 2;
        });

        await Assert.That(mapped).IsEqualTo(Result.Error<int, string>("boom"));
        await Assert.That(invoked).IsFalse();
    }

    [Test]
    public async Task MapError_on_error_result_transforms_error()
    {
        var result = Result.Error<int, string>("boom");

        var mapped = result.MapError(e => e.Length);

        await Assert.That(mapped).IsEqualTo(Result.Error<int, int>(4));
    }

    [Test]
    public async Task MapError_on_ok_result_propagates_value_without_invoking_mapper()
    {
        var result = Result.Ok<int, string>(42);
        var invoked = false;

        var mapped = result.MapError(e =>
        {
            invoked = true;
            return e.Length;
        });

        await Assert.That(mapped).IsEqualTo(Result.Ok<int, int>(42));
        await Assert.That(invoked).IsFalse();
    }

    [Test]
    public async Task Bind_on_ok_result_returning_ok_produces_ok()
    {
        var result = Result.Ok<int, string>(21);

        var bound = result.Bind(x => Result.Ok<string, string>($"value:{x}"));

        await Assert.That(bound).IsEqualTo(Result.Ok<string, string>("value:21"));
    }

    [Test]
    public async Task Bind_on_ok_result_returning_error_produces_error()
    {
        var result = Result.Ok<int, string>(21);

        var bound = result.Bind(_ => Result.Error<string, string>("inner failure"));

        await Assert.That(bound).IsEqualTo(Result.Error<string, string>("inner failure"));
    }

    [Test]
    public async Task Bind_on_error_result_propagates_error_without_invoking_binder()
    {
        var result = Result.Error<int, string>("boom");
        var invoked = false;

        var bound = result.Bind(x =>
        {
            invoked = true;
            return Result.Ok<string, string>(x.ToString());
        });

        await Assert.That(bound).IsEqualTo(Result.Error<string, string>("boom"));
        await Assert.That(invoked).IsFalse();
    }

    [Test]
    public async Task Bind_to_unit_result_on_ok_result_invokes_binder()
    {
        var result = Result.Ok<int, string>(21);

        var bound = result.Bind(x => x > 0 ? Result.Ok<string>() : Result.Error("not positive"));

        await Assert.That(bound).IsEqualTo(Result.Ok<string>());
    }

    [Test]
    public async Task Bind_to_unit_result_on_error_result_propagates_error()
    {
        var result = Result.Error<int, string>("boom");

        var bound = result.Bind(_ => Result.Ok<string>());

        await Assert.That(bound).IsEqualTo(Result.Error<string>("boom"));
    }

    [Test]
    public async Task Ignore_on_ok_result_discards_value()
    {
        var result = Result.Ok<int, string>(42);

        var ignored = result.Ignore<int>();

        await Assert.That(ignored).IsEqualTo(Result.Ok<string>());
    }

    [Test]
    public async Task Ignore_on_error_result_preserves_error()
    {
        var result = Result.Error<int, string>("boom");

        var ignored = result.Ignore<int>();

        await Assert.That(ignored).IsEqualTo(Result.Error<string>("boom"));
    }

    [Test]
    public async Task TraverseTask_on_ok_result_awaits_task_and_wraps_value()
    {
        var result = Result.Ok<int, string>(21);

        var traversed = await result.TraverseTask(async x =>
        {
            await Task.Yield();
            return x * 2;
        });

        await Assert.That(traversed).IsEqualTo(Result.Ok<int, string>(42));
    }

    [Test]
    public async Task TraverseTask_on_error_result_short_circuits_without_invoking_task()
    {
        var result = Result.Error<int, string>("boom");
        var invoked = false;

        var traversed = await result.TraverseTask(x =>
        {
            invoked = true;
            return Task.FromResult(x * 2);
        });

        await Assert.That(traversed).IsEqualTo(Result.Error<int, string>("boom"));
        await Assert.That(invoked).IsFalse();
    }

    [Test]
    public async Task TraverseTask_to_unit_result_on_ok_result_runs_side_effect()
    {
        var result = Result.Ok<int, string>(42);
        var seen = 0;

        var traversed = await result.TraverseTask(async x =>
        {
            await Task.Yield();
            seen = x;
        });

        await Assert.That(traversed).IsEqualTo(Result.Ok<string>());
        await Assert.That(seen).IsEqualTo(42);
    }

    [Test]
    public async Task TraverseTask_to_unit_result_on_error_result_skips_side_effect()
    {
        var result = Result.Error<int, string>("boom");
        var invoked = false;

        var traversed = await result.TraverseTask(_ =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        await Assert.That(traversed).IsEqualTo(Result.Error<string>("boom"));
        await Assert.That(invoked).IsFalse();
    }

    [Test]
    public async Task ThrowOnError_on_ok_result_returns_value()
    {
        var result = Result.Ok<int, string>(42);

        var value = result.ThrowOnError(e => new InvalidOperationException(e));

        await Assert.That(value).IsEqualTo(42);
    }

    [Test]
    public async Task ThrowOnError_on_error_result_throws_mapped_exception()
    {
        var result = Result.Error<int, string>("boom");

        var exception = Assert.Throws<InvalidOperationException>(
            () => result.ThrowOnError(e => new InvalidOperationException(e)));

        await Assert.That(exception.Message).IsEqualTo("boom");
    }

    [Test]
    public async Task Iter_on_ok_result_invokes_only_ok_action()
    {
        var result = Result.Ok<int, string>(42);
        var okCalls = new List<int>();
        var errorCalls = new List<string>();

        result.Iter(okCalls.Add, errorCalls.Add);

        await Assert.That(okCalls).IsEquivalentTo([42]);
        await Assert.That(errorCalls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Iter_on_error_result_invokes_only_error_action()
    {
        var result = Result.Error<int, string>("boom");
        var okCalls = new List<int>();
        var errorCalls = new List<string>();

        result.Iter(okCalls.Add, errorCalls.Add);

        await Assert.That(okCalls.Count).IsEqualTo(0);
        await Assert.That(errorCalls).IsEquivalentTo(["boom"]);
    }

    [Test]
    public async Task FromNullable_with_non_null_value_produces_ok()
    {
        var result = Result.FromNullable<string, string>("value", () => "was null");

        await Assert.That(result).IsEqualTo(Result.Ok<string, string>("value"));
    }

    [Test]
    public async Task FromNullable_with_null_value_produces_error()
    {
        var result = Result.FromNullable<string, string>(null, () => "was null");

        await Assert.That(result).IsEqualTo(Result.Error<string, string>("was null"));
    }

    [Test]
    public async Task FromNullable_with_non_null_value_does_not_invoke_error_factory()
    {
        var invoked = false;

        Result.FromNullable<string, string>("value", () =>
        {
            invoked = true;
            return "was null";
        });

        await Assert.That(invoked).IsFalse();
    }

    [Test]
    public async Task FromNullable_with_null_nullable_struct_produces_error()
    {
        var result = Result.FromNullable<int?, string>(null, () => "was null");

        await Assert.That(result).IsEqualTo(Result.Error<int?, string>("was null"));
    }

    [Test]
    public async Task Equality_holds_for_ok_results_with_equal_values()
    {
        var first = Result.Ok<int, string>(42);
        var second = Result.Ok<int, string>(42);

        await Assert.That(first).IsEqualTo(second);
    }

    [Test]
    public async Task Equality_fails_between_ok_and_error_results()
    {
        var ok = Result.Ok<string, string>("x");
        var error = Result.Error<string, string>("x");

        await Assert.That(ok).IsNotEqualTo(error);
    }
}

public class UnitResultTests
{
    [Test]
    public async Task Ok_reduces_to_ok_branch()
    {
        var result = Result.Ok<string>();

        var reduced = result.Reduce(() => "ok", error => $"error:{error}");

        await Assert.That(reduced).IsEqualTo("ok");
    }

    [Test]
    public async Task Error_reduces_to_error_branch()
    {
        var result = Result.Error("boom");

        var reduced = result.Reduce(() => "ok", error => $"error:{error}");

        await Assert.That(reduced).IsEqualTo("error:boom");
    }

    [Test]
    public async Task Map_on_ok_result_produces_ok_with_value()
    {
        var result = Result.Ok<string>();

        var mapped = result.Map(() => 42);

        await Assert.That(mapped).IsEqualTo(Result.Ok<int, string>(42));
    }

    [Test]
    public async Task Map_on_error_result_propagates_error_without_invoking_mapper()
    {
        var result = Result.Error("boom");
        var invoked = false;

        var mapped = result.Map(() =>
        {
            invoked = true;
            return 42;
        });

        await Assert.That(mapped).IsEqualTo(Result.Error<int, string>("boom"));
        await Assert.That(invoked).IsFalse();
    }

    [Test]
    public async Task MapError_on_error_result_transforms_error()
    {
        var result = Result.Error("boom");

        var mapped = result.MapError(e => e.Length);

        await Assert.That(mapped).IsEqualTo(Result.Error(4));
    }

    [Test]
    public async Task MapError_on_ok_result_stays_ok()
    {
        var result = Result.Ok<string>();

        var mapped = result.MapError(e => e.Length);

        await Assert.That(mapped).IsEqualTo(Result.Ok<int>());
    }

    [Test]
    public async Task Bind_on_ok_result_invokes_binder()
    {
        var result = Result.Ok<string>();

        var bound = result.Bind(() => Result.Error("inner failure"));

        await Assert.That(bound).IsEqualTo(Result.Error("inner failure"));
    }

    [Test]
    public async Task Bind_on_error_result_propagates_error_without_invoking_binder()
    {
        var result = Result.Error("boom");
        var invoked = false;

        var bound = result.Bind(() =>
        {
            invoked = true;
            return Result.Ok<string>();
        });

        await Assert.That(bound).IsEqualTo(Result.Error("boom"));
        await Assert.That(invoked).IsFalse();
    }

    [Test]
    public async Task Bind_to_valued_result_on_ok_result_produces_value()
    {
        var result = Result.Ok<string>();

        var bound = result.Bind(() => Result.Ok<int, string>(42));

        await Assert.That(bound).IsEqualTo(Result.Ok<int, string>(42));
    }

    [Test]
    public async Task Bind_to_valued_result_on_error_result_propagates_error()
    {
        var result = Result.Error("boom");

        var bound = result.Bind(() => Result.Ok<int, string>(42));

        await Assert.That(bound).IsEqualTo(Result.Error<int, string>("boom"));
    }

    [Test]
    public async Task TraverseTask_on_ok_result_awaits_side_effect()
    {
        var result = Result.Ok<string>();
        var invoked = false;

        var traversed = await result.TraverseTask(async () =>
        {
            await Task.Yield();
            invoked = true;
        });

        await Assert.That(traversed).IsEqualTo(Result.Ok<string>());
        await Assert.That(invoked).IsTrue();
    }

    [Test]
    public async Task TraverseTask_on_error_result_skips_side_effect()
    {
        var result = Result.Error("boom");
        var invoked = false;

        var traversed = await result.TraverseTask(() =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        await Assert.That(traversed).IsEqualTo(Result.Error("boom"));
        await Assert.That(invoked).IsFalse();
    }

    [Test]
    public async Task TraverseTask_to_valued_result_on_ok_result_wraps_task_value()
    {
        var result = Result.Ok<string>();

        var traversed = await result.TraverseTask(async () =>
        {
            await Task.Yield();
            return 42;
        });

        await Assert.That(traversed).IsEqualTo(Result.Ok<int, string>(42));
    }

    [Test]
    public async Task TraverseTask_to_valued_result_on_error_result_short_circuits()
    {
        var result = Result.Error("boom");
        var invoked = false;

        var traversed = await result.TraverseTask(() =>
        {
            invoked = true;
            return Task.FromResult(42);
        });

        await Assert.That(traversed).IsEqualTo(Result.Error<int, string>("boom"));
        await Assert.That(invoked).IsFalse();
    }

    [Test]
    public async Task ThrowOnError_on_ok_result_does_not_throw()
    {
        var result = Result.Ok<string>();
        Exception? caught = null;

        try
        {
            result.ThrowOnError(e => new InvalidOperationException(e));
        }
        catch (Exception e)
        {
            caught = e;
        }

        await Assert.That(caught).IsNull();
    }

    [Test]
    public async Task ThrowOnError_on_error_result_throws_mapped_exception()
    {
        var result = Result.Error("boom");

        var exception = Assert.Throws<InvalidOperationException>(
            () => result.ThrowOnError(e => new InvalidOperationException(e)));

        await Assert.That(exception.Message).IsEqualTo("boom");
    }

    [Test]
    public async Task Iter_on_ok_result_invokes_only_ok_action()
    {
        var result = Result.Ok<string>();
        var okCalls = 0;
        var errorCalls = new List<string>();

        result.Iter(() => okCalls++, errorCalls.Add);

        await Assert.That(okCalls).IsEqualTo(1);
        await Assert.That(errorCalls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Iter_on_error_result_invokes_only_error_action()
    {
        var result = Result.Error("boom");
        var okCalls = 0;
        var errorCalls = new List<string>();

        result.Iter(() => okCalls++, errorCalls.Add);

        await Assert.That(okCalls).IsEqualTo(0);
        await Assert.That(errorCalls).IsEquivalentTo(["boom"]);
    }

    [Test]
    public async Task Equality_holds_for_ok_results_of_same_error_type()
    {
        await Assert.That(Result.Ok<string>()).IsEqualTo(Result<string>.Ok());
    }
}

public class ResultCombinatorTests
{
    [Test]
    public async Task Swap_on_ok_of_ok_produces_ok_of_ok()
    {
        var nested = Result.Ok<Result<int, string>, bool>(Result.Ok<int, string>(42));

        var swapped = nested.Swap();

        await Assert.That(swapped)
            .IsEqualTo(Result.Ok<Result<int, bool>, string>(Result.Ok<int, bool>(42)));
    }

    [Test]
    public async Task Swap_on_ok_of_error_lifts_inner_error_to_outer()
    {
        var nested = Result.Ok<Result<int, string>, bool>(Result.Error<int, string>("inner"));

        var swapped = nested.Swap();

        await Assert.That(swapped).IsEqualTo(Result.Error<Result<int, bool>, string>("inner"));
    }

    [Test]
    public async Task Swap_on_outer_error_pushes_error_into_inner_result()
    {
        var nested = Result.Error<Result<int, string>, bool>(true);

        var swapped = nested.Swap();

        await Assert.That(swapped)
            .IsEqualTo(Result.Ok<Result<int, bool>, string>(Result.Error<int, bool>(true)));
    }

    [Test]
    public async Task Swap_on_unit_ok_of_ok_produces_ok_of_ok()
    {
        var nested = Result.Ok<Result<string>, bool>(Result.Ok<string>());

        var swapped = nested.Swap();

        await Assert.That(swapped).IsEqualTo(Result.Ok<Result<bool>, string>(Result.Ok<bool>()));
    }

    [Test]
    public async Task Swap_on_unit_ok_of_error_lifts_inner_error_to_outer()
    {
        var nested = Result.Ok<Result<string>, bool>(Result.Error("inner"));

        var swapped = nested.Swap();

        await Assert.That(swapped).IsEqualTo(Result.Error<Result<bool>, string>("inner"));
    }

    [Test]
    public async Task Swap_on_unit_outer_error_pushes_error_into_inner_result()
    {
        var nested = Result.Error<Result<string>, bool>(true);

        var swapped = nested.Swap();

        await Assert.That(swapped).IsEqualTo(Result.Ok<Result<bool>, string>(Result.Error(true)));
    }

    [Test]
    public async Task Flatten_on_ok_of_ok_produces_flat_ok()
    {
        var nested = Result.Ok<Result<int, string>, bool>(Result.Ok<int, string>(42));

        var flattened = nested.Flatten();

        await Assert.That(flattened).IsEqualTo(Result.Ok<int, Either<bool, string>>(42));
    }

    [Test]
    public async Task Flatten_on_outer_error_produces_first_either_branch()
    {
        var nested = Result.Error<Result<int, string>, bool>(true);

        var flattened = nested.Flatten();

        await Assert.That(flattened)
            .IsEqualTo(Result.Error<int, Either<bool, string>>(new Either<bool, string>(true)));
    }

    [Test]
    public async Task Flatten_on_inner_error_produces_second_either_branch()
    {
        var nested = Result.Ok<Result<int, string>, bool>(Result.Error<int, string>("inner"));

        var flattened = nested.Flatten();

        await Assert.That(flattened)
            .IsEqualTo(Result.Error<int, Either<bool, string>>(new Either<bool, string>("inner")));
    }
}
