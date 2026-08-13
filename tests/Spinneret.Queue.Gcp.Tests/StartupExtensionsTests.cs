using Spinneret.Functional;
using Google.Cloud.Tasks.V2;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Spinneret.Mediator;
using Task = System.Threading.Tasks.Task;

namespace Spinneret.Queue.Gcp.Tests;

public sealed class StartupExtensionsTests
{
    private sealed class FakeMediator : ISpinneretMediator
    {
        public Task Send(IRequest<Unit> request, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
            => Task.FromResult(default(TResponse)!);
    }

    private sealed class NoopDeadLetterWriter : IDeadLetterWriter
    {
        public Task WriteAsync(DeadLetterEntry entry, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CustomPayloadSerializer : IQueuePayloadSerializer
    {
        public string Serialize(object request, Type requestType) => "{}";
        public object? Deserialize(string json, Type requestType) => null;
    }

    [Test]
    public async Task AddGcpQueue_registers_same_singleton_for_queue_and_envelope_queue()
    {
        var provider = TestSetup.BuildProvider(client: new FakeCloudTasksClient());

        var queue = provider.GetRequiredService<IQueue>();
        var envelopeQueue = provider.GetRequiredService<IEnvelopeQueue>();

        await Assert.That(ReferenceEquals(queue, envelopeQueue)).IsTrue();
        await Assert.That(ReferenceEquals(queue, provider.GetRequiredService<IQueue>())).IsTrue();
    }

    [Test]
    public async Task AddGcpQueue_registers_host_json_payload_serializer()
    {
        var provider = TestSetup.BuildProvider();

        var serializer = provider.GetRequiredService<IQueuePayloadSerializer>();

        await Assert.That(serializer.GetType().Name).IsEqualTo("HostJsonPayloadSerializer");
    }

    [Test]
    public async Task AddGcpQueue_keeps_payload_serializer_registered_by_the_host()
    {
        var custom = new CustomPayloadSerializer();

        var provider = TestSetup.BuildProvider(
            configure: services => services.AddSingleton<IQueuePayloadSerializer>(custom));

        await Assert.That(ReferenceEquals(provider.GetRequiredService<IQueuePayloadSerializer>(), custom)).IsTrue();
    }

    [Test]
    public async Task AddGcpQueue_registers_type_registry_scanned_from_supplied_assemblies()
    {
        var provider = TestSetup.BuildProvider();

        var registry = provider.GetRequiredService<QueueTypeRegistry>();

        await Assert.That(registry.GetName(typeof(PlainCommand))).IsEqualTo(typeof(PlainCommand).FullName!);
        await Assert.That(registry.DeclaredChannels).Contains("bulk");
    }

    [Test]
    public async Task AddGcpQueue_binds_options_from_queue_gcp_section()
    {
        var provider = TestSetup.BuildProvider();

        var options = provider.GetRequiredService<IOptions<GcpQueueOptions>>().Value;

        await Assert.That(options.ProjectId).IsEqualTo(TestSetup.ProjectId);
        await Assert.That(options.LocationId).IsEqualTo(TestSetup.LocationId);
        await Assert.That(options.DispatcherUrl).IsEqualTo(TestSetup.DispatcherUrl);
        await Assert.That(options.ServiceAccountEmail).IsEqualTo(TestSetup.ServiceAccountEmail);
        await Assert.That(options.Channels["default"]).IsEqualTo(TestSetup.DefaultQueueId);
        await Assert.That(options.Channels["bulk"]).IsEqualTo(TestSetup.BulkQueueId);
    }

    [Test]
    public async Task AddGcpQueue_registers_scoped_delivery_pipeline_resolvable_with_host_services()
    {
        var provider = TestSetup.BuildProvider(
            client: new FakeCloudTasksClient(),
            configure: services =>
            {
                services.AddSingleton<ISpinneretMediator, FakeMediator>();
                services.AddSingleton<IDeadLetterWriter, NoopDeadLetterWriter>();
            });

        using var scope = provider.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IQueueDeliveryProcessor>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IQueueDispatcher>();

        await Assert.That(processor).IsNotNull();
        await Assert.That(dispatcher).IsNotNull();
    }

    [Test]
    public async Task AddGcpQueue_with_emulator_endpoint_builds_client_without_credentials()
    {
        var config = TestSetup.Config(values => values["Queue:Gcp:EmulatorEndpoint"] = "localhost:8123");
        var provider = TestSetup.BuildProvider(config);

        var client = provider.GetRequiredService<CloudTasksClient>();

        await Assert.That(client).IsNotNull();
    }

    [Test]
    [Arguments("Queue:Gcp:ProjectId", "ProjectId")]
    [Arguments("Queue:Gcp:LocationId", "LocationId")]
    [Arguments("Queue:Gcp:Channels:default", "Channels:default")]
    [Arguments("Queue:Gcp:Channels:bulk", "bulk")]
    [Arguments("Queue:Gcp:DispatcherUrl", "DispatcherUrl")]
    [Arguments("Queue:Gcp:ServiceAccountEmail", "ServiceAccountEmail")]
    public async Task AddGcpQueue_missing_required_configuration_fails_at_startup(
        string missingKey, string expectedMessagePart)
    {
        var config = TestSetup.Config(values => values.Remove(missingKey));
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddGcpQueue(config, typeof(PlainCommand).Assembly));

        await Assert.That(ex.Message).Contains(expectedMessagePart);
    }
}
