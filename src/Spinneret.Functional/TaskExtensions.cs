namespace Spinneret.Functional
{
    public static class TaskExtensions
    {
        public static async Task<K> Bind<T, K>(this Task<T> task, Func<T, Task<K>> continuation)
        {
            var res = await task;
            return await continuation(res);
        }

        public static async Task Bind<T>(this Task<T> task, Func<T, Task> continuation)
        {
            var res = await task;
            await continuation(res);
        }

        public static async Task<T> Bind<T>(this Task task, Func<Task<T>> continuation)
        {
            await task;
            return await continuation();
        }

        public static async Task Bind(this Task task, Func<Task> continuation)
        {
            await task;
            await continuation();
        }

        public static async Task<K> Map<T, K>(this Task<T> task, Func<T, K> continuation)
        {
            var res = await task;
            return continuation(res);
        }

        public static async Task Map<T>(this Task<T> task, Action<T> continuation)
        {
            var res = await task;
            continuation(res);
        }

        public static async Task<T> Map<T>(this Task task, Func<T> continuation)
        {
            await task;
            return continuation();
        }

        public static async Task Map(this Task task, Action continuation)
        {
            await task;
            continuation();
        }
    }
}
