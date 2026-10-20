using System.Collections.Concurrent;
using System.Text.Json;
using Spinneret.Mediator;
using Spinneret.Queue;

namespace Spinneret.Testing;

/// <summary>Thread-safe record of handler activity, shared through DI as a singleton.</summary>
public sealed class DeliveryLog
{
    private readonly ConcurrentDictionary<string, int> _attempts = new();
    private readonly ConcurrentQueue<string> _deliveries = new();

    public int RecordAttempt(string key) => _attempts.AddOrUpdate(key, 1, (_, n) => n + 1);
    public void RecordDelivery(string tag) => _deliveries.Enqueue(tag);

    public int Attempts(string key) => _attempts.GetValueOrDefault(key);
    public IReadOnlyCollection<string> Deliveries => [.. _deliveries];
    public int DeliveryCount(string tag) => _deliveries.Count(d => d == tag);
}

/// <summary>What was handed to the queue, so an enqueue can be asserted without a transport.</summary>
public sealed class RecordingQueue : IQueue
{
    private readonly ConcurrentQueue<object> _enqueued = new();

    /// <summary>Set to make every enqueue fail, for the compensation and "entry left in place" paths.</summary>
    public Exception? FailWith { get; set; }

    public IReadOnlyCollection<object> Enqueued => [.. _enqueued];
    public int CountOf<T>(Func<T, bool> predicate) => _enqueued.OfType<T>().Count(predicate);

    public Task Enqueue<TResponse>(IRequest<TResponse> request, QueueOptions? options = null, CancellationToken ct = default)
    {
        if (FailWith is { } failure)
            return Task.FromException(failure);

        _enqueued.Enqueue(request);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Records dead letters in memory. <see cref="FailWith"/> covers the branches where a caller must
/// keep its own copy because the write that would have preserved the payload did not land.
/// </summary>
public sealed class RecordingDeadLetterWriter : IDeadLetterWriter
{
    private readonly ConcurrentQueue<DeadLetterEntry> _entries = new();

    public Exception? FailWith { get; set; }

    public IReadOnlyCollection<DeadLetterEntry> Entries => [.. _entries];

    public Task WriteAsync(DeadLetterEntry entry, CancellationToken ct = default)
    {
        if (FailWith is { } failure)
            return Task.FromException(failure);

        _entries.Enqueue(entry);
        return Task.CompletedTask;
    }
}

/// <summary>
/// A real JSON serializer rather than a stub: stored payloads are read back into requests, and only
/// a real round trip proves what was written is still deserializable.
/// </summary>
public sealed class JsonPayloadSerializer : IQueuePayloadSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public string Serialize(object request, Type requestType) => JsonSerializer.Serialize(request, requestType, Options);

    public object? Deserialize(string json, Type requestType) => JsonSerializer.Deserialize(json, requestType, Options);
}

/// <summary>A clock frozen at a known instant, so stored timestamps are assertable. Settable to advance it.</summary>
public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

public static class Wait
{
    /// <summary>Polls until <paramref name="condition"/> holds; fails the test on timeout.</summary>
    public static async Task Until(Func<Task<bool>> condition, string because, int timeoutSeconds = 20)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return;
            await Task.Delay(25);
        }

        throw new TimeoutException($"Timed out waiting for: {because}");
    }

    public static Task Until(Func<bool> condition, string because, int timeoutSeconds = 20) =>
        Until(() => Task.FromResult(condition()), because, timeoutSeconds);
}
