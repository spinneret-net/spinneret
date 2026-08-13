using System.Runtime.CompilerServices;

namespace Spinneret.Functional;

/// <summary>
/// An awaitable <see cref="Task{T}"/> of <see cref="Result{TOk, TError}"/> with result combinators,
/// so async result pipelines compose without unwrapping at every step.
/// Always create one via <see cref="TaskResult"/> factories or <c>AsTaskResult()</c> — a
/// <c>default</c> instance has no underlying task and throws on use.
/// </summary>
public readonly record struct TaskResult<TOk, TError>(Task<Result<TOk, TError>> Value)
{
    private Task<Result<TOk, TError>> UnderlyingTask => Value ?? throw new InvalidOperationException(
        "This TaskResult was default-constructed and has no underlying task. Create it via TaskResult.Ok/Error or AsTaskResult().");

    public Task<Result<TOk, TError>> AsTask() => UnderlyingTask;

    public TaskAwaiter<Result<TOk, TError>> GetAwaiter()
    {
        return UnderlyingTask.GetAwaiter();
    }

    /// <summary>Discards the success value, keeping only the outcome.</summary>
    public TaskResult<TError> Ignore()
    {
        return UnderlyingTask.Map(res => res.Ignore()).AsTaskResult();
    }

    /// <summary>Collapses both outcomes into a single value once the task completes.</summary>
    public Task<T> Match<T>(Func<TOk, T> okFn, Func<TError, T> errorFn)
    {
        return UnderlyingTask.Map(res => res.Match(okFn, errorFn));
    }

    /// <summary>Transforms the success value, leaving errors untouched.</summary>
    public TaskResult<TMapped, TError> Map<TMapped>(Func<TOk, TMapped> continuation)
    {
        return UnderlyingTask.Map(res => res.Map(continuation)).AsTaskResult();
    }

    /// <summary>Transforms the error, leaving the success value untouched.</summary>
    public TaskResult<TOk, TMappedError> MapError<TMappedError>(Func<TError, TMappedError> continuation)
    {
        return UnderlyingTask.Map(res => res.MapError(continuation)).AsTaskResult();
    }

    /// <summary>Chains a result-producing operation, run only on success.</summary>
    public TaskResult<TError> Bind(Func<TOk, Result<TError>> continuation)
    {
        return Bind(x => continuation(x).AsTaskResult());
    }

    /// <summary>Chains an async operation, run only on success; its completion means Ok.</summary>
    public TaskResult<TError> Bind(Func<TOk, Task> continuation)
    {
        return Bind(x => continuation(x).Map(Result<TError>.Ok).AsTaskResult());
    }

    /// <summary>Chains an async result-producing operation, run only on success.</summary>
    public TaskResult<TError> Bind(Func<TOk, Task<Result<TError>>> continuation)
    {
        return Bind(x => continuation(x).AsTaskResult());
    }

    /// <summary>Chains another task-result operation, run only on success.</summary>
    public TaskResult<TError> Bind(Func<TOk, TaskResult<TError>> continuation)
    {
        return Map(continuation)
            .Match(
                x => x,
                e => Task.FromResult(Result<TError>.Error(e)).AsTaskResult())
            .Bind(x => x.AsTask())
            .AsTaskResult();
    }

    /// <summary>Chains a value-producing result operation, run only on success.</summary>
    public TaskResult<TMapped, TError> Bind<TMapped>(Func<TOk, Result<TMapped, TError>> continuation)
    {
        return Bind(x => continuation(x).AsTaskResult());
    }

    /// <summary>Chains an async value-producing operation, run only on success; its value becomes Ok.</summary>
    public TaskResult<TMapped, TError> Bind<TMapped>(Func<TOk, Task<TMapped>> continuation)
    {
        return Bind(x => continuation(x).Map(Result<TMapped, TError>.Ok).AsTaskResult());
    }

    /// <summary>Chains an async result-producing operation, run only on success.</summary>
    public TaskResult<TMapped, TError> Bind<TMapped>(Func<TOk, Task<Result<TMapped, TError>>> continuation)
    {
        return Bind(x => continuation(x).AsTaskResult());
    }

    /// <summary>Chains another task-result operation, run only on success.</summary>
    public TaskResult<TMapped, TError> Bind<TMapped>(Func<TOk, TaskResult<TMapped, TError>> continuation)
    {
        return Map(continuation)
            .Match(
                x => x,
                e => Task.FromResult(Result<TMapped, TError>.Error(e)).AsTaskResult())
            .Bind(x => x.AsTask())
            .AsTaskResult();
    }

    /// <summary>Chains a recovery operation, run only on error.</summary>
    public TaskResult<TOk, TMappedError> BindError<TMappedError>(Func<TError, Result<TOk, TMappedError>> continuation)
    {
        return BindError(e => continuation(e).AsTaskResult());
    }

    /// <summary>Chains an async error-mapping operation, run only on error; its value becomes the new error.</summary>
    public TaskResult<TOk, TMappedError> BindError<TMappedError>(Func<TError, Task<TMappedError>> continuation)
    {
        return BindError(e => continuation(e).Map(Result<TOk, TMappedError>.Error).AsTaskResult());
    }

    /// <summary>Chains an async recovery operation, run only on error.</summary>
    public TaskResult<TOk, TMappedError> BindError<TMappedError>(Func<TError, Task<Result<TOk, TMappedError>>> continuation)
    {
        return BindError(e => continuation(e).AsTaskResult());
    }

    /// <summary>Chains another task-result recovery operation, run only on error.</summary>
    public TaskResult<TOk, TMappedError> BindError<TMappedError>(Func<TError, TaskResult<TOk, TMappedError>> continuation)
    {
        return MapError(continuation)
            .Match(
                x => Task.FromResult(Result<TOk, TMappedError>.Ok(x)).AsTaskResult(),
                e => e)
            .Bind(x => x.AsTask())
            .AsTaskResult();
    }
}

/// <summary>
/// An awaitable <see cref="Task{T}"/> of the value-less <see cref="Result{TError}"/> with result
/// combinators, so async result pipelines compose without unwrapping at every step.
/// Always create one via <see cref="TaskResult"/> factories or <c>AsTaskResult()</c> — a
/// <c>default</c> instance has no underlying task and throws on use.
/// </summary>
public readonly record struct TaskResult<TError>(Task<Result<TError>> Value)
{
    private Task<Result<TError>> UnderlyingTask => Value ?? throw new InvalidOperationException(
        "This TaskResult was default-constructed and has no underlying task. Create it via TaskResult.Ok/Error or AsTaskResult().");

    public Task<Result<TError>> AsTask() => UnderlyingTask;

    public TaskAwaiter<Result<TError>> GetAwaiter()
    {
        return UnderlyingTask.GetAwaiter();
    }

    /// <summary>Collapses both outcomes into a single value once the task completes.</summary>
    public Task<T> Match<T>(Func<T> okFn, Func<TError, T> errorFn)
    {
        return UnderlyingTask.Map(res => res.Match(okFn, errorFn));
    }

    /// <summary>Produces a value on success, turning this into a <see cref="TaskResult{TMapped, TError}"/>.</summary>
    public TaskResult<TMapped, TError> Map<TMapped>(Func<TMapped> continuation)
    {
        return UnderlyingTask.Map(res => res.Map(continuation)).AsTaskResult();
    }

    /// <summary>Chains a result-producing operation, run only on success.</summary>
    public TaskResult<TError> Bind(Func<Result<TError>> continuation)
    {
        return Bind(() => continuation().AsTaskResult());
    }

    /// <summary>Chains an async operation, run only on success; its completion means Ok.</summary>
    public TaskResult<TError> Bind(Func<Task> continuation)
    {
        return Bind(() => continuation().Map(Result<TError>.Ok).AsTaskResult());
    }

    /// <summary>Chains an async result-producing operation, run only on success.</summary>
    public TaskResult<TError> Bind(Func<Task<Result<TError>>> continuation)
    {
        return Bind(() => continuation().AsTaskResult());
    }

    /// <summary>Chains another task-result operation, run only on success.</summary>
    public TaskResult<TError> Bind(Func<TaskResult<TError>> continuation)
    {
        return Map(continuation)
            .Match(
                x => x,
                e => Task.FromResult(Result<TError>.Error(e)).AsTaskResult())
            .Bind(x => x.AsTask())
            .AsTaskResult();
    }

    /// <summary>Chains a value-producing result operation, run only on success.</summary>
    public TaskResult<TOk, TError> Bind<TOk>(Func<Result<TOk, TError>> continuation)
    {
        return Bind(() => continuation().AsTaskResult());
    }

    /// <summary>Chains an async value-producing operation, run only on success; its value becomes Ok.</summary>
    public TaskResult<TOk, TError> Bind<TOk>(Func<Task<TOk>> continuation)
    {
        return Bind(() => continuation().Map(Result<TOk, TError>.Ok).AsTaskResult());
    }

    /// <summary>Chains an async result-producing operation, run only on success.</summary>
    public TaskResult<TOk, TError> Bind<TOk>(Func<Task<Result<TOk, TError>>> continuation)
    {
        return Bind(() => continuation().AsTaskResult());
    }

    /// <summary>Chains another task-result operation, run only on success.</summary>
    public TaskResult<TOk, TError> Bind<TOk>(Func<TaskResult<TOk, TError>> continuation)
    {
        return Map(continuation)
            .Match(
                x => x,
                e => Task.FromResult(Result<TOk, TError>.Error(e)).AsTaskResult())
            .Bind(x => x.AsTask())
            .AsTaskResult();
    }

    /// <summary>Transforms the error, leaving success untouched.</summary>
    public TaskResult<TMappedError> MapError<TMappedError>(Func<TError, TMappedError> continuation)
    {
        return UnderlyingTask.Map(res => res.MapError(continuation)).AsTaskResult();
    }

    /// <summary>Chains a recovery operation, run only on error.</summary>
    public TaskResult<TMappedError> BindError<TMappedError>(Func<TError, Result<TMappedError>> continuation)
    {
        return BindError(e => continuation(e).AsTaskResult());
    }

    /// <summary>Chains an async error-mapping operation, run only on error; its value becomes the new error.</summary>
    public TaskResult<TMappedError> BindError<TMappedError>(Func<TError, Task<TMappedError>> continuation)
    {
        return BindError(e => continuation(e).Map(Result<TMappedError>.Error).AsTaskResult());
    }

    /// <summary>Chains an async recovery operation, run only on error.</summary>
    public TaskResult<TMappedError> BindError<TMappedError>(Func<TError, Task<Result<TMappedError>>> continuation)
    {
        return BindError(e => continuation(e).AsTaskResult());
    }

    /// <summary>Chains another task-result recovery operation, run only on error.</summary>
    public TaskResult<TMappedError> BindError<TMappedError>(Func<TError, TaskResult<TMappedError>> continuation)
    {
        return MapError(continuation)
            .Match(
                () => Task.FromResult(Result<TMappedError>.Ok()).AsTaskResult(),
                e => e)
            .Bind(x => x.AsTask())
            .AsTaskResult();
    }
}

/// <summary>Factory methods for creating already-completed task results.</summary>
public static class TaskResult
{
    /// <summary>Creates a completed, successful value-less task result.</summary>
    public static TaskResult<TError> Ok<TError>()
    {
        return Task.FromResult(Result<TError>.Ok()).AsTaskResult();
    }

    /// <summary>Creates a completed, failed value-less task result.</summary>
    public static TaskResult<TError> Error<TError>(TError error)
    {
        return Task.FromResult(Result<TError>.Error(error)).AsTaskResult();
    }

    /// <summary>Creates a completed, successful task result carrying <paramref name="value"/>.</summary>
    public static TaskResult<TOk, TError> Ok<TOk, TError>(TOk value)
    {
        return Task.FromResult(Result<TOk, TError>.Ok(value)).AsTaskResult();
    }

    /// <summary>Creates a completed, failed task result carrying <paramref name="error"/>.</summary>
    public static TaskResult<TOk, TError> Error<TOk, TError>(TError error)
    {
        return Task.FromResult(Result<TOk, TError>.Error(error)).AsTaskResult();
    }
}

/// <summary>Conversions into <see cref="TaskResult{TOk, TError}"/> / <see cref="TaskResult{TError}"/>.</summary>
public static class TaskResultExtensions
{
    public static TaskResult<TOk, TError> AsTaskResult<TOk, TError>(this Task<Result<TOk, TError>> taskOfResult)
    {
        return new TaskResult<TOk, TError>(taskOfResult);
    }

    public static TaskResult<TError> AsTaskResult<TError>(this Task<Result<TError>> taskOfResult)
    {
        return new TaskResult<TError>(taskOfResult);
    }

    public static TaskResult<TOk, TError> AsTaskResult<TOk, TError>(this Result<TOk, TError> result)
    {
        return new TaskResult<TOk, TError>(Task.FromResult(result));
    }

    public static TaskResult<TError> AsTaskResult<TError>(this Result<TError> result)
    {
        return new TaskResult<TError>(Task.FromResult(result));
    }

    public static TaskResult<TOk, TError> AsTaskResult<TOk, TError>(this Task<TOk> result)
    {
        return new TaskResult<TOk, TError>(result.Map(Result<TOk, TError>.Ok));
    }

    public static TaskResult<TError> AsTaskResult<TError>(this Task result)
    {
        return new TaskResult<TError>(result.Map(Result<TError>.Ok));
    }
}
