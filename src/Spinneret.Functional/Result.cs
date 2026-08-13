namespace Spinneret.Functional;

/// <summary>
/// The outcome of an operation that produces no value: either success, or an error of
/// <typeparamref name="TError"/>. The error type is unconstrained — an enum, a record,
/// or any type the application uses to describe failures.
/// </summary>
public sealed record Result<TError>
{
    private readonly TError _error;
    private readonly bool _isOk;

    private Result(TError error, bool isOk)
    {
        _error = error;
        _isOk = isOk;
    }

    /// <summary>Produces a value on success, turning this into a <see cref="Result{TMapped, TError}"/>.</summary>
    public Result<TMapped, TError> Map<TMapped>(Func<TMapped> okFn)
    {
        return Match(
            () => Result<TMapped, TError>.Ok(okFn()),
            Result<TMapped, TError>.Error
        );
    }

    /// <summary>Transforms the error, leaving success untouched.</summary>
    public Result<TMappedError> MapError<TMappedError>(Func<TError, TMappedError> errorFn)
    {
        return Match(
            Result<TMappedError>.Ok,
            error => Result<TMappedError>.Error(errorFn(error))
        );
    }

    /// <summary>Chains another result-producing operation, run only on success.</summary>
    public Result<TError> Bind(Func<Result<TError>> okFn)
    {
        return Match(
            okFn,
            Error
        );
    }

    /// <summary>Chains a value-producing result operation, run only on success.</summary>
    public Result<TMapped, TError> Bind<TMapped>(Func<Result<TMapped, TError>> okFn)
    {
        return Match(
            okFn,
            Result<TMapped, TError>.Error
        );
    }

    /// <summary>Runs an async operation on success; an error short-circuits without running it.</summary>
    public Task<Result<TError>> TraverseTask(Func<Task> okFn)
    {
        return Match(
            async () =>
            {
                await okFn();
                return Ok();
            },
            error => Task.FromResult(Error(error))
        );
    }

    /// <summary>Runs an async value-producing operation on success; an error short-circuits without running it.</summary>
    public Task<Result<TMapped, TError>> TraverseTask<TMapped>(Func<Task<TMapped>> okFn)
    {
        return Match(
            async () =>
            {
                var result = await okFn();
                return Result<TMapped, TError>.Ok(result);
            },
            error => Task.FromResult(Result<TMapped, TError>.Error(error))
        );
    }

    /// <summary>Throws the exception produced by <paramref name="fn"/> if this is an error.</summary>
    public void ThrowOnError(Func<TError, Exception> fn)
    {
        if (_isOk)
        {
            return;
        }

        throw fn(_error);
    }

    /// <summary>Runs exactly one of the two actions, depending on the outcome.</summary>
    public void Switch(Action okFn, Action<TError> errorFn)
    {
        if (_isOk)
        {
            okFn();
        }
        else
        {
            errorFn(_error);
        }
    }

    /// <summary>Collapses both outcomes into a single value. The one place a result is unpacked.</summary>
    public T Match<T>(Func<T> okFn, Func<TError, T> errorFn)
    {
        if (_isOk)
        {
            return okFn();
        }

        return errorFn(_error);
    }

    /// <summary>Creates a successful result.</summary>
    public static Result<TError> Ok()
    {
        return new Result<TError>(default!, true);
    }

    /// <summary>Creates a failed result carrying <paramref name="error"/>.</summary>
    public static Result<TError> Error(TError error)
    {
        return new Result<TError>(error, false);
    }

    public override string ToString()
    {
        return _isOk ? "Ok" : $"Error({_error})";
    }
}

/// <summary>
/// The outcome of an operation: a value of <typeparamref name="TOk"/> on success, or an error
/// of <typeparamref name="TError"/>. The error type is unconstrained — an enum, a record,
/// or any type the application uses to describe failures.
/// </summary>
public sealed record Result<TOk, TError>
{
    private readonly TOk _ok;
    private readonly TError _error;
    private readonly bool _isOk;

    private Result(TOk ok, TError error, bool isOk)
    {
        _ok = ok;
        _error = error;
        _isOk = isOk;
    }

    /// <summary>Discards the success value, keeping only the outcome.</summary>
    public Result<TError> Ignore()
    {
        return Match(
            _ => Result<TError>.Ok(),
            Result<TError>.Error
        );
    }

    /// <summary>Transforms the success value, leaving errors untouched.</summary>
    public Result<TMapped, TError> Map<TMapped>(Func<TOk, TMapped> okFn)
    {
        return Match(
            value => Result<TMapped, TError>.Ok(okFn(value)),
            Result<TMapped, TError>.Error
        );
    }

    /// <summary>Transforms the error, leaving the success value untouched.</summary>
    public Result<TOk, TMappedError> MapError<TMappedError>(Func<TError, TMappedError> errorFn)
    {
        return Match(
            Result<TOk, TMappedError>.Ok,
            error => Result<TOk, TMappedError>.Error(errorFn(error))
        );
    }

    /// <summary>Chains another result-producing operation, run only on success.</summary>
    public Result<TMapped, TError> Bind<TMapped>(Func<TOk, Result<TMapped, TError>> okFn)
    {
        return Match(
            okFn,
            Result<TMapped, TError>.Error
        );
    }

    /// <summary>Chains a value-less result operation, run only on success.</summary>
    public Result<TError> Bind(Func<TOk, Result<TError>> okFn)
    {
        return Match(
            okFn,
            Result<TError>.Error
        );
    }

    /// <summary>Runs an async operation on the success value; an error short-circuits without running it.</summary>
    public Task<Result<TError>> TraverseTask(Func<TOk, Task> okFn)
    {
        return Match(
            async ok =>
            {
                await okFn(ok);
                return Result<TError>.Ok();
            },
            error => Task.FromResult(Result<TError>.Error(error))
        );
    }

    /// <summary>Runs an async value-producing operation on the success value; an error short-circuits without running it.</summary>
    public Task<Result<TMapped, TError>> TraverseTask<TMapped>(Func<TOk, Task<TMapped>> okFn)
    {
        return Match(
            async ok =>
            {
                var result = await okFn(ok);
                return Result<TMapped, TError>.Ok(result);
            },
            error => Task.FromResult(Result<TMapped, TError>.Error(error)));
    }

    /// <summary>Returns the success value, or throws the exception produced by <paramref name="fn"/>.</summary>
    public TOk ThrowOnError(Func<TError, Exception> fn)
    {
        if (_isOk)
        {
            return _ok;
        }

        throw fn(_error);
    }

    /// <summary>Runs exactly one of the two actions, depending on the outcome.</summary>
    public void Switch(Action<TOk> okFn, Action<TError> errorFn)
    {
        if (_isOk)
        {
            okFn(_ok);
        }
        else
        {
            errorFn(_error);
        }
    }

    /// <summary>Collapses both outcomes into a single value. The one place a result is unpacked.</summary>
    public T Match<T>(Func<TOk, T> okFn, Func<TError, T> errorFn)
    {
        if (_isOk)
        {
            return okFn(_ok);
        }

        return errorFn(_error);
    }

    /// <summary>Creates a successful result carrying <paramref name="value"/>.</summary>
    public static Result<TOk, TError> Ok(TOk value)
    {
        return new Result<TOk, TError>(value, default!, true);
    }

    /// <summary>Creates a failed result carrying <paramref name="error"/>.</summary>
    public static Result<TOk, TError> Error(TError error)
    {
        return new Result<TOk, TError>(default!, error, false);
    }

    /// <summary>Lifts a nullable reference: a value becomes Ok, null becomes the error produced by <paramref name="errorFn"/>.</summary>
    public static Result<TOk, TError> FromNullable(TOk? value, Func<TError> errorFn)
    {
        if (value is not null)
        {
            return Ok(value);
        }

        return Error(errorFn());
    }

    public override string ToString()
    {
        return _isOk ? $"Ok({_ok})" : $"Error({_error})";
    }
}

/// <summary>Factory methods for creating results with inferred or explicit type arguments.</summary>
public static class Result
{
    /// <summary>Creates a successful value-less result.</summary>
    public static Result<TError> Ok<TError>()
    {
        return Result<TError>.Ok();
    }

    /// <summary>Creates a successful result carrying <paramref name="value"/>.</summary>
    public static Result<TOk, TError> Ok<TOk, TError>(TOk value)
    {
        return Result<TOk, TError>.Ok(value);
    }

    /// <summary>Creates a failed value-less result carrying <paramref name="error"/>.</summary>
    public static Result<TError> Error<TError>(TError error)
    {
        return Result<TError>.Error(error);
    }

    /// <summary>Creates a failed result carrying <paramref name="error"/>.</summary>
    public static Result<TOk, TError> Error<TOk, TError>(TError error)
    {
        return Result<TOk, TError>.Error(error);
    }

    /// <summary>Lifts a nullable reference: a value becomes Ok, null becomes the error produced by <paramref name="errorFn"/>.</summary>
    public static Result<TOk, TError> FromNullable<TOk, TError>(TOk? value, Func<TError> errorFn)
    {
        return Result<TOk, TError>.FromNullable(value, errorFn);
    }

    /// <summary>Lifts a nullable value type: a value becomes Ok (unwrapped), null becomes the error produced by <paramref name="errorFn"/>.</summary>
    public static Result<TOk, TError> FromNullable<TOk, TError>(TOk? value, Func<TError> errorFn)
        where TOk : struct
    {
        if (value.HasValue)
        {
            return Result<TOk, TError>.Ok(value.Value);
        }

        return Result<TOk, TError>.Error(errorFn());
    }
}

/// <summary>Combinators over nested results.</summary>
public static class ResultExtensions
{
    /// <summary>Swaps the nesting order of a result of a result, so the inner error becomes the outer one.</summary>
    public static Result<Result<TOk, TError1>, TError2> Swap<TOk, TError1, TError2>(this Result<Result<TOk, TError2>, TError1> input)
    {
        return input.Match<Result<Result<TOk, TError1>, TError2>>(
            inner => inner.Match<Result<Result<TOk, TError1>, TError2>>(
                x => Result<Result<TOk, TError1>, TError2>.Ok(Result<TOk, TError1>.Ok(x)),
                Result<Result<TOk, TError1>, TError2>.Error
            ),
            e => Result<Result<TOk, TError1>, TError2>.Ok(Result<TOk, TError1>.Error(e))
        );
    }

    /// <summary>Swaps the nesting order of a result of a value-less result, so the inner error becomes the outer one.</summary>
    public static Result<Result<TError1>, TError2> Swap<TError1, TError2>(this Result<Result<TError2>, TError1> input)
    {
        return input.Match<Result<Result<TError1>, TError2>>(
            inner => inner.Match(
                () => Result<Result<TError1>, TError2>.Ok(Result<TError1>.Ok()),
                Result<Result<TError1>, TError2>.Error
            ),
            e => Result<Result<TError1>, TError2>.Ok(Result<TError1>.Error(e))
        );
    }

    /// <summary>Merges a nested result's two error types into a single <see cref="Either{TError1, TError2}"/>.</summary>
    public static Result<TOk, Either<TError1, TError2>> Flatten<TOk, TError1, TError2>(this Result<Result<TOk, TError2>, TError1> res)
    {
        return res
            .MapError(Either<TError1, TError2>.First)
            .Bind(x => x.MapError(Either<TError1, TError2>.Second));
    }
}
