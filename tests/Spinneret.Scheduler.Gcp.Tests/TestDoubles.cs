using NodaTime;
using Spinneret.Mediator;
using Spinneret.Queue;

namespace Spinneret.Scheduler.Gcp.Tests;

// ---------------------------------------------------------------------------------------------
// Request types scanned by QueueTypeRegistry from this assembly, plus hand-rolled fakes for the
// queue and scheduler abstractions. No mocking library is used.
// ---------------------------------------------------------------------------------------------

public sealed record TestRequest(string Name) : IRequest<Unit>;

public sealed record OtherTestRequest(int Number) : IRequest<Unit>;

public sealed class FakePayloadSerializer : IQueuePayloadSerializer
{
    public string SerializeResult { get; set; } = "{}";
    public object? DeserializeResult { get; set; }
    public List<(object Request, Type RequestType)> SerializeCalls { get; } = [];
    public List<(string Json, Type RequestType)> DeserializeCalls { get; } = [];

    public string Serialize(object request, Type requestType)
    {
        SerializeCalls.Add((request, requestType));
        return SerializeResult;
    }

    public object? Deserialize(string json, Type requestType)
    {
        DeserializeCalls.Add((json, requestType));
        return DeserializeResult;
    }
}

public sealed class RecordingRecurringJobScheduler : IRecurringJobScheduler
{
    public List<(string Key, IRequest<Unit> Request, Duration Interval, CancellationToken Ct)> Registrations { get; } = [];
    public HashSet<string> FailingKeys { get; } = [];

    public Task RegisterAsync(string key, IRequest<Unit> request, Duration interval, CancellationToken ct = default)
    {
        if (FailingKeys.Contains(key))
            throw new InvalidOperationException($"Registration failed for '{key}'.");

        Registrations.Add((key, request, interval, ct));
        return Task.CompletedTask;
    }
}

public sealed class FakeRecurringJob(string key, Duration interval, IRequest<Unit> request) : IRecurringJob
{
    public string Key => key;
    public Duration Interval => interval;
    public IRequest<Unit> CreateRequest() => request;
}

public sealed class ThrowingCreateRequestJob(string key) : IRecurringJob
{
    public string Key => key;
    public Duration Interval => Duration.FromMinutes(1);
    public IRequest<Unit> CreateRequest() => throw new InvalidOperationException($"CreateRequest failed for '{key}'.");
}

public sealed class FakeQueue : IQueue
{
    public List<object> Enqueued { get; } = [];

    public Task Enqueue<TResponse>(IRequest<TResponse> request, QueueOptions? options = null, CancellationToken ct = default)
    {
        Enqueued.Add(request);
        return Task.CompletedTask;
    }

    public Task Enqueue(IRequest<Unit> request, QueueOptions? options = null, CancellationToken ct = default)
    {
        Enqueued.Add(request);
        return Task.CompletedTask;
    }
}

public sealed class FakeDeadLetterWriter : IDeadLetterWriter
{
    public List<DeadLetterEntry> Entries { get; } = [];

    public Task WriteAsync(DeadLetterEntry entry, CancellationToken ct = default)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }
}
