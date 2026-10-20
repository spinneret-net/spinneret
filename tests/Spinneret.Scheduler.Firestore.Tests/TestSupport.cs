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

/// <summary>Has a non-Unit response, so the sweep must resolve the response type by reflection.</summary>
public sealed record ReportRequest(string Name) : IRequest<string>;

/// <summary>A started scheduler host on a per-test collection, with raw Firestore read helpers.</summary>
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

    /// <summary><paramref name="reuseCollection"/> shares the first host's jobs, for competing hosts.</summary>
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
            s.UseFirestoreStorage(o => o.Collection = collection);
            if (sweeper)
                // Fast ticks so integration tests do not wait out the 15s default.
                s.UseInHostSweeper(o => o.SweepInterval = TimeSpan.FromMilliseconds(100));
        });
        configure?.Invoke(services);

        var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToArray();
        foreach (var hostedService in hostedServices)
            await hostedService.StartAsync(CancellationToken.None);

        return new SchedulerTestHost(provider, hostedServices, db, collection);
    }

    public CollectionReference Jobs => Db.Collection(Collection);

    public DocumentReference Job(string key) => Jobs.Document(key);

    /// <summary>Documents are deleted when done, so existence is the state.</summary>
    public async Task<bool> JobExists(string key) => (await Job(key).GetSnapshotAsync()).Exists;

    public async Task<int> JobCount() => (await Jobs.GetSnapshotAsync()).Count;

    public async Task<DateTimeOffset> JobNextExecuteAt(string key) =>
        (await Job(key).GetSnapshotAsync())
        .GetValue<Timestamp>(ScheduledJob.Fields.NextExecuteAt).ToDateTimeOffset();

    public async Task<T> JobField<T>(string key, string field) =>
        (await Job(key).GetSnapshotAsync()).GetValue<T>(field);

    /// <summary>Moves only on a real write, telling identical-value writes from no write.</summary>
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
