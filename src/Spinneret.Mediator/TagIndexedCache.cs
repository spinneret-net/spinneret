using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

namespace Spinneret.Mediator;

internal interface ITagIndexedCache
{
    Task<TValue> GetOrCreate<TValue>(
        object key,
        Func<Task<TValue>> factory,
        TimeSpan duration,
        IReadOnlyList<Enum> tags);
    void RemoveByTag(Enum tag);
    void Clear();
}

internal sealed class TagIndexedCache(IMemoryCache inner) : ITagIndexedCache
{
    private readonly ConcurrentDictionary<Enum, ConcurrentDictionary<object, byte>> _tagIndex = new();
    private readonly object _writeLock = new();
    private CancellationTokenSource _clearSignal = new();

    public Task<TValue> GetOrCreate<TValue>(
        object key,
        Func<Task<TValue>> factory,
        TimeSpan duration,
        IReadOnlyList<Enum> tags)
    {
        if (TryGetUsable(key, out Task<TValue>? cached))
            return cached;

        lock (_writeLock)
        {
            if (TryGetUsable(key, out cached))
                return cached;

            Task<TValue> task;
            try
            {
                task = factory();
            }
            catch (Exception ex)
            {
                return Task.FromException<TValue>(ex);
            }

            Store(key, task, duration, tags);
            ScheduleFailureCleanup(key, task);
            return task;
        }
    }

    public void RemoveByTag(Enum tag)
    {
        lock (_writeLock)
        {
            if (!_tagIndex.TryRemove(tag, out var keys)) return;
            foreach (var key in keys.Keys)
                inner.Remove(key);
        }
    }

    public void Clear()
    {
        var old = Interlocked.Exchange(ref _clearSignal, new CancellationTokenSource());
        old.Cancel();
        old.Dispose();
        _tagIndex.Clear();
    }

    private bool TryGetUsable<TValue>(object key, [NotNullWhen(true)] out Task<TValue>? task)
    {
        if (inner.TryGetValue(key, out task) && task is not null && (!task.IsCompleted || task.IsCompletedSuccessfully))
            return true;
        task = null;
        return false;
    }

    private void Store<TValue>(object key, Task<TValue> task, TimeSpan duration, IReadOnlyList<Enum> tags)
    {
        var capturedTags = tags;
        var options = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = duration };
        options.AddExpirationToken(new CancellationChangeToken(_clearSignal.Token));
        options.RegisterPostEvictionCallback((evictedKey, _, _, _) =>
        {
            lock (_writeLock)
            {
                if (inner.TryGetValue(evictedKey, out _))
                {
                    // Key has been added again - do nothing.
                    return;
                }

                foreach (var tag in capturedTags)
                    if (_tagIndex.TryGetValue(tag, out var k))
                        k.TryRemove(evictedKey, out _);
            }
        });

        inner.Set(key, task, options);

        foreach (var tag in tags)
        {
            var keys = _tagIndex.GetOrAdd(tag, _ => new ConcurrentDictionary<object, byte>());
            keys.TryAdd(key, 0);
        }
    }

    private void ScheduleFailureCleanup<TValue>(object key, Task<TValue> task)
    {
        task.ContinueWith(
            static (completed, state) =>
            {
                if (completed.IsCompletedSuccessfully) return;
                var (cache, k) = ((TagIndexedCache, object))state!;
                cache.RemoveIfStillCurrent(k, completed);
            },
            state: (this, key),
            cancellationToken: CancellationToken.None,
            continuationOptions: TaskContinuationOptions.None,
            scheduler: TaskScheduler.Default);
    }

    private void RemoveIfStillCurrent(object key, object owningTask)
    {
        lock (_writeLock)
        {
            if (inner.TryGetValue(key, out var current) && ReferenceEquals(current, owningTask))
                inner.Remove(key);
        }
    }
}
