namespace Spinneret.Functional
{
    public static class LinqExtensions
    {
        public static IEnumerable<T2> Choose<T1, T2>(this IEnumerable<T1> enumerable, Func<T1, T2?> func) where T2: class
        {
            return enumerable.Select(x => func(x)!).Where(x => x != null);
        }

		public static IEnumerable<T2> Choose<T1, T2>(this IEnumerable<T1> enumerable, Func<T1, T2?> func) where T2 : struct
		{
			return enumerable.Select(x => func(x)).Where(x => x.HasValue).Select(x => x!.Value);
		}
	}
}
