using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spinneret.Functional;
using Spinneret.Mediator;

namespace Spinneret.Queue.Tests;

// ---------------------------------------------------------------------------------------------
// Test command types scanned by QueueTypeRegistry from this assembly. All policies must be
// valid: an invalid [QueuePolicy] anywhere in the assembly fails registry construction for
// every test. Invalid-policy scenarios use DynamicRequestAssembly instead.
// ---------------------------------------------------------------------------------------------

public sealed class UnannotatedCommand : IRequest<Unit>;

[QueuePolicy(
    Channel = "test-channel",
    MaxAttempts = 2,
    MaxAge = "01:00:00",
    MinBackoff = "00:00:05",
    MaxBackoff = "00:01:00",
    OnErrorResult = ErrorResultAction.Discard)]
public sealed class AnnotatedCommand : IRequest<Unit>;

[QueuePolicy(OnErrorResult = ErrorResultAction.Retry)]
public sealed class RetryOnErrorResultCommand : IRequest<Unit>;

[QueuePolicy(MaxAttempts = 2, MaxAge = "01:00:00", OnExhausted = ExhaustedAction.Discard)]
public sealed class DiscardOnExhaustionCommand : IRequest<Unit>;

[QueuePolicy(Channel = "bulk")]
public sealed class SecondChannelCommand : IRequest<Unit>;

public sealed class SingleResultCommand : IRequest<Result<string>>;

public sealed class OkErrorResultCommand : IRequest<Result<int, string>>;

public sealed class NestedResultCommand : IRequest<Result<Result<string>, string>>;

public sealed class PlainResponseCommand : IRequest<string>;

public abstract class AbstractCommand : IRequest<Unit>;

// ---------------------------------------------------------------------------------------------
// Hand-rolled fakes.
// ---------------------------------------------------------------------------------------------

internal sealed class FakeDispatcher : IQueueDispatcher
{
    public Exception? Throw { get; set; }
    public int Calls { get; private set; }

    /// <summary>The ambient activity as the handler saw it — the trace the message is processed in.</summary>
    public ActivityContext ObservedContext { get; private set; }

    /// <summary>The parent of the span the handler ran under — who the consumer attached itself to.</summary>
    public ActivitySpanId ObservedParentSpanId { get; private set; }

    public Task Dispatch(QueueEnvelope envelope, CancellationToken ct)
    {
        Calls++;
        ObservedContext = Activity.Current?.Context ?? default;
        ObservedParentSpanId = Activity.Current?.ParentSpanId ?? default;
        return Throw is null ? Task.CompletedTask : Task.FromException(Throw);
    }
}

internal sealed class FakeEnvelopeQueue : IEnvelopeQueue
{
    public Exception? Throw { get; set; }
    public List<(QueueEnvelope Envelope, TimeSpan? Delay)> Enqueued { get; } = [];

    public Task Enqueue(QueueEnvelope envelope, TimeSpan? delay = null, CancellationToken ct = default)
    {
        if (Throw is not null)
            return Task.FromException(Throw);

        Enqueued.Add((envelope, delay));
        return Task.CompletedTask;
    }
}

internal sealed class FakeDeadLetterWriter : IDeadLetterWriter
{
    public Exception? Throw { get; set; }
    public List<DeadLetterEntry> Entries { get; } = [];

    public Task WriteAsync(DeadLetterEntry entry, CancellationToken ct = default)
    {
        if (Throw is not null)
            return Task.FromException(Throw);

        Entries.Add(entry);
        return Task.CompletedTask;
    }
}

internal sealed class FakeQueue : IQueue
{
    public Exception? Throw { get; set; }
    public List<object> Enqueued { get; } = [];

    public Task Enqueue<TResponse>(IRequest<TResponse> request, QueueOptions? options = null, CancellationToken ct = default)
    {
        if (Throw is not null)
            return Task.FromException(Throw);

        Enqueued.Add(request);
        return Task.CompletedTask;
    }
}

internal sealed class FakeDeadLetterStore : IDeadLetterStore
{
    private readonly Dictionary<string, DeadLetter> _entries = new(StringComparer.Ordinal);

    /// <summary>Every key passed to <see cref="DeleteAsync"/>, whether or not it existed.</summary>
    public List<string> Deleted { get; } = [];

    public FakeDeadLetterStore Add(DeadLetter deadLetter)
    {
        _entries[deadLetter.IdempotencyKey] = deadLetter;
        return this;
    }

    public Task<DeadLetterPage> ListAsync(DeadLetterQuery query, CancellationToken ct = default) =>
        Task.FromResult(new DeadLetterPage
        {
            Items = _entries.Values.OrderByDescending(e => e.DeadLetteredAt).ToArray(),
        });

    public Task<DeadLetter?> GetAsync(string idempotencyKey, CancellationToken ct = default) =>
        Task.FromResult(_entries.GetValueOrDefault(idempotencyKey));

    public Task<bool> DeleteAsync(string idempotencyKey, CancellationToken ct = default)
    {
        Deleted.Add(idempotencyKey);
        return Task.FromResult(_entries.Remove(idempotencyKey));
    }
}

/// <summary>
/// Records whether the work ran inside the scope, so tests can tell a resend that grouped its
/// enqueue and delete from one that ran them loose.
/// </summary>
internal sealed class RecordingQueueTransactionScope : IQueueTransactionScope
{
    public int Executions { get; private set; }
    public bool IsInside { get; private set; }

    public async Task ExecuteAsync(Func<CancellationToken, Task> work, CancellationToken ct)
    {
        Executions++;
        IsInside = true;
        try
        {
            await work(ct);
        }
        finally
        {
            IsInside = false;
        }
    }
}

internal sealed class FakeMediator : ISpinneretMediator
{
    /// <summary>The response Send returns; must be castable to the request's TResponse.</summary>
    public object? Response { get; set; }
    public object? LastRequest { get; private set; }
    public int Calls { get; private set; }

    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        Calls++;
        LastRequest = request;
        return Task.FromResult(Response is null ? default(TResponse)! : (TResponse)Response);
    }
}

internal sealed class FakeSerializer : IQueuePayloadSerializer
{
    /// <summary>Override deserialization; default creates the request via its parameterless ctor.</summary>
    public Func<string, Type, object?>? OnDeserialize { get; set; }

    public string Serialize(object request, Type requestType) => "{}";

    public object? Deserialize(string json, Type requestType) =>
        OnDeserialize is not null ? OnDeserialize(json, requestType) : Activator.CreateInstance(requestType);
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

// ---------------------------------------------------------------------------------------------
// Helpers.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// Collects finished queue spans. <see cref="TaggedWith"/> filters by a tag the caller made unique
/// to its own test, because an <see cref="ActivityListener"/> is process-global while TUnit runs
/// tests in parallel — without the filter a test would assert on a sibling's spans.
/// </summary>
internal sealed class SpanCollector : IDisposable
{
    private readonly List<Activity> _spans = [];
    private readonly ActivityListener _listener;

    public SpanCollector()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == QueueDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                lock (_spans)
                    _spans.Add(activity);
            },
        };

        ActivitySource.AddActivityListener(_listener);
    }

    public Activity TaggedWith(string tag, string value)
    {
        lock (_spans)
            return Expect.Single(_spans.Where(span => (string?)span.GetTagItem(tag) == value).ToList());
    }

    public void Dispose() => _listener.Dispose();
}

internal static class Expect
{
    public static T Single<T>(IReadOnlyList<T> items)
    {
        if (items.Count != 1)
            throw new InvalidOperationException($"Expected exactly one item but found {items.Count}.");

        return items[0];
    }
}

internal static class TestServices
{
    /// <summary>Registers open-generic null loggers so internal DI-registered services resolve.</summary>
    public static IServiceCollection AddNullLogging(this IServiceCollection services)
    {
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        return services;
    }
}

internal static class ProcessorTestExtensions
{
    /// <summary>Shorthand that wraps envelope + task id into a <see cref="QueueDeliveryContext"/>.</summary>
    public static Task<QueueDeliveryOutcome> ProcessAsync(
        this IQueueDeliveryProcessor processor, QueueEnvelope envelope, string taskId, CancellationToken ct)
        => processor.ProcessAsync(new QueueDeliveryContext { Envelope = envelope, TaskId = taskId }, ct);
}

/// <summary>
/// Builds a throwaway in-memory assembly containing a single command implementing IRequest&lt;Unit&gt;
/// (or the given response types), optionally annotated with [QueuePolicy]. Lets invalid-policy,
/// duplicate-name and dual-interface scenarios exercise QueueTypeRegistry's public constructor
/// without poisoning the test assembly scan.
/// </summary>
internal static class DynamicRequestAssembly
{
    public static Assembly WithRequest(string fullName, params (string Property, object Value)[] policyProperties) =>
        WithRequest(fullName, [typeof(Unit)], policyProperties);

    /// <summary>Same, but implementing IRequest&lt;T&gt; once per type in <paramref name="responseTypes"/>.</summary>
    public static Assembly WithRequest(
        string fullName, Type[] responseTypes, params (string Property, object Value)[] policyProperties)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("Dynamic_" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("Main");

        var type = module.DefineType(fullName, TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class);
        foreach (var responseType in responseTypes)
            type.AddInterfaceImplementation(typeof(IRequest<>).MakeGenericType(responseType));

        if (policyProperties.Length > 0)
        {
            var ctor = typeof(QueuePolicyAttribute).GetConstructor(Type.EmptyTypes)!;
            var properties = policyProperties
                .Select(p => typeof(QueuePolicyAttribute).GetProperty(p.Property)!)
                .ToArray();
            var values = policyProperties.Select(p => p.Value).ToArray();
            type.SetCustomAttribute(new CustomAttributeBuilder(ctor, [], properties, values));
        }

        type.CreateType();
        return assembly;
    }
}
