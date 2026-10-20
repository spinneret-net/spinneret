using System.Collections.Concurrent;
using System.Text.Json;
using Google.Api.Gax;
using Google.Cloud.Firestore;
using Microsoft.Extensions.DependencyInjection;
using Spinneret.Functional;
using Spinneret.Mediator;
using Spinneret.Queue;
using Testcontainers.Firestore;
using TUnit.Core.Interfaces;

namespace Spinneret.Queue.Firestore.Tests;

public sealed record PingCommand(string Name) : IRequest<Unit>;

public sealed record ReportCommand(string Name) : IRequest<string>;

public sealed class DeadLetterTestHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    public string Collection { get; }
    public FirestoreDb Db { get; }
    public RecordingQueue Queue { get; }
    public FixedTimeProvider Clock { get; }

    private DeadLetterTestHost(ServiceProvider provider, FirestoreDb db, string collection)
    {
        _provider = provider;
        Db = db;
        Collection = collection;
        Queue = provider.GetRequiredService<RecordingQueue>();
        Clock = (FixedTimeProvider)provider.GetRequiredService<TimeProvider>();
    }

    public IServiceProvider Services => _provider;
    public IDeadLetterWriter Writer => _provider.GetRequiredService<IDeadLetterWriter>();
    public IDeadLetterStore Store => _provider.GetRequiredService<IDeadLetterStore>();
    public IDeadLetterResender Resender => _provider.GetRequiredService<IDeadLetterResender>();

    public static DeadLetterTestHost Start(FirestoreEmulatorFixture fixture, DateTimeOffset? now = null)
    {
        var collection = $"dl_{Guid.NewGuid():N}";
        var db = fixture.CreateDb();

        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(new FixedTimeProvider(now ?? new DateTimeOffset(2026, 8, 16, 9, 0, 0, TimeSpan.Zero)));
        services.AddSingleton<TimeProvider>(sp => sp.GetRequiredService<FixedTimeProvider>());
        services.AddSingleton<RecordingQueue>();
        services.AddSingleton<IQueue>(sp => sp.GetRequiredService<RecordingQueue>());
        services.AddSingleton<IQueuePayloadSerializer, JsonPayloadSerializer>();
        services.AddQueueCore([typeof(DeadLetterTestHost).Assembly]);
        services.AddDeadLetters(q => q.UseFirestoreDeadLetters(o => o.Collection = collection));

        return new DeadLetterTestHost(services.BuildServiceProvider(), db, collection);
    }

    public CollectionReference Documents => Db.Collection(Collection);

    public DocumentReference Document(string id) => Documents.Document(id);

    public async Task<IReadOnlyDictionary<string, object>?> RawFields(string id)
    {
        var snapshot = await Document(id).GetSnapshotAsync();
        return snapshot.Exists ? snapshot.ToDictionary() : null;
    }

    public async Task<int> DocumentCount() => (await Documents.GetSnapshotAsync()).Count;

    // Seeds an exact instant, which the writer's own clock can't produce for paging ties.
    public Task Seed(
        string key,
        DateTimeOffset deadLetteredAt,
        string? commandTypeName = null,
        string payloadJson = """{"name":"seeded"}""",
        DeadLetterSource source = DeadLetterSource.Queue,
        bool withDescription = true) =>
        Document(key).CreateAsync(new Dictionary<string, object?>
        {
            // Not via the writer's helper: this is a data contract, so the test states it independently.
            [DeadLetterDocument.Fields.Source] = source.ToString(),
            [DeadLetterDocument.Fields.CommandTypeName] = commandTypeName ?? typeof(PingCommand).FullName!,
            [DeadLetterDocument.Fields.Description] = withDescription ? $"seeded {key}" : null,
            [DeadLetterDocument.Fields.PayloadJson] = payloadJson,
            [DeadLetterDocument.Fields.Error] = "boom",
            [DeadLetterDocument.Fields.Attempts] = 3,
            [DeadLetterDocument.Fields.DeadLetteredAt] = Timestamp.FromDateTimeOffset(deadLetteredAt),
        });

    public async Task<List<string>> PageThrough(int pageSize)
    {
        var keys = new List<string>();
        string? cursor = null;
        var pages = 0;

        do
        {
            var page = await Store.ListAsync(new DeadLetterQuery { PageSize = pageSize, Cursor = cursor });
            keys.AddRange(page.Items.Select(i => i.IdempotencyKey));
            cursor = page.NextCursor;

            if (++pages > 100)
                throw new InvalidOperationException("Paging did not terminate.");
        }
        while (cursor is not null);

        return keys;
    }

    public async ValueTask DisposeAsync() => await _provider.DisposeAsync();
}

internal static class TestRegistration
{
    public static IServiceCollection AddDeadLetters(this IServiceCollection services, Func<QueueBuilder, QueueBuilder> use)
    {
        use(new QueueBuilder(services)).DeadLetters!.Register(services);
        return services;
    }
}
