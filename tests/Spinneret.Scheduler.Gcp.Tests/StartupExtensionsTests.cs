using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Spinneret.Queue;

namespace Spinneret.Scheduler.Gcp.Tests;

/// <summary>
/// Verifies the DI surface of <see cref="StartupExtensions.AddGcpScheduler"/> against the
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
        services.AddGcpScheduler(configuration ?? EmptyConfiguration);
        return services;
    }

    [Test]
    public async Task AddGcpScheduler_registers_recurring_job_scheduler_as_firestore_singleton()
    {
        var services = AddScheduler();

        var descriptor = services.Single(d => d.ServiceType == typeof(IRecurringJobScheduler));

        await Assert.That(descriptor.Lifetime).IsEqualTo(ServiceLifetime.Singleton);
        await Assert.That(descriptor.ImplementationType!.FullName)
            .IsEqualTo("Spinneret.Scheduler.Gcp.FirestoreScheduler");
    }

    [Test]
    public async Task AddGcpScheduler_registers_transactional_scheduler_as_singleton()
    {
        var services = AddScheduler();

        var descriptor = services.Single(d => d.ServiceType == typeof(IFirestoreTransactionalScheduler));

        await Assert.That(descriptor.Lifetime).IsEqualTo(ServiceLifetime.Singleton);
        await Assert.That(descriptor.ImplementationType!.FullName)
            .IsEqualTo("Spinneret.Scheduler.Gcp.FirestoreTransactionalScheduler");
    }

    [Test]
    public async Task AddGcpScheduler_registers_document_factory_and_dispatcher_as_singletons()
    {
        var services = AddScheduler();

        var factory = services.Single(d =>
            d.ServiceType.FullName == "Spinneret.Scheduler.Gcp.ScheduledJobDocumentFactory");
        var dispatcher = services.Single(d =>
            d.ServiceType.FullName == "Spinneret.Scheduler.Gcp.GcpSchedulerDispatcher");

        await Assert.That(factory.Lifetime).IsEqualTo(ServiceLifetime.Singleton);
        await Assert.That(dispatcher.Lifetime).IsEqualTo(ServiceLifetime.Singleton);
    }

    [Test]
    public async Task AddGcpScheduler_registers_recurring_job_installer_as_hosted_service()
    {
        var services = AddScheduler();

        var descriptor = services.Single(d => d.ServiceType == typeof(IHostedService));

        await Assert.That(descriptor.ImplementationType!.FullName)
            .IsEqualTo("Spinneret.Scheduler.Gcp.RecurringJobInstaller");
    }

    [Test]
    public async Task AddGcpScheduler_binds_options_from_the_scheduler_gcp_section()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scheduler:Gcp:Collection"] = "custom_jobs",
                ["Scheduler:Gcp:OneShotLeaseWindow"] = "00:10:00",
            })
            .Build();

        await using var provider = AddScheduler(configuration).BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<GcpSchedulerOptions>>().Value;

        await Assert.That(options.Collection).IsEqualTo("custom_jobs");
        await Assert.That(options.OneShotLeaseWindow).IsEqualTo(TimeSpan.FromMinutes(10));
    }

    [Test]
    public async Task AddGcpScheduler_without_configuration_section_keeps_option_defaults()
    {
        await using var provider = AddScheduler().BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<GcpSchedulerOptions>>().Value;

        await Assert.That(options.Collection).IsEqualTo("scheduled_jobs");
        await Assert.That(options.OneShotLeaseWindow).IsEqualTo(TimeSpan.FromMinutes(5));
    }

    [Test]
    public async Task AddGcpScheduler_called_twice_does_not_duplicate_registrations()
    {
        var services = new ServiceCollection();

        services.AddGcpScheduler(EmptyConfiguration);
        services.AddGcpScheduler(EmptyConfiguration);

        await Assert.That(services.Count(d => d.ServiceType == typeof(IRecurringJobScheduler))).IsEqualTo(1);
        await Assert.That(services.Count(d => d.ServiceType == typeof(IFirestoreTransactionalScheduler))).IsEqualTo(1);
        await Assert.That(services.Count(d => d.ServiceType == typeof(IHostedService))).IsEqualTo(1);
        await Assert.That(services.Count(d =>
            d.ServiceType.FullName == "Spinneret.Scheduler.Gcp.GcpSchedulerDispatcher")).IsEqualTo(1);
    }

    [Test]
    public async Task AddGcpScheduler_document_factory_resolves_once_queue_dependencies_exist()
    {
        var services = AddScheduler();
        services.AddSingleton(new QueueTypeRegistry([]));
        services.AddSingleton<IQueuePayloadSerializer>(new FakePayloadSerializer());
        var factoryType = services
            .Single(d => d.ServiceType.FullName == "Spinneret.Scheduler.Gcp.ScheduledJobDocumentFactory")
            .ServiceType;

        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService(factoryType);

        await Assert.That(factory).IsNotNull();
    }
}
