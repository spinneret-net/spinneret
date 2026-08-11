namespace Spinneret.Functional
{
    public record Result<TError>
    {
        private readonly TError _error;
        private readonly bool _isOk;

        private Result(TError error, bool isOk)
        {
            _error = error;
            _isOk = isOk;
        }

        public Result<TMapped, TError> Map<TMapped>(Func<TMapped> okFn)
        {
            return Reduce(
                () => Result<TMapped, TError>.Ok(okFn()),
                Result<TMapped, TError>.Error
            );
        }

        public Result<TMappedError> MapError<TMappedError>(Func<TError, TMappedError> errorFn)
        {
            return Reduce(
                Result<TMappedError>.Ok,
                error => Result<TMappedError>.Error(errorFn(error))
            );
        }

        public Result<TError> Bind(Func<Result<TError>> okFn)
        {
            return Reduce(
                okFn,
                Error
            );
        }

        public Result<TMapped, TError> Bind<TMapped>(Func<Result<TMapped, TError>> okFn)
        {
            return Reduce(
                okFn,
                Result<TMapped, TError>.Error
            );
        }

        public Task<Result<TError>> TraverseTask(Func<Task> okFn)
        {
            return Reduce(
                async () =>
                {
                    await okFn();
                    return Ok();
                },
                error =>
                {
                    return Task.FromResult(Error(error));
                }
            );
        }

        public Task<Result<TMapped, TError>> TraverseTask<TMapped>(Func<Task<TMapped>> okFn)
        {
            return Reduce(
                async () =>
                {
                    var result = await okFn();
                    return Result<TMapped, TError>.Ok(result);
                },
                error =>
                {
                    return Task.FromResult(Result<TMapped, TError>.Error(error));
                }
            );
        }

        public void ThrowOnError(Func<TError, Exception> fn)
        {
            if (_isOk)
            {
                return;
            }

            throw fn(_error);
        }

        public void Iter(Action okFn, Action<TError> errorFn)
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

        public T Reduce<T>(Func<T> successCase, Func<TError, T> errorCase)
        {
            if (_isOk)
            {
                return successCase();
            }

            return errorCase(_error);
        }

        public static Result<TError> Ok()
        {
            return new Result<TError>(default!, true);
        }

        public static Result<TError> Error(TError error)
        {
            return new Result<TError>(error, false);
        }
    }

    public record Result<TOk, TError>
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

        public Result<TError> Ignore<TIgnore>() where TIgnore : TOk
        {
            return Reduce(
                _ => Result<TError>.Ok(),
                Result<TError>.Error
            );
        }

        public Result<TMapped, TError> Map<TMapped>(Func<TOk, TMapped> okFn)
        {
            return Reduce(
                value => Result<TMapped, TError>.Ok(okFn(value)),
                Result<TMapped, TError>.Error
            );
        }

        public Result<TOk, TMappedError> MapError<TMappedError>(Func<TError, TMappedError> errorFn)
        {
            return Reduce(
                Result<TOk, TMappedError>.Ok,
                error => Result<TOk, TMappedError>.Error(errorFn(error))
            );
        }

        public Result<TMapped, TError> Bind<TMapped>(Func<TOk, Result<TMapped, TError>> okFn)
        {
            return Reduce(
                okFn,
                Result<TMapped, TError>.Error
            );
        }

        public Result<TError> Bind(Func<TOk, Result<TError>> okFn)
        {
            return Reduce(
                okFn,
                Result<TError>.Error
            );
        }

        public Task<Result<TError>> TraverseTask(Func<TOk, Task> okFn)
        {
            return Reduce(
                async ok =>
                {
                    await okFn(ok);
                    return Result<TError>.Ok();
                },
                error =>
                {
                    return Task.FromResult(Result<TError>.Error(error));
                }
            );
        }

        public Task<Result<TMapped, TError>> TraverseTask<TMapped>(Func<TOk, Task<TMapped>> okFn)
        {
            return Reduce(
                async ok =>
                {
                    var result = await okFn(ok);
                    return Result<TMapped, TError>.Ok(result);
                },
                error => Task.FromResult(Result<TMapped, TError>.Error(error)));
        }

        public TOk ThrowOnError(Func<TError, Exception> fn)
        {
            if (_isOk)
            {
                return _ok;
            }

            throw fn(_error);
        }

        public void Iter(Action<TOk> okFn, Action<TError> errorFn)
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

        public T Reduce<T>(Func<TOk, T> okFn, Func<TError, T> errorFn)
        {
            if (_isOk)
            {
                return okFn(_ok);
            }

            return errorFn(_error);
        }

        public static Result<TOk, TError> Ok(TOk value)
        {
            return new Result<TOk, TError>(value, default!, true);
        }

        public static Result<TOk, TError> Error(TError error)
        {
            return new Result<TOk, TError>(default!, error, false);
        }

        public static Result<TOk, TError> FromNullable(TOk? value, Func<TError> errorFn)
        {
            if (value is not null)
            {
                return Ok(value);
            }

            return Error(errorFn());
        }
    }

    public static class Result
    {
        public static Result<TError> Ok<TError>()
        {
            return Result<TError>.Ok();
        }

        public static Result<TOk, TError> Ok<TOk, TError>(TOk value)
        {
            return Result<TOk, TError>.Ok(value);
        }

        public static Result<TError> Error<TError>(TError error)
        {
            return Result<TError>.Error(error);
        }

        public static Result<TOk, TError> Error<TOk, TError>(TError error)
        {
            return Result<TOk, TError>.Error(error);
        }

        public static Result<TOk, TError> FromNullable<TOk, TError>(TOk? value, Func<TError> errorFn)
        {
            return Result<TOk, TError>.FromNullable(value, errorFn);
        }
        
        public static Result<Result<TOk, TError1>, TError2> Swap<TOk, TError1, TError2>(this Result<Result<TOk,TError2>, TError1> input)
        {
            return input.Reduce<Result<Result<TOk, TError1>, TError2>>(
                inner => inner.Reduce<Result<Result<TOk, TError1>, TError2>>(
                    x => Result<Result<TOk, TError1>, TError2>.Ok(Result<TOk, TError1>.Ok(x)),
                    Result<Result<TOk, TError1>, TError2>.Error
                ),
                e => Result<Result<TOk, TError1>, TError2>.Ok(Result<TOk, TError1>.Error(e))
            );
        }
        
        public static Result<Result<TError1>, TError2> Swap<TError1, TError2>(this Result<Result<TError2>, TError1> input)
        {
            return input.Reduce<Result<Result<TError1>, TError2>>(
                inner => inner.Reduce(
                    () => Result<Result<TError1>, TError2>.Ok(Result<TError1>.Ok()),
                    Result<Result<TError1>, TError2>.Error
                ),
                e => Result<Result<TError1>, TError2>.Ok(Result<TError1>.Error(e))
            );
        }
    }
    
    public static class ResultExtensions
    {
        public static Result<TOk, Either<TError1, TError2>> Flatten<TOk, TError1, TError2>(this Result<Result<TOk, TError2>, TError1> res)
        {
            return res
                .MapError(e => new Either<TError1, TError2>(e))
                .Bind(x => x.MapError(e => new Either<TError1, TError2>(e)));
        }
    }
}