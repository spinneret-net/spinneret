using Microsoft.Extensions.DependencyInjection;
using Spinneret.Mediator;

namespace Spinneret.Queue.Tests;

public class StartupExtensionsTests
{
    private static ServiceCollection ServicesWithAllDependencies()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISpinneretMediator>(new FakeMediator());
        services.AddSingleton<IQueuePayloadSerializer>(new FakeSerializer());
        services.AddSingleton<IEnvelopeQueue>(new FakeEnvelopeQueue());
        services.AddSingleton<IDeadLetterWriter>(new FakeDeadLetterWriter());
        services.AddNullLogging();
        return services;
    }

    [Test]
    public async Task AddQueueCore_without_assemblies_throws_argument_exception()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<ArgumentException>(() => services.AddQueueCore([]));

        await Assert.That(ex.Message).Contains("At least one assembly");
    }

    [Test]
    public async Task AddQueueCore_assembly_overload_builds_and_registers_the_registry()
    {
        var services = new ServiceCollection();

        services.AddQueueCore([typeof(StartupExtensionsTests).Assembly]);
        var registry = services.BuildServiceProvider().GetRequiredService<QueueTypeRegistry>();

        await Assert.That(registry.GetPolicy(typeof(UnannotatedCommand))).IsEqualTo(QueuePolicy.Default);
    }

    [Test]
    public async Task AddQueueCore_registry_overload_registers_the_given_instance_as_singleton()
    {
        var registry = new QueueTypeRegistry([typeof(StartupExtensionsTests).Assembly]);
        var services = new ServiceCollection();

        services.AddQueueCore(registry);
        var provider = services.BuildServiceProvider();

        await Assert.That(ReferenceEquals(provider.GetRequiredService<QueueTypeRegistry>(), registry)).IsTrue();
    }

    [Test]
    public async Task AddQueueCore_registers_resolvable_dispatcher_and_delivery_processor()
    {
        var services = ServicesWithAllDependencies();
        services.AddQueueCore([typeof(StartupExtensionsTests).Assembly]);

        using var scope = services.BuildServiceProvider().CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IQueueDispatcher>();
        var processor = scope.ServiceProvider.GetRequiredService<IQueueDeliveryProcessor>();

        await Assert.That(dispatcher).IsNotNull();
        await Assert.That(processor).IsNotNull();
    }

    [Test]
    public async Task AddQueueCore_keeps_a_pre_registered_dispatcher_implementation()
    {
        var fake = new FakeDispatcher();
        var services = ServicesWithAllDependencies();
        services.AddSingleton<IQueueDispatcher>(fake);

        services.AddQueueCore([typeof(StartupExtensionsTests).Assembly]);
        var resolved = services.BuildServiceProvider().GetRequiredService<IQueueDispatcher>();

        await Assert.That(ReferenceEquals(resolved, fake)).IsTrue();
    }

    [Test]
    public async Task AddQueueCore_defaults_the_time_provider_to_system()
    {
        var services = new ServiceCollection();

        services.AddQueueCore([typeof(StartupExtensionsTests).Assembly]);
        var provider = services.BuildServiceProvider();

        await Assert.That(ReferenceEquals(provider.GetRequiredService<TimeProvider>(), TimeProvider.System)).IsTrue();
    }

    [Test]
    public async Task AddQueueCore_keeps_a_pre_registered_time_provider()
    {
        var fixedTime = new FixedTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(fixedTime);

        services.AddQueueCore([typeof(StartupExtensionsTests).Assembly]);
        var resolved = services.BuildServiceProvider().GetRequiredService<TimeProvider>();

        await Assert.That(ReferenceEquals(resolved, fixedTime)).IsTrue();
    }
}
