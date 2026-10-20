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

// ---------------------------------------------------------------------------------------------
// Request types scanned by QueueTypeRegistry from this assembly, plus hand-rolled fakes for the
// queue and the serializer. No mocking library is used.
// ---------------------------------------------------------------------------------------------

public sealed record PingCommand(string Name) : IRequest<Unit>;

public sealed record ReportCommand(string Name) : IRequest<string>;

// ---------------------------------------------------------------------------------------------
// Docker fixture and host harness.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// A DI container with the Firestore dead-letter writer and store registered against a collection
/// unique to one test, plus helpers that read the collection raw so what actually landed in
/// Firestore can be asserted rather than what the store says landed.
/// </summary>
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

    // ------------------------------------------------------------------- Firestore helpers ---

    public CollectionReference Documents => Db.Collection(Collection);

    public DocumentReference Document(string id) => Documents.Document(id);

    /// <summary>The raw stored fields, so the document shape can be asserted independently of the reader.</summary>
    public async Task<IReadOnlyDictionary<string, object>?> RawFields(string id)
    {
        var snapshot = await Document(id).GetSnapshotAsync();
        return snapshot.Exists ? snapshot.ToDictionary() : null;
    }

    public async Task<int> DocumentCount() => (await Documents.GetSnapshotAsync()).Count;

    /// <summary>
    /// Writes a dead-letter document with an exact instant. The writer's own clock cannot produce
    /// the ties and orderings the paging tests need, so those are seeded straight into Firestore.
    /// </summary>
    public Task Seed(
        string key,
        DateTimeOffset deadLetteredAt,
        string? commandTypeName = null,
        string payloadJson = """{"name":"seeded"}""",
        DeadLetterSource source = DeadLetterSource.Queue,
        bool withDescription = true) =>
        Document(key).CreateAsync(new Dictionary<string, object?>
        {
            // Spelled the way the library persists it. DeadLetterStorage.FormatSource is internal to
            // Spinneret.Queue and not visible here, and stating the spelling literally is the point:
            // it is a data contract, so a test that derived it from the same helper the writer uses
            // could not catch the writer changing it.
            [DeadLetterDocument.Fields.Source] = source.ToString(),
            [DeadLetterDocument.Fields.CommandTypeName] = commandTypeName ?? typeof(PingCommand).FullName!,
            [DeadLetterDocument.Fields.Description] = withDescription ? $"seeded {key}" : null,
            [DeadLetterDocument.Fields.PayloadJson] = payloadJson,
            [DeadLetterDocument.Fields.Error] = "boom",
            [DeadLetterDocument.Fields.Attempts] = 3,
            [DeadLetterDocument.Fields.DeadLetteredAt] = Timestamp.FromDateTimeOffset(deadLetteredAt),
        });

    /// <summary>Walks every page, returning the keys in order and guarding against a runaway loop.</summary>
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
    /// <summary>
    /// Registers only the dead-letter role a builder call chose, as <c>AddQueue</c> would — so the
    /// store's own registrations are assertable without composing a whole queue around them.
    /// </summary>
    public static IServiceCollection AddDeadLetters(this IServiceCollection services, Func<QueueBuilder, QueueBuilder> use)
    {
        use(new QueueBuilder(services)).DeadLetters!.Register(services);
        return services;
    }
}
