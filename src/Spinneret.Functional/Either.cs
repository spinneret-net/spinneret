using System.Text.Json.Serialization;

namespace Spinneret.Functional
{
    public record Either<T1, T2>
    {
        [JsonInclude]
        private readonly int tag;

        [JsonInclude]
        private readonly T1? value1;

        [JsonInclude]
        private readonly T2? value2;

        [JsonConstructor]
#pragma warning disable IDE0051 // Private member is used for deserialization only
        private Either(int tag, T1? value1, T2? value2)
#pragma warning restore IDE0051
        {
            this.tag = tag;
            this.value1 = value1;
            this.value2 = value2;
        }

        public Either(T1 value)
        {
            tag = 1;
            value1 = value;
            value2 = default;
        }

        public Either(T2 value)
        {
            tag = 2;
            value1 = default;
            value2 = value;
        }

        public T Reduce<T>(
            Func<T1, T> f1,
            Func<T2, T> f2
        )
        {
            return tag switch
            {
                1 => f1(value1!),
                2 => f2(value2!),
                _ => throw new NotImplementedException(),
            };
        }

        public void Iter(
            Action<T1> f1,
            Action<T2> f2
        )
        {
            switch (tag)
            {
                case 1:
                    f1(value1!);
                    return;
                case 2:
                    f2(value2!);
                    return;
                default:
                    throw new NotImplementedException();
            }
        }

        public Either<T3, T4> Map<T3, T4>(
            Func<T1, T3> f1,
            Func<T2, T4> f2
        )
        {
            return tag switch
            {
                1 => new Either<T3, T4>(f1(value1!)),
                2 => new Either<T3, T4>(f2(value2!)),
                _ => throw new NotImplementedException(),
            };
        }

        public Either<T2, T1> Reverse()
        {
            return new(tag == 1 ? 2 : 1, value2, value1);
        }
        public Result<Either<T3, T4>, TError> TraverseResult<T3, T4, TError>(
            Func<T1, Result<T3, TError>> f1,
            Func<T2, Result<T4, TError>> f2
        )
        {
            return tag switch
            {
                1 => f1(value1!).Map(x => new Either<T3, T4>(x)),
                2 => f2(value2!).Map(x => new Either<T3, T4>(x)),
                _ => throw new NotImplementedException(),
            };
        }
    }
}
