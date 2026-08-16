using System.Collections.Concurrent;
using System.Text.Json;
using Google.Api.Gax;
using Google.Cloud.Firestore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spinneret.Mediator;
using Spinneret.Queue;
using Testcontainers.Firestore;
using TUnit.Core.Interfaces;

namespace Spinneret.Scheduler.Firestore.Tests;

// ---------------------------------------------------------------------------------------------
// Recording doubles for the queue and the dead-letter writer, so what the sweep did is assertable
// without standing up a transport. No mocking library is used.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// A request whose handler would return a value. A scheduled run discards it — the response type is
/// here to prove a job is not restricted to <c>IRequest&lt;Unit&gt;</c>, and that the sweep can
/// enqueue a stored job whose response type it only learns at runtime, by reflection.
/// </summary>
public sealed record ReportRequest(string Name) : IRequest<string>;

/// <summary>What the sweep handed to the queue. The scheduler's job ends at the enqueue.</summary>
public sealed class RecordingQueue : IQueue
{
    private readonly ConcurrentQueue<object> _enqueued = new();

    /// <summary>Set to make every enqueue fail, for the dead-letter compensation paths.</summary>
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
/// Records dead letters in memory. <see cref="FailWith"/> covers the branch where the dispatcher
/// must keep a job document because the write that would have preserved its payload did not land.
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
/// A real JSON serializer rather than a stub: the sweep resolves a stored type name and
/// deserializes the stored payload back into a request, and only a real round trip proves the
/// document the scheduler wrote is one the dispatcher can still read.
/// </summary>
public sealed class JsonPayloadSerializer : IQueuePayloadSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public string Serialize(object request, Type requestType) => JsonSerializer.Serialize(request, requestType, Options);

    public object? Deserialize(string json, Type requestType) => JsonSerializer.Deserialize(json, requestType, Options);
}

// ---------------------------------------------------------------------------------------------
// Docker fixture and host harness.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// One Firestore emulator for the whole test session; each test isolates itself with a unique
/// collection name, mirroring how the MSSQL suites isolate with unique table names.
/// </summary>
/// <remarks>
/// The emulator is reached through <c>FIRESTORE_EMULATOR_HOST</c> and
/// <see cref="EmulatorDetection.EmulatorOnly"/> rather than an explicit endpoint, because that is
/// the wiring <c>docs/scheduler-firestore.md</c> tells hosts to use — so the fixture exercises the
/// documented path. The environment variable is process-global, which is safe here: it is set once,
/// before any <see cref="FirestoreDb"/> in this assembly is built.
/// </remarks>
public sealed class FirestoreEmulatorFixture : IAsyncInitializer, IAsyncDisposable
{
    /// <summary>
    /// Pinned rather than floating, the same way the MSSQL fixture pins its server image: an
    /// emulator that changes underneath the suite turns a red build into a mystery.
    /// </summary>
    private const string Image = "gcr.io/google.com/cloudsdktool/google-cloud-cli:580.0.0-emulators";

    public const string ProjectId = "spinneret-tests";

    private readonly FirestoreContainer _container = new FirestoreBuilder(Image).Build();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // GetEmulatorEndpoint() renders "http://host:port/"; the variable wants a bare host:port.
        var endpoint = new Uri(_container.GetEmulatorEndpoint());
        Environment.SetEnvironmentVariable("FIRESTORE_EMULATOR_HOST", $"{endpoint.Host}:{endpoint.Port}");
    }

    public FirestoreDb CreateDb() =>
        new FirestoreDbBuilder { ProjectId = ProjectId, EmulatorDetection = EmulatorDetection.EmulatorOnly }.Build();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

/// <summary>
/// A running scheduler host: DI container with the Firestore scheduler registered against a
/// collection unique to one test, hosted services started, plus helpers that read the job
/// collection raw so what actually landed in Firestore can be asserted.
/// </summary>
public sealed class SchedulerTestHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IHostedService[] _hostedServices;

    public string Collection { get; }
    public FirestoreDb Db { get; }
    public RecordingQueue Queue { get; }
    public RecordingDeadLetterWriter DeadLetters { get; }

    private SchedulerTestHost(
        ServiceProvider provider, IHostedService[] hostedServices, FirestoreDb db, string collection)
    {
        _provider = provider;
        _hostedServices = hostedServices;
        Db = db;
        Collection = collection;
        Queue = provider.GetRequiredService<RecordingQueue>();
        DeadLetters = provider.GetRequiredService<RecordingDeadLetterWriter>();
    }

    public IServiceProvider Services => _provider;
    public IRecurringJobScheduler Scheduler => _provider.GetRequiredService<IRecurringJobScheduler>();
    public IFirestoreTransactionalScheduler TransactionalScheduler =>
        _provider.GetRequiredService<IFirestoreTransactionalScheduler>();
    public ISchedulerSweep Sweep => _provider.GetRequiredService<ISchedulerSweep>();

    /// <summary>
    /// Starts a host. <paramref name="reuseCollection"/> puts a second host on the first host's
    /// jobs, which is how the competing-sweep and parallel-installer cases are built.
    /// </summary>
    public static async Task<SchedulerTestHost> StartAsync(
        FirestoreEmulatorFixture fixture,
        bool sweeper = false,
        Action<IServiceCollection>? configure = null,
        string? reuseCollection = null)
    {
        var collection = reuseCollection ?? $"jobs_{Guid.NewGuid():N}";
        var db = fixture.CreateDb();

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(db);
        services.AddSingleton<RecordingQueue>();
        services.AddSingleton<IQueue>(sp => sp.GetRequiredService<RecordingQueue>());
        services.AddSingleton<RecordingDeadLetterWriter>();
        services.AddSingleton<IDeadLetterWriter>(sp => sp.GetRequiredService<RecordingDeadLetterWriter>());
        services.AddSingleton<IQueuePayloadSerializer, JsonPayloadSerializer>();
        services.AddQueueCore([typeof(SchedulerTestHost).Assembly]);
        services.AddFirestoreScheduler(o => o.Collection = collection);
        if (sweeper)
            // Fast ticks so integration tests do not wait out the 15s default.
            services.AddSchedulerSweeper(o => o.SweepInterval = TimeSpan.FromMilliseconds(100));
        configure?.Invoke(services);

        var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToArray();
        foreach (var hostedService in hostedServices)
            await hostedService.StartAsync(CancellationToken.None);

        return new SchedulerTestHost(provider, hostedServices, db, collection);
    }

    // ------------------------------------------------------------------- Firestore helpers ---

    public CollectionReference Jobs => Db.Collection(Collection);

    public DocumentReference Job(string key) => Jobs.Document(key);

    /// <summary>
    /// Whether the job is still outstanding work. There is no status field — a document is deleted
    /// once it has run, been cancelled, or had its failure dead-lettered — so existence is the state.
    /// </summary>
    public async Task<bool> JobExists(string key) => (await Job(key).GetSnapshotAsync()).Exists;

    public async Task<int> JobCount() => (await Jobs.GetSnapshotAsync()).Count;

    public async Task<DateTimeOffset> JobNextExecuteAt(string key) =>
        (await Job(key).GetSnapshotAsync())
        .GetValue<Timestamp>(ScheduledJob.Fields.NextExecuteAt).ToDateTimeOffset();

    public async Task<T> JobField<T>(string key, string field) =>
        (await Job(key).GetSnapshotAsync()).GetValue<T>(field);

    /// <summary>Makes the job due now, so a sweep picks it up without waiting out its schedule.</summary>
    public Task MakeDue(string key, TimeSpan? ago = null) =>
        Job(key).UpdateAsync(
            ScheduledJob.Fields.NextExecuteAt,
            Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow - (ago ?? TimeSpan.Zero)));

    public Task SetField(string key, string field, object value) => Job(key).UpdateAsync(field, value);

    public async ValueTask DisposeAsync()
    {
        foreach (var hostedService in _hostedServices.Reverse())
            await hostedService.StopAsync(CancellationToken.None);
        await _provider.DisposeAsync();
    }
}

internal static class Wait
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
