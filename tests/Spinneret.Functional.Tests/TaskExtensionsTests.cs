namespace Spinneret.Functional.Tests;

public class TaskExtensionsTests
{
    [Test]
    public async Task Map_on_task_with_value_transforms_result()
    {
        var task = Task.FromResult(21);

        var mapped = await task.Map(x => x * 2);

        await Assert.That(mapped).IsEqualTo(42);
    }

    [Test]
    public async Task Map_with_action_on_task_with_value_invokes_action_with_result()
    {
        var task = Task.FromResult(42);
        var seen = 0;

        await task.Map(x => { seen = x; });

        await Assert.That(seen).IsEqualTo(42);
    }

    [Test]
    public async Task Map_on_plain_task_produces_value_after_completion()
    {
        var task = Task.CompletedTask;

        var mapped = await task.Map(() => 42);

        await Assert.That(mapped).IsEqualTo(42);
    }

    [Test]
    public async Task Map_with_action_on_plain_task_invokes_action()
    {
        var task = Task.CompletedTask;
        var invoked = false;

        await task.Map(() => { invoked = true; });

        await Assert.That(invoked).IsTrue();
    }

    [Test]
    public async Task Map_does_not_invoke_continuation_before_task_completes()
    {
        var tcs = new TaskCompletionSource<int>();
        var invoked = false;

        var mapped = tcs.Task.Map(x =>
        {
            invoked = true;
            return x * 2;
        });

        await Assert.That(invoked).IsFalse();

        tcs.SetResult(21);

        await Assert.That(await mapped).IsEqualTo(42);
        await Assert.That(invoked).IsTrue();
    }

    [Test]
    public async Task Map_on_faulted_task_propagates_exception_without_invoking_continuation()
    {
        var task = Task.FromException<int>(new InvalidOperationException("boom"));
        var invoked = false;

        var mapped = task.Map(x =>
        {
            invoked = true;
            return x;
        });

        await Assert.That(async () => await mapped).Throws<InvalidOperationException>();
        await Assert.That(invoked).IsFalse();
    }

    [Test]
    public async Task Bind_on_task_with_value_chains_to_continuation_task()
    {
        var task = Task.FromResult(21);

        var bound = await task.Bind(async x =>
        {
            await Task.Yield();
            return x * 2;
        });

        await Assert.That(bound).IsEqualTo(42);
    }

    [Test]
    public async Task Bind_on_task_with_value_to_plain_task_awaits_continuation()
    {
        var task = Task.FromResult(42);
        var seen = 0;

        await task.Bind(async x =>
        {
            await Task.Yield();
            seen = x;
        });

        await Assert.That(seen).IsEqualTo(42);
    }

    [Test]
    public async Task Bind_on_plain_task_to_task_with_value_produces_continuation_value()
    {
        var task = Task.CompletedTask;

        var bound = await task.Bind(async () =>
        {
            await Task.Yield();
            return 42;
        });

        await Assert.That(bound).IsEqualTo(42);
    }

    [Test]
    public async Task Bind_on_plain_task_to_plain_task_runs_continuation()
    {
        var task = Task.CompletedTask;
        var invoked = false;

        await task.Bind(async () =>
        {
            await Task.Yield();
            invoked = true;
        });

        await Assert.That(invoked).IsTrue();
    }

    [Test]
    public async Task Bind_runs_continuation_after_source_task_completes()
    {
        var order = new List<string>();
        var tcs = new TaskCompletionSource<int>();

        var bound = tcs.Task.Bind(x =>
        {
            order.Add($"continuation:{x}");
            return Task.FromResult(x);
        });

        order.Add("before completion");
        tcs.SetResult(1);
        await bound;

        await Assert.That(order).IsEquivalentTo(["before completion", "continuation:1"]);
    }

    [Test]
    public async Task Bind_on_faulted_task_propagates_exception_without_invoking_continuation()
    {
        var task = Task.FromException<int>(new InvalidOperationException("boom"));
        var invoked = false;

        var bound = task.Bind(x =>
        {
            invoked = true;
            return Task.FromResult(x);
        });

        await Assert.That(async () => await bound).Throws<InvalidOperationException>();
        await Assert.That(invoked).IsFalse();
    }
}
