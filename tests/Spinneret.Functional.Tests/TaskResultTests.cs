namespace Spinneret.Functional.Tests;

public class TaskResultTests
{
    [Test]
    public async Task Ok_factory_awaits_to_ok_result()
    {
        var result = await TaskResult.Ok<int, string>(42);

        await Assert.That(result).IsEqualTo(Result.Ok<int, string>(42));
    }

    [Test]
    public async Task Error_factory_awaits_to_error_result()
    {
        var result = await TaskResult.Error<int, string>("boom");

        await Assert.That(result).IsEqualTo(Result.Error<int, string>("boom"));
    }

    [Test]
    public async Task AsTask_returns_underlying_task()
    {
        var task = Task.FromResult(Result.Ok<int, string>(1));

        var taskResult = task.AsTaskResult();

        await Assert.That(ReferenceEquals(taskResult.AsTask(), task)).IsTrue();
    }

    [Test]
    public async Task Reduce_on_ok_invokes_success_case()
    {
        var taskResult = TaskResult.Ok<int, string>(42);

        var reduced = await taskResult.Reduce(ok => $"ok:{ok}", error => $"error:{error}");

        await Assert.That(reduced).IsEqualTo("ok:42");
    }

    [Test]
    public async Task Reduce_on_error_invokes_error_case()
    {
        var taskResult = TaskResult.Error<int, string>("boom");

        var reduced = await taskResult.Reduce(ok => $"ok:{ok}", error => $"error:{error}");

        await Assert.That(reduced).IsEqualTo("error:boom");
    }

    [Test]
    public async Task Map_on_ok_transforms_value()
    {
        var taskResult = TaskResult.Ok<int, string>(21);

        var mapped = await taskResult.Map(x => x * 2);

        await Assert.That(mapped).IsEqualTo(Result.Ok<int, string>(42));
    }

    [Test]
    public async Task Map_on_error_propagates_error_without_invoking_mapper()
    {
        var taskResult = TaskResult.Error<int, string>("boom");
        var invoked = false;

        var mapped = await taskResult.Map(x =>
        {
            invoked = true;
            return x * 2;
        });

        await Assert.That(mapped).IsEqualTo(Result.Error<int, string>("boom"));
        await Assert.That(invoked).IsFalse();
    }

    [Test]
    public async Task MapError_on_error_transforms_error()
    {
        var taskResult = TaskResult.Error<int, string>("boom");

        var mapped = await taskResult.MapError(e => e.Length);

        await Assert.That(mapped).IsEqualTo(Result.Error<int, int>(4));
    }

    [Test]
    public async Task MapError_on_ok_propagates_value()
    {
        var taskResult = TaskResult.Ok<int, string>(42);

        var mapped = await taskResult.MapError(e => e.Length);

        await Assert.That(mapped).IsEqualTo(Result.Ok<int, int>(42));
    }

    [Test]
    public async Task Ignore_on_ok_discards_value()
    {
        var taskResult = TaskResult.Ok<int, string>(42);

        var ignored = await taskResult.Ignore<int>();

        await Assert.That(ignored).IsEqualTo(Result.Ok<string>());
    }

    [Test]
    public async Task Ignore_on_error_preserves_error()
    {
        var taskResult = TaskResult.Error<int, string>("boom");

        var ignored = await taskResult.Ignore<int>();

        await Assert.That(ignored).IsEqualTo(Result.Error<string>("boom"));
    }

    [Test]
    public async Task Bind_with_result_continuation_on_ok_produces_bound_result()
    {
        var taskResult = TaskResult.Ok<int, string>(21);

        var bound = await taskResult.Bind(x => Result.Ok<int, string>(x * 2));

        await Assert.That(bound).IsEqualTo(Result.Ok<int, string>(42));
    }

    [Test]
    public async Task Bind_with_task_continuation_on_ok_wraps_awaited_value()
    {
        var taskResult = TaskResult.Ok<int, string>(21);

        var bound = await taskResult.Bind(async x =>
        {
            await Task.Yield();
            return x * 2;
        });

        await Assert.That(bound).IsEqualTo(Result.Ok<int, string>(42));
    }

    [Test]
    public async Task Bind_with_task_of_result_continuation_on_ok_produces_bound_result()
    {
        var taskResult = TaskResult.Ok<int, string>(21);

        var bound = await taskResult.Bind(x => Task.FromResult(Result.Error<int, string>($"failed:{x}")));

        await Assert.That(bound).IsEqualTo(Result.Error<int, string>("failed:21"));
    }

    [Test]
    public async Task Bind_with_task_result_continuation_on_ok_produces_bound_result()
    {
        var taskResult = TaskResult.Ok<int, string>(21);

        var bound = await taskResult.Bind(x => TaskResult.Ok<int, string>(x * 2));

        await Assert.That(bound).IsEqualTo(Result.Ok<int, string>(42));
    }

    [Test]
    public async Task Bind_with_task_result_continuation_on_error_short_circuits_without_invoking_continuation()
    {
        var taskResult = TaskResult.Error<int, string>("boom");
        var invoked = false;

        var bound = await taskResult.Bind(x =>
        {
            invoked = true;
            return TaskResult.Ok<int, string>(x);
        });

        await Assert.That(bound).IsEqualTo(Result.Error<int, string>("boom"));
        await Assert.That(invoked).IsFalse();
    }

    [Test]
    public async Task Bind_to_unit_with_result_continuation_on_ok_produces_unit_result()
    {
        var taskResult = TaskResult.Ok<int, string>(42);

        var bound = await taskResult.Bind(x => x > 0 ? Result.Ok<string>() : Result.Error("not positive"));

        await Assert.That(bound).IsEqualTo(Result.Ok<string>());
    }

    [Test]
    public async Task Bind_to_unit_with_plain_task_continuation_on_ok_produces_ok()
    {
        var taskResult = TaskResult.Ok<int, string>(42);
        var seen = 0;

        var bound = await taskResult.Bind(x =>
        {
            seen = x;
            return Task.CompletedTask;
        });

        await Assert.That(bound).IsEqualTo(Result.Ok<string>());
        await Assert.That(seen).IsEqualTo(42);
    }

    [Test]
    public async Task Bind_to_unit_with_task_of_result_continuation_on_ok_produces_bound_result()
    {
        var taskResult = TaskResult.Ok<int, string>(42);

        var bound = await taskResult.Bind(_ => Task.FromResult(Result.Error("inner failure")));

        await Assert.That(bound).IsEqualTo(Result.Error("inner failure"));
    }

    [Test]
    public async Task Bind_to_unit_with_task_result_continuation_on_ok_produces_bound_result()
    {
        var taskResult = TaskResult.Ok<int, string>(42);

        var bound = await taskResult.Bind(_ => TaskResult.Ok<string>());

        await Assert.That(bound).IsEqualTo(Result.Ok<string>());
    }

    [Test]
    public async Task Bind_to_unit_on_error_short_circuits_without_invoking_continuation()
    {
        var taskResult = TaskResult.Error<int, string>("boom");
        var invoked = false;

        var bound = await taskResult.Bind(_ =>
        {
            invoked = true;
            return TaskResult.Ok<string>();
        });

        await Assert.That(bound).IsEqualTo(Result.Error<string>("boom"));
        await Assert.That(invoked).IsFalse();
    }

    [Test]
    public async Task BindError_with_result_continuation_on_error_can_recover_to_ok()
    {
        var taskResult = TaskResult.Error<int, string>("boom");

        var recovered = await taskResult.BindError(e => Result.Ok<int, int>(e.Length));

        await Assert.That(recovered).IsEqualTo(Result.Ok<int, int>(4));
    }

    [Test]
    public async Task BindError_with_task_continuation_on_error_maps_to_new_error()
    {
        var taskResult = TaskResult.Error<int, string>("boom");

        var bound = await taskResult.BindError(async e =>
        {
            await Task.Yield();
            return e.Length;
        });

        await Assert.That(bound).IsEqualTo(Result.Error<int, int>(4));
    }

    [Test]
    public async Task BindError_with_task_of_result_continuation_on_error_produces_bound_result()
    {
        var taskResult = TaskResult.Error<int, string>("boom");

        var bound = await taskResult.BindError(e => Task.FromResult(Result.Error<int, bool>(true)));

        await Assert.That(bound).IsEqualTo(Result.Error<int, bool>(true));
    }

    [Test]
    public async Task BindError_with_task_result_continuation_on_error_produces_bound_result()
    {
        var taskResult = TaskResult.Error<int, string>("boom");

        var bound = await taskResult.BindError(e => TaskResult.Ok<int, bool>(7));

        await Assert.That(bound).IsEqualTo(Result.Ok<int, bool>(7));
    }

    [Test]
    public async Task BindError_on_ok_propagates_value_without_invoking_continuation()
    {
        var taskResult = TaskResult.Ok<int, string>(42);
        var invoked = false;

        var bound = await taskResult.BindError(e =>
        {
            invoked = true;
            return TaskResult.Error<int, bool>(true);
        });

        await Assert.That(bound).IsEqualTo(Result.Ok<int, bool>(42));
        await Assert.That(invoked).IsFalse();
    }

    [Test]
    public async Task Bind_chain_preserves_first_error_and_skips_later_continuations()
    {
        var invocations = new List<string>();

        var result = await TaskResult.Ok<int, string>(1)
            .Bind(x =>
            {
                invocations.Add("first");
                return TaskResult.Error<int, string>("failed at first");
            })
            .Bind(x =>
            {
                invocations.Add("second");
                return TaskResult.Ok<int, string>(x);
            });

        await Assert.That(result).IsEqualTo(Result.Error<int, string>("failed at first"));
        await Assert.That(invocations).IsEquivalentTo(["first"]);
    }
}

public class UnitTaskResultTests
{
    [Test]
    public async Task Ok_factory_awaits_to_ok_result()
    {
        var result = await TaskResult.Ok<string>();

        await Assert.That(result).IsEqualTo(Result.Ok<string>());
    }

    [Test]
    public async Task Error_factory_awaits_to_error_result()
    {
        var result = await TaskResult.Error("boom");

        await Assert.That(result).IsEqualTo(Result.Error("boom"));
    }

    [Test]
    public async Task Reduce_on_ok_invokes_success_case()
    {
        var taskResult = TaskResult.Ok<string>();

        var reduced = await taskResult.Reduce(() => "ok", error => $"error:{error}");

        await Assert.That(reduced).IsEqualTo("ok");
    }

    [Test]
    public async Task Reduce_on_error_invokes_error_case()
    {
        var taskResult = TaskResult.Error("boom");

        var reduced = await taskResult.Reduce(() => "ok", error => $"error:{error}");

        await Assert.That(reduced).IsEqualTo("error:boom");
    }

    [Test]
    public async Task Map_on_ok_produces_ok_with_value()
    {
        var taskResult = TaskResult.Ok<string>();

        var mapped = await taskResult.Map(() => 42);

        await Assert.That(mapped).IsEqualTo(Result.Ok<int, string>(42));
    }

    [Test]
    public async Task Map_on_error_propagates_error_without_invoking_mapper()
    {
        var taskResult = TaskResult.Error("boom");
        var invoked = false;

        var mapped = await taskResult.Map(() =>
        {
            invoked = true;
            return 42;
        });

        await Assert.That(mapped).IsEqualTo(Result.Error<int, string>("boom"));
        await Assert.That(invoked).IsFalse();
    }

    [Test]
    public async Task MapError_on_error_transforms_error()
    {
        var taskResult = TaskResult.Error("boom");

        var mapped = await taskResult.MapError(e => e.Length);

        await Assert.That(mapped).IsEqualTo(Result.Error(4));
    }

    [Test]
    public async Task MapError_on_ok_stays_ok()
    {
        var taskResult = TaskResult.Ok<string>();

        var mapped = await taskResult.MapError(e => e.Length);

        await Assert.That(mapped).IsEqualTo(Result.Ok<int>());
    }

    [Test]
    public async Task Bind_with_result_continuation_on_ok_produces_bound_result()
    {
        var taskResult = TaskResult.Ok<string>();

        var bound = await taskResult.Bind(() => Result.Error("inner failure"));

        await Assert.That(bound).IsEqualTo(Result.Error("inner failure"));
    }

    [Test]
    public async Task Bind_with_plain_task_continuation_on_ok_produces_ok()
    {
        var taskResult = TaskResult.Ok<string>();
        var invoked = false;

        var bound = await taskResult.Bind(() =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        await Assert.That(bound).IsEqualTo(Result.Ok<string>());
        await Assert.That(invoked).IsTrue();
    }

    [Test]
    public async Task Bind_with_task_of_result_continuation_on_ok_produces_bound_result()
    {
        var taskResult = TaskResult.Ok<string>();

        var bound = await taskResult.Bind(() => Task.FromResult(Result.Ok<string>()));

        await Assert.That(bound).IsEqualTo(Result.Ok<string>());
    }

    [Test]
    public async Task Bind_with_task_result_continuation_on_ok_produces_bound_result()
    {
        var taskResult = TaskResult.Ok<string>();

        var bound = await taskResult.Bind(() => TaskResult.Error("inner failure"));

        await Assert.That(bound).IsEqualTo(Result.Error("inner failure"));
    }

    [Test]
    public async Task Bind_on_error_short_circuits_without_invoking_continuation()
    {
        var taskResult = TaskResult.Error("boom");
        var invoked = false;

        var bound = await taskResult.Bind(() =>
        {
            invoked = true;
            return TaskResult.Ok<string>();
        });

        await Assert.That(bound).IsEqualTo(Result.Error("boom"));
        await Assert.That(invoked).IsFalse();
    }

    [Test]
    public async Task Bind_to_valued_with_result_continuation_on_ok_produces_value()
    {
        var taskResult = TaskResult.Ok<string>();

        var bound = await taskResult.Bind(() => Result.Ok<int, string>(42));

        await Assert.That(bound).IsEqualTo(Result.Ok<int, string>(42));
    }

    [Test]
    public async Task Bind_to_valued_with_task_continuation_on_ok_wraps_awaited_value()
    {
        var taskResult = TaskResult.Ok<string>();

        var bound = await taskResult.Bind(async () =>
        {
            await Task.Yield();
            return 42;
        });

        await Assert.That(bound).IsEqualTo(Result.Ok<int, string>(42));
    }

    [Test]
    public async Task Bind_to_valued_with_task_of_result_continuation_on_ok_produces_bound_result()
    {
        var taskResult = TaskResult.Ok<string>();

        var bound = await taskResult.Bind(() => Task.FromResult(Result.Ok<int, string>(42)));

        await Assert.That(bound).IsEqualTo(Result.Ok<int, string>(42));
    }

    [Test]
    public async Task Bind_to_valued_with_task_result_continuation_on_ok_produces_bound_result()
    {
        var taskResult = TaskResult.Ok<string>();

        var bound = await taskResult.Bind(() => TaskResult.Ok<int, string>(42));

        await Assert.That(bound).IsEqualTo(Result.Ok<int, string>(42));
    }

    [Test]
    public async Task Bind_to_valued_on_error_short_circuits_without_invoking_continuation()
    {
        var taskResult = TaskResult.Error("boom");
        var invoked = false;

        var bound = await taskResult.Bind(() =>
        {
            invoked = true;
            return TaskResult.Ok<int, string>(42);
        });

        await Assert.That(bound).IsEqualTo(Result.Error<int, string>("boom"));
        await Assert.That(invoked).IsFalse();
    }

    [Test]
    public async Task BindError_with_result_continuation_on_error_can_recover_to_ok()
    {
        var taskResult = TaskResult.Error("boom");

        var recovered = await taskResult.BindError(e => Result.Ok<int>());

        await Assert.That(recovered).IsEqualTo(Result.Ok<int>());
    }

    [Test]
    public async Task BindError_with_task_continuation_on_error_maps_to_new_error()
    {
        var taskResult = TaskResult.Error("boom");

        var bound = await taskResult.BindError(async e =>
        {
            await Task.Yield();
            return e.Length;
        });

        await Assert.That(bound).IsEqualTo(Result.Error(4));
    }

    [Test]
    public async Task BindError_with_task_of_result_continuation_on_error_produces_bound_result()
    {
        var taskResult = TaskResult.Error("boom");

        var bound = await taskResult.BindError(e => Task.FromResult(Result.Error(e.Length)));

        await Assert.That(bound).IsEqualTo(Result.Error(4));
    }

    [Test]
    public async Task BindError_with_task_result_continuation_on_error_produces_bound_result()
    {
        var taskResult = TaskResult.Error("boom");

        var bound = await taskResult.BindError(e => TaskResult.Error(e.Length));

        await Assert.That(bound).IsEqualTo(Result.Error(4));
    }

    [Test]
    public async Task BindError_on_ok_stays_ok_without_invoking_continuation()
    {
        var taskResult = TaskResult.Ok<string>();
        var invoked = false;

        var bound = await taskResult.BindError(e =>
        {
            invoked = true;
            return TaskResult.Error(e.Length);
        });

        await Assert.That(bound).IsEqualTo(Result.Ok<int>());
        await Assert.That(invoked).IsFalse();
    }
}

public class TaskResultExtensionsTests
{
    [Test]
    public async Task AsTaskResult_on_task_of_valued_result_wraps_task()
    {
        var task = Task.FromResult(Result.Ok<int, string>(42));

        var taskResult = task.AsTaskResult();

        await Assert.That(await taskResult).IsEqualTo(Result.Ok<int, string>(42));
    }

    [Test]
    public async Task AsTaskResult_on_task_of_unit_result_wraps_task()
    {
        var task = Task.FromResult(Result.Error("boom"));

        var taskResult = task.AsTaskResult();

        await Assert.That(await taskResult).IsEqualTo(Result.Error("boom"));
    }

    [Test]
    public async Task AsTaskResult_on_valued_result_lifts_to_completed_task()
    {
        var result = Result.Ok<int, string>(42);

        var taskResult = result.AsTaskResult();

        await Assert.That(await taskResult).IsEqualTo(Result.Ok<int, string>(42));
    }

    [Test]
    public async Task AsTaskResult_on_unit_result_lifts_to_completed_task()
    {
        var result = Result.Error("boom");

        var taskResult = result.AsTaskResult();

        await Assert.That(await taskResult).IsEqualTo(Result.Error("boom"));
    }

    [Test]
    public async Task AsTaskResult_on_plain_value_task_lifts_value_to_ok()
    {
        var task = Task.FromResult(42);

        var taskResult = task.AsTaskResult<int, string>();

        await Assert.That(await taskResult).IsEqualTo(Result.Ok<int, string>(42));
    }

    [Test]
    public async Task AsTaskResult_on_plain_task_lifts_to_unit_ok()
    {
        var task = Task.CompletedTask;

        var taskResult = task.AsTaskResult<string>();

        await Assert.That(await taskResult).IsEqualTo(Result.Ok<string>());
    }
}
