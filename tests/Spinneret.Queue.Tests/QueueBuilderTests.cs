using Microsoft.Extensions.DependencyInjection;

namespace Spinneret.Queue.Tests;

/// <summary>
/// The rules <c>AddQueue</c> holds a composition to, with stand-in roles so the rules are tested
/// rather than any one provider.
/// </summary>
public class QueueBuilderTests
{
    private static QueueStorageRole Storage(string name = "UseTestStorage", bool claimable = true, bool relay = false) =>
        new(name, (services, _) => services.AddSingleton<IQueue, FakeQueue>().AddSingleton<IEnvelopeQueue, FakeEnvelopeQueue>())
        {
            Claimable = claimable,
            RegisterRelay = relay ? (_, _) => { } : null,
        };

    private static QueueDeliveryRole Transport(string name = "UseTestTransport") =>
        new(name, (services, _) => services.AddSingleton<IQueueTransport, NullTransport>()) { ProvidesTransport = true };

    private static QueueDeliveryRole Worker(string name = "UseTestWorker") =>
        new(name, (_, _) => { }) { RequiresClaimableStorage = true };

    private static IServiceCollection Compose(Action<QueueBuilder> configure, IServiceCollection? services = null)
    {
        services ??= new ServiceCollection();
        services.AddNullLogging();
        return services.AddQueue(q => configure(q.AddRequestAssemblies(typeof(QueueBuilderTests).Assembly)));
    }

    [Test]
    public async Task Without_request_assemblies_fails()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddQueue(q => q.UseDelivery(Transport())));

        await Assert.That(ex.Message).Contains("AddRequestAssemblies");
    }

    [Test]
    public async Task With_neither_storage_nor_transport_fails()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Compose(_ => { }));

        await Assert.That(ex.Message).Contains("nowhere to put messages");
    }

    [Test]
    public async Task Two_storages_fail_naming_both()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Compose(q => q.UseStorage(Storage("UseFirst")).UseStorage(Storage("UseSecond"))));

        await Assert.That(ex.Message).Contains("UseFirst");
        await Assert.That(ex.Message).Contains("UseSecond");
    }

    [Test]
    public async Task Two_deliveries_fail_naming_both()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Compose(q => q.UseDelivery(Transport("UseFirst")).UseDelivery(Worker("UseSecond"))));

        await Assert.That(ex.Message).Contains("UseFirst");
        await Assert.That(ex.Message).Contains("UseSecond");
    }

    [Test]
    public async Task A_delivery_that_drains_storage_needs_claimable_storage()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Compose(q => q.UseStorage(Storage(claimable: false)).UseDelivery(Worker())));

        await Assert.That(ex.Message).Contains("UseTestWorker");
    }

    [Test]
    public async Task Storage_feeding_a_transport_needs_a_relay()
    {
        // Otherwise enqueues would land in the storage and nothing would ever forward them.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Compose(q => q.UseStorage(Storage(relay: false)).UseDelivery(Transport())));

        await Assert.That(ex.Message).Contains("relay");
    }

    [Test]
    public async Task Storage_with_a_relay_composes_with_a_transport()
    {
        var services = Compose(q => q.UseStorage(Storage(relay: true)).UseDelivery(Transport()));

        await Assert.That(services.BuildServiceProvider().GetRequiredService<IQueue>()).IsTypeOf<FakeQueue>();
    }

    [Test]
    public async Task A_transport_alone_enqueues_directly()
    {
        var provider = Compose(q => q.UseDelivery(Transport())).BuildServiceProvider();

        await Assert.That(provider.GetRequiredService<IQueue>()).IsTypeOf<DirectQueue>();
        await Assert.That(provider.GetRequiredService<IEnvelopeQueue>()).IsSameReferenceAs(provider.GetRequiredService<IQueue>());
        await Assert.That(provider.GetRequiredService<IQueueUnitOfWorkFactory>()).IsTypeOf<PassThroughQueueUnitOfWorkFactory>();
    }

    [Test]
    public async Task A_queue_registered_outside_AddQueue_fails()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IQueue, FakeQueue>();

        var ex = Assert.Throws<InvalidOperationException>(() => Compose(q => q.UseDelivery(Transport()), services));

        await Assert.That(ex.Message).Contains("already registered");
    }

    [Test]
    public async Task AddQueue_twice_fails()
    {
        var services = Compose(q => q.UseDelivery(Transport()));

        var ex = Assert.Throws<InvalidOperationException>(() => Compose(q => q.UseDelivery(Transport()), services));

        await Assert.That(ex.Message).Contains("already called");
    }

    [Test]
    public async Task An_explicit_dead_letter_store_wins_over_the_storages_own()
    {
        var explicitWriter = new FakeDeadLetterWriter();
        var services = Compose(q => q
            .UseStorage(new QueueStorageRole("UseTestStorage", (s, _) => s
                .AddSingleton<IQueue, FakeQueue>()
                .AddSingleton<IEnvelopeQueue, FakeEnvelopeQueue>()
                .TryAddDeadLetterWriter(new FakeDeadLetterWriter())))
            .UseDeadLetters(new QueueDeadLetterRole("UseTestDeadLetters", s => s.TryAddDeadLetterWriter(explicitWriter))));

        await Assert.That(services.BuildServiceProvider().GetRequiredService<IDeadLetterWriter>()).IsSameReferenceAs(explicitWriter);
    }

    [Test]
    public async Task Payload_serializer_follows_explicit_then_delivery_then_default()
    {
        var suggesting = Transport() with { SuggestedSerializer = typeof(FakeSerializer) };

        var byDefault = Compose(q => q.UseDelivery(Transport())).BuildServiceProvider();
        var bySuggestion = Compose(q => q.UseDelivery(suggesting)).BuildServiceProvider();
        var byChoice = Compose(q => q.UseDelivery(suggesting).UsePayloadSerializer<OtherSerializer>()).BuildServiceProvider();

        await Assert.That(byDefault.GetRequiredService<IQueuePayloadSerializer>()).IsTypeOf<DefaultJsonPayloadSerializer>();
        await Assert.That(bySuggestion.GetRequiredService<IQueuePayloadSerializer>()).IsTypeOf<FakeSerializer>();
        await Assert.That(byChoice.GetRequiredService<IQueuePayloadSerializer>()).IsTypeOf<OtherSerializer>();
    }

    private sealed class NullTransport : IQueueTransport
    {
        public Task<bool> SendAsync(OutboundMessage message, CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class OtherSerializer : IQueuePayloadSerializer
    {
        public string Serialize(object request, Type requestType) => "{}";
        public object? Deserialize(string json, Type requestType) => null;
    }
}

internal static class DeadLetterRegistration
{
    public static IServiceCollection TryAddDeadLetterWriter(this IServiceCollection services, IDeadLetterWriter writer)
    {
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton(services, writer);
        return services;
    }
}
