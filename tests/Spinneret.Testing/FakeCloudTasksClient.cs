using System.Collections.Concurrent;
using Google.Api.Gax.Grpc;
using Google.Cloud.Tasks.V2;
using Grpc.Core;
using CloudTask = Google.Cloud.Tasks.V2.Task;
using Task = System.Threading.Tasks.Task;

namespace Spinneret.Testing;

/// <summary>
/// Hand-rolled <see cref="CloudTasksClient"/> capturing task creation. All convenience overloads of
/// CreateTaskAsync funnel into the (CreateTaskRequest, CallSettings) overload, so overriding it
/// captures every enqueue the library performs.
/// </summary>
/// <remarks>
/// Behaves like Cloud Tasks where the library depends on it: a named task is remembered, and creating
/// another with the same name answers <see cref="StatusCode.AlreadyExists"/> — the deduplication the
/// relay's idempotent forwarding rests on.
/// </remarks>
public sealed class FakeCloudTasksClient : CloudTasksClient
{
    private readonly ConcurrentQueue<CreateTaskRequest> _requests = new();
    private readonly ConcurrentDictionary<string, byte> _names = new(StringComparer.Ordinal);
    private int _failNext;
    private int _loseNextResponse;

    /// <summary>Every task created, in order. Duplicates refused as already existing are not included.</summary>
    public List<CreateTaskRequest> Requests => [.. _requests];

    /// <summary>Queue resources created through <c>CreateQueueAsync</c>, in call order.</summary>
    public List<CreateQueueRequest> CreatedQueues { get; } = [];

    /// <summary>Every <c>CreateQueueAsync</c> call, including the ones made to fail.</summary>
    public int CreateQueueAttempts { get; private set; }

    /// <summary>
    /// Leading <c>CreateQueueAsync</c> calls that fail as an emulator that is not listening yet
    /// does, after which creation succeeds — an emulator container starting alongside the host.
    /// </summary>
    public int TransientCreateQueueFailures { get; set; }

    /// <summary>Thrown by every <c>CreateTaskAsync</c> while set.</summary>
    public Exception? ThrowOnCreateTask { get; set; }

    public Exception? ThrowOnCreateQueue { get; set; }

    /// <summary>Makes the next <paramref name="count"/> task creations fail as an outage would, creating nothing.</summary>
    public void FailNextCreateTasks(int count) => Interlocked.Exchange(ref _failNext, count);

    /// <summary>
    /// Makes the next <paramref name="count"/> task creations succeed but answer with an error, as
    /// when the response is lost on the way back — the caller cannot tell the task now exists.
    /// </summary>
    public void LoseNextCreateTaskResponses(int count) => Interlocked.Exchange(ref _loseNextResponse, count);

    public CreateTaskRequest SingleRequest => Requests.Single();

    public override Task<CloudTask> CreateTaskAsync(CreateTaskRequest request, CallSettings? callSettings = null)
    {
        if (ThrowOnCreateTask is not null)
            throw ThrowOnCreateTask;

        if (Interlocked.Decrement(ref _failNext) >= 0)
            throw new RpcException(new Status(StatusCode.Unavailable, "Cloud Tasks is unavailable"));

        if (!string.IsNullOrEmpty(request.Task.Name) && !_names.TryAdd(request.Task.Name, 0))
            throw new RpcException(new Status(StatusCode.AlreadyExists, $"Task {request.Task.Name} already exists"));

        _requests.Enqueue(request);

        if (Interlocked.Decrement(ref _loseNextResponse) >= 0)
            throw new RpcException(new Status(StatusCode.DeadlineExceeded, "The response was lost"));

        return Task.FromResult(request.Task);
    }

    public override Task<Google.Cloud.Tasks.V2.Queue> CreateQueueAsync(
        CreateQueueRequest request, CallSettings? callSettings = null)
    {
        CreateQueueAttempts++;

        if (ThrowOnCreateQueue is not null)
            throw ThrowOnCreateQueue;

        if (TransientCreateQueueFailures > 0)
        {
            TransientCreateQueueFailures--;
            throw new RpcException(new Status(StatusCode.Unavailable, "the emulator is not up yet"));
        }

        CreatedQueues.Add(request);
        return Task.FromResult(request.Queue);
    }
}
