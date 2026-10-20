using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spinneret.Queue;
using Spinneret.Queue.CloudTasks;
using Spinneret.Sql.Conformance;

namespace Spinneret.Composition.Tests;

/// <summary>Which compositions hold together, asserted on registrations — no database needed.</summary>
public sealed class CompositionRulesTests
{
    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Queue:Mssql:ConnectionString"] = "Server=localhost;Database=q;Integrated Security=true;",
        ["Queue:Postgres:ConnectionString"] = "Host=localhost;Database=q;Username=app;Password=secret",
        ["Queue:CloudTasks:ProjectId"] = "p",
        ["Queue:CloudTasks:LocationId"] = "l",
        ["Queue:CloudTasks:Channels:default"] = "q",
        ["Queue:CloudTasks:DispatcherUrl"] = "https://worker.test/internal/queue/dispatch",
        ["Queue:CloudTasks:ServiceAccountEmail"] = "sa@p.iam.gserviceaccount.com",
    }).Build();

    [Test]
    public async Task Cloud_tasks_over_sql_registers_the_relay_and_the_outbox()
    {
        var services = new ServiceCollection();
        services.AddQueue(q => q
            .AddRequestAssemblies(typeof(CompositionRulesTests).Assembly)
            .UseMssqlStorage(Configuration())
            .UseCloudTasksDelivery(Configuration()));

        await Assert.That(services.Any(d => d.ServiceType == typeof(IQueueRelay))).IsTrue();
        await Assert.That(services.Any(d => d.ServiceType == typeof(IHostedService)
            && d.ImplementationType?.Name == "SqlOutboxRelayService")).IsTrue();
        await Assert.That(services.Single(d => d.ServiceType == typeof(IQueue)).ImplementationFactory).IsNotNull();
    }

    [Test]
    public async Task Cloud_tasks_alone_has_no_relay()
    {
        var services = new ServiceCollection();
        services.AddQueue(q => q
            .AddRequestAssemblies(typeof(CompositionRulesTests).Assembly)
            .UseCloudTasksDelivery(Configuration()));

        await Assert.That(services.Any(d => d.ServiceType == typeof(IQueueRelay))).IsFalse();
    }

    [Test]
    public async Task Firestore_dead_letters_take_over_from_the_sql_table()
    {
        var services = new ServiceCollection();
        services.AddQueue(q => q
            .AddRequestAssemblies(typeof(CompositionRulesTests).Assembly)
            .UseMssqlStorage(Configuration())
            .UseInHostDelivery()
            .UseFirestoreDeadLetters());

        await Assert.That(services.Single(d => d.ServiceType == typeof(IDeadLetterWriter)).ImplementationType?.Name)
            .IsEqualTo("FirestoreDeadLetterWriter");
        await Assert.That(services.Single(d => d.ServiceType == typeof(IDeadLetterStore)).ImplementationType?.Name)
            .IsEqualTo("FirestoreDeadLetterStore");
    }

    [Test]
    public async Task In_host_delivery_and_cloud_tasks_cannot_both_deliver()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddQueue(q => q
            .AddRequestAssemblies(typeof(CompositionRulesTests).Assembly)
            .UseMssqlStorage(Configuration())
            .UseInHostDelivery()
            .UseCloudTasksDelivery(Configuration())));

        await Assert.That(ex.Message).Contains("UseInHostDelivery");
        await Assert.That(ex.Message).Contains("UseCloudTasksDelivery");
    }

    [Test]
    public async Task A_forwarding_horizon_beyond_what_cloud_tasks_accepts_fails_at_registration()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddQueue(q => q
            .AddRequestAssemblies(typeof(CompositionRulesTests).Assembly)
            .UseMssqlStorage(Configuration())
            .UseCloudTasksDelivery(Configuration(), o => o.Relay.ForwardAhead = TimeSpan.FromDays(31))));

        await Assert.That(ex.Message).Contains("Queue:CloudTasks:Relay:ForwardAhead");
    }

    [Test]
    public async Task Map_queue_relay_without_a_relay_fails_at_startup()
    {
        var services = new ServiceCollection();
        services.AddRouting();
        services.AddQueue(q => q
            .AddRequestAssemblies(typeof(CompositionRulesTests).Assembly)
            .UseCloudTasksDelivery(Configuration()));
        var endpoints = new RouteBuilderStub(services.BuildServiceProvider());

        var ex = Assert.Throws<InvalidOperationException>(() =>
            Microsoft.AspNetCore.Builder.CloudTasksEndpointRouteBuilderExtensions.MapQueueRelay(endpoints, CloudTasksAuth.PolicyName));

        await Assert.That(ex.Message).Contains("UseMssqlStorage");
    }

    private sealed class RouteBuilderStub(IServiceProvider services) : Microsoft.AspNetCore.Routing.IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = services;
        public ICollection<Microsoft.AspNetCore.Routing.EndpointDataSource> DataSources { get; } = [];
        public Microsoft.AspNetCore.Builder.IApplicationBuilder CreateApplicationBuilder() =>
            new Microsoft.AspNetCore.Builder.ApplicationBuilder(ServiceProvider);
    }

    [Test]
    public async Task Two_sql_storages_cannot_both_be_the_queues()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddQueue(q => q
            .AddRequestAssemblies(typeof(CompositionRulesTests).Assembly)
            .UseMssqlStorage(Configuration())
            .UsePostgresStorage(Configuration())));

        await Assert.That(ex.Message).Contains("UseMssqlStorage");
        await Assert.That(ex.Message).Contains("UsePostgresStorage");
    }

    [Test]
    public async Task A_scheduler_on_one_database_beside_a_queue_on_another_fails_when_the_host_starts()
    {
        // A due job's booking and its enqueue commit in one transaction, which no two databases can
        // share. Registration order is free, so the check runs as the host starts — before any
        // hosted service, so the schema initializer never creates a table in the wrong database.
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddQueue(q => q
                    .AddRequestAssemblies(typeof(CompositionRulesTests).Assembly)
                    .UseMssqlStorage(Configuration()));
                services.AddScheduler(s => s.UsePostgresStorage(Configuration()));
            })
            .Build();

        var ex = await Assert.ThrowsAsync<Exception>(() => host.StartAsync());

        await Assert.That(ex!.Message).Contains("'postgres' storage cannot share a transaction with a 'mssql' queue");
    }

    [Test]
    public async Task A_sql_scheduler_beside_a_queue_without_sql_storage_fails_when_the_host_starts()
    {
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddQueue(q => q
                    .AddRequestAssemblies(typeof(CompositionRulesTests).Assembly)
                    .UseCloudTasksDelivery(Configuration()));
                services.AddScheduler(s => s.UseMssqlStorage(Configuration()));
            })
            .Build();

        var ex = await Assert.ThrowsAsync<Exception>(() => host.StartAsync());

        await Assert.That(ex!.Message).Contains("s.UseMssqlStorage(...) requires the queue's storage in the same database");
    }
}
