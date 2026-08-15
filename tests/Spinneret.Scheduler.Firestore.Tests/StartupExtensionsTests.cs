using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Spinneret.Queue;

namespace Spinneret.Scheduler.Firestore.Tests;

/// <summary>
/// Verifies the DI surface of <c>AddFirestoreScheduler</c> against the
/// referenced assembly. Internal implementation types cannot be named here (the source-linked
/// copies compiled into this test assembly are distinct types), so registrations are asserted
/// via descriptor inspection by type name.
/// </summary>
public class StartupExtensionsTests
{
    private static IConfiguration EmptyConfiguration => new ConfigurationBuilder().Build();

    private static ServiceCollection AddScheduler(IConfiguration? configuration = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new QueueTypeRegistry([]));
        services.AddFirestoreScheduler(configuration ?? EmptyConfiguration);
        return services;
    }

    [Test]
    public async Task AddFirestoreScheduler_composes_in_either_order_relative_to_the_queue()
    {
        // Previously this threw when the scheduler was registered first, even though the resulting
        // container was identical. Nothing here reads the collection, so the calls commute.
        var before = new ServiceCollection();
        before.AddSingleton(new QueueTypeRegistry([]));
        before.AddFirestoreScheduler(EmptyConfiguration);

        var after = new ServiceCollection();
        after.AddFirestoreScheduler(EmptyConfiguration);
        after.AddSingleton(new QueueTypeRegistry([]));

        await Assert.That(after.Select(d => d.ServiceType.FullName).OrderBy(n => n))
            .IsEquivalentTo(before.Select(d => d.ServiceType.FullName).OrderBy(n => n));
    }

    [Test]
    public async Task AddFirestoreScheduler_registers_the_sweep_engine_but_not_a_trigger()
    {
        var services = AddScheduler();

        var sweep = services.Single(d => d.ServiceType == typeof(ISchedulerSweep));
        await Assert.That(sweep.ImplementationType!.FullName)
            .IsEqualTo("Spinneret.Scheduler.Firestore.FirestoreSchedulerDispatcher");
        await Assert.That(services
            .Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService))
            .Select(d => d.ImplementationType?.FullName))
            .DoesNotContain("Spinneret.Scheduler.SchedulerSweeperService");
    }

    [Test]
    public async Task AddFirestoreScheduler_registers_recurring_job_scheduler_as_firestore_singleton()
    {
        var services = AddScheduler();

        var descriptor = services.Single(d => d.ServiceType == typeof(IRecurringJobScheduler));

        await Assert.That(descriptor.Lifetime).IsEqualTo(ServiceLifetime.Singleton);
        await Assert.That(descriptor.ImplementationType!.FullName)
            .IsEqualTo("Spinneret.Scheduler.Firestore.FirestoreScheduler");
    }

    [Test]
    public async Task AddFirestoreScheduler_registers_transactional_scheduler_as_singleton()
    {
        var services = AddScheduler();

        var descriptor = services.Single(d => d.ServiceType == typeof(IFirestoreTransactionalScheduler));

        await Assert.That(descriptor.Lifetime).IsEqualTo(ServiceLifetime.Singleton);
        await Assert.That(descriptor.ImplementationType!.FullName)
            .IsEqualTo("Spinneret.Scheduler.Firestore.FirestoreTransactionalScheduler");
    }

    [Test]
    public async Task AddFirestoreScheduler_registers_document_factory_and_dispatcher_as_singletons()
    {
        var services = AddScheduler();

        var factory = services.Single(d =>
            d.ServiceType.FullName == "Spinneret.Scheduler.Firestore.ScheduledJobDocumentFactory");
        var dispatcher = services.Single(d => d.ServiceType == typeof(ISchedulerSweep));

        await Assert.That(factory.Lifetime).IsEqualTo(ServiceLifetime.Singleton);
        await Assert.That(dispatcher.Lifetime).IsEqualTo(ServiceLifetime.Singleton);
    }

    [Test]
    public async Task AddFirestoreScheduler_registers_recurring_job_installer_as_hosted_service()
    {
        var services = AddScheduler();

        var descriptor = services.Single(d => d.ServiceType == typeof(IHostedService));

        await Assert.That(descriptor.ImplementationType!.FullName)
            .IsEqualTo("Spinneret.Scheduler.RecurringJobInstaller");
    }

    [Test]
    public async Task AddFirestoreScheduler_binds_options_from_the_scheduler_gcp_section()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scheduler:Firestore:Collection"] = "custom_jobs",
                ["Scheduler:Firestore:OneShotLeaseWindow"] = "00:10:00",
            })
            .Build();

        await using var provider = AddScheduler(configuration).BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<FirestoreSchedulerOptions>>().Value;

        await Assert.That(options.Collection).IsEqualTo("custom_jobs");
        await Assert.That(options.OneShotLeaseWindow).IsEqualTo(TimeSpan.FromMinutes(10));
    }

    [Test]
    public async Task AddFirestoreScheduler_without_configuration_section_keeps_option_defaults()
    {
        await using var provider = AddScheduler().BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<FirestoreSchedulerOptions>>().Value;

        await Assert.That(options.Collection).IsEqualTo("scheduled_jobs");
        await Assert.That(options.OneShotLeaseWindow).IsEqualTo(TimeSpan.FromMinutes(5));
    }

    [Test]
    public async Task AddFirestoreScheduler_called_twice_does_not_duplicate_registrations()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new QueueTypeRegistry([]));

        services.AddFirestoreScheduler(EmptyConfiguration);
        services.AddFirestoreScheduler(EmptyConfiguration);

        await Assert.That(services.Count(d => d.ServiceType == typeof(IRecurringJobScheduler))).IsEqualTo(1);
        await Assert.That(services.Count(d => d.ServiceType == typeof(IFirestoreTransactionalScheduler))).IsEqualTo(1);
        await Assert.That(services.Count(d => d.ServiceType == typeof(IHostedService))).IsEqualTo(1);
        await Assert.That(services.Count(d => d.ServiceType == typeof(ISchedulerSweep))).IsEqualTo(1);
    }

    [Test]
    public async Task AddFirestoreScheduler_document_factory_resolves_once_queue_dependencies_exist()
    {
        var services = AddScheduler();
        services.AddSingleton(new QueueTypeRegistry([]));
        services.AddSingleton<IQueuePayloadSerializer>(new FakePayloadSerializer());
        var factoryType = services
            .Single(d => d.ServiceType.FullName == "Spinneret.Scheduler.Firestore.ScheduledJobDocumentFactory")
            .ServiceType;

        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService(factoryType);

        await Assert.That(factory).IsNotNull();
    }
}
