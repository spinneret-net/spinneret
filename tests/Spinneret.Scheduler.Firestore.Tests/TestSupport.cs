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

// ---------------------------------------------------------------------------------------------
// Docker fixture and host harness.
// ---------------------------------------------------------------------------------------------

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
        services.AddScheduler(s =>
        {
            s.UseFirestore(o => o.Collection = collection);
            if (sweeper)
                // Fast ticks so integration tests do not wait out the 15s default.
                s.UseSweeper(o => o.SweepInterval = TimeSpan.FromMilliseconds(100));
        });
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

    /// <summary>
    /// When the document was last written. Firestore only moves this on a commit that touches the
    /// document, so it is how a test tells "wrote identical values" apart from "wrote nothing".
    /// </summary>
    public async Task<Timestamp?> JobUpdateTime(string key) => (await Job(key).GetSnapshotAsync()).UpdateTime;

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
