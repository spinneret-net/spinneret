using System.Runtime.CompilerServices;

namespace Spinneret.Functional;

public readonly record struct TaskResult<TOk, TError>(Task<Result<TOk, TError>> Value)
{
    public Task<Result<TOk, TError>> AsTask() => Value;
    
    public TaskAwaiter<Result<TOk, TError>> GetAwaiter()
    {
        return Value.GetAwaiter();
    }
    
    public TaskResult<TError> Ignore<TIgnore>() where TIgnore : TOk
    {
        return Value.Map(res => res.Ignore<TIgnore>()).AsTaskResult();
    }
    
    public Task<T> Reduce<T>(Func<TOk, T> successCase, Func<TError, T> errorCase)
    {
        return Value.Map(res => res.Reduce(successCase, errorCase));
    }
    
    public TaskResult<TMapped, TError> Map<TMapped>(Func<TOk, TMapped> continuation)
    {
        return Value.Map(res => res.Map(continuation)).AsTaskResult();
    }

    public TaskResult<TOk, TMappedError> MapError<TMappedError>(Func<TError, TMappedError> continuation)
    {
        return Value.Map(res => res.MapError(continuation)).AsTaskResult();
    }
    
    public TaskResult<TError> Bind(Func<TOk, Result<TError>> continuation)
    {
        return Bind(x => continuation(x).AsTaskResult());
    }
    
    public TaskResult<TError> Bind(Func<TOk, Task> continuation)
    {
        return Bind(x => continuation(x).Map(Result<TError>.Ok).AsTaskResult());
    }
    
    public TaskResult<TError> Bind(Func<TOk, Task<Result<TError>>> continuation)
    {
        return Bind(x => continuation(x).AsTaskResult());
    }
    
    public TaskResult<TError> Bind(Func<TOk, TaskResult<TError>> continuation)
    {
        return Map(continuation)
            .Reduce(
                x => x,
                e => Task.FromResult(Result<TError>.Error(e)).AsTaskResult())
            .Bind(x => x.AsTask())
            .AsTaskResult();
    }
    
    public TaskResult<TMapped, TError> Bind<TMapped>(Func<TOk, Result<TMapped, TError>> continuation)
    {
        return Bind(x => continuation(x).AsTaskResult());
    }
    
    public TaskResult<TMapped, TError> Bind<TMapped>(Func<TOk, Task<TMapped>> continuation)
    {
        return Bind(x => continuation(x).Map(Result<TMapped, TError>.Ok).AsTaskResult());
    }
    
    public TaskResult<TMapped, TError> Bind<TMapped>(Func<TOk, Task<Result<TMapped, TError>>> continuation)
    {
        return Bind(x => continuation(x).AsTaskResult());
    }
    
    public TaskResult<TMapped, TError> Bind<TMapped>(Func<TOk, TaskResult<TMapped, TError>> continuation)
    {
        return Map(continuation)
            .Reduce(
                x => x,
                e => Task.FromResult(Result<TMapped, TError>.Error(e)).AsTaskResult())
            .Bind(x => x.AsTask())
            .AsTaskResult();
    }
    
    public TaskResult<TOk, TMappedError> BindError<TMappedError>(Func<TError, Result<TOk, TMappedError>> continuation)
    {
        return BindError(e => continuation(e).AsTaskResult());
    }
    
    public TaskResult<TOk, TMappedError> BindError<TMappedError>(Func<TError, Task<TMappedError>> continuation)
    {
        return BindError(e => continuation(e).Map(Result<TOk, TMappedError>.Error).AsTaskResult());
    }
    
    public TaskResult<TOk, TMappedError> BindError<TMappedError>(Func<TError, Task<Result<TOk, TMappedError>>> continuation)
    {
        return BindError(e => continuation(e).AsTaskResult());
    }
    
    public TaskResult<TOk, TMappedError> BindError<TMappedError>(Func<TError, TaskResult<TOk, TMappedError>> continuation)
    {
        return MapError(continuation)
            .Reduce(
                x => Task.FromResult(Result<TOk, TMappedError>.Ok(x)).AsTaskResult(),
                e => e)
            .Bind(x => x.AsTask())
            .AsTaskResult();
    }
}

public readonly record struct TaskResult<TError>(Task<Result<TError>> Value)
{
    public Task<Result<TError>> AsTask() => Value;
    
    public TaskAwaiter<Result<TError>> GetAwaiter()
    {
        return Value.GetAwaiter();
    }
    
    public Task<T> Reduce<T>(Func<T> successCase, Func<TError, T> errorCase)
    {
        return Value.Map(res => res.Reduce(successCase, errorCase));
    }

    public TaskResult<TMapped, TError> Map<TMapped>(Func<TMapped> continuation)
    {
        return Value.Map(res => res.Map(continuation)).AsTaskResult();
    }
    
    public TaskResult<TError> Bind(Func<Result<TError>> continuation)
    {
        return Bind(() => continuation().AsTaskResult());
    }
    
    public TaskResult<TError> Bind(Func<Task> continuation)
    {
        return Bind(() => continuation().Map(Result<TError>.Ok).AsTaskResult());
    }
    
    public TaskResult<TError> Bind(Func<Task<Result<TError>>> continuation)
    {
        return Bind(() => continuation().AsTaskResult());
    }
    
    public TaskResult<TError> Bind(Func<TaskResult<TError>> continuation)
    {
        return Map(continuation)
            .Reduce(
                x => x,
                e => Task.FromResult(Result<TError>.Error(e)).AsTaskResult())
            .Bind(x => x.AsTask())
            .AsTaskResult();
    }
    
    public TaskResult<TOk, TError> Bind<TOk>(Func<Result<TOk, TError>> continuation)
    {
        return Bind(() => continuation().AsTaskResult());
    }
    
    public TaskResult<TOk, TError> Bind<TOk>(Func<Task<TOk>> continuation)
    {
        return Bind(() => continuation().Map(Result<TOk, TError>.Ok).AsTaskResult());
    }
    
    public TaskResult<TOk, TError> Bind<TOk>(Func<Task<Result<TOk, TError>>> continuation)
    {
        return Bind(() => continuation().AsTaskResult());
    }
    
    public TaskResult<TOk, TError> Bind<TOk>(Func<TaskResult<TOk, TError>> continuation)
    {
        return Map(continuation)
            .Reduce(
                x => x,
                e => Task.FromResult(Result<TOk, TError>.Error(e)).AsTaskResult())
            .Bind(x => x.AsTask())
            .AsTaskResult();
    }

    public TaskResult<TMappedError> MapError<TMappedError>(Func<TError, TMappedError> continuation)
    {
        return Value.Map(res => res.MapError(continuation)).AsTaskResult();
    }
    
    public TaskResult<TMappedError> BindError<TMappedError>(Func<TError, Result<TMappedError>> continuation)
    {
        return BindError(e => continuation(e).AsTaskResult());
    }
    
    public TaskResult<TMappedError> BindError<TMappedError>(Func<TError, Task<TMappedError>> continuation)
    {
        return BindError(e => continuation(e).Map(Result<TMappedError>.Error).AsTaskResult());
    }
    
    public TaskResult<TMappedError> BindError<TMappedError>(Func<TError, Task<Result<TMappedError>>> continuation)
    {
        return BindError(e => continuation(e).AsTaskResult());
    }
    
    public TaskResult<TMappedError> BindError<TMappedError>(Func<TError, TaskResult<TMappedError>> continuation)
    {
        return MapError(continuation)
            .Reduce(
                () => Task.FromResult(Result<TMappedError>.Ok()).AsTaskResult(),
                e => e)
            .Bind(x => x.AsTask())
            .AsTaskResult();
    }
}

public static class TaskResult
{
    public static TaskResult<TError> Ok<TError>()
    {
        return Task.FromResult(Result<TError>.Ok()).AsTaskResult();
    }
    
    public static TaskResult<TError> Error<TError>(TError error)
    {
        return Task.FromResult(Result<TError>.Error(error)).AsTaskResult();
    }
    
    public static TaskResult<TOk, TError> Ok<TOk, TError>(TOk value)
    {
        return Task.FromResult(Result<TOk, TError>.Ok(value)).AsTaskResult();
    }
    
    public static TaskResult<TOk, TError> Error<TOk, TError>(TError error)
    {
        return Task.FromResult(Result<TOk, TError>.Error(error)).AsTaskResult();
    }
}

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