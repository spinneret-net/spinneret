using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spinneret.Queue;

namespace Spinneret.Scheduler.Firestore.Tests;

public class CompositionTests
{
    private static IConfiguration EmptyConfiguration => new ConfigurationBuilder().Build();

    private sealed class FakeQueuePayloadSerializer : IQueuePayloadSerializer
    {
        public string Serialize(object request, Type requestType) => "{}";
        public object? Deserialize(string json, Type requestType) => Activator.CreateInstance(requestType);
    }

    private static ServiceCollection AnotherTransportsQueue()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(new QueueTypeRegistry([]));
        services.AddSingleton<IQueuePayloadSerializer, FakeQueuePayloadSerializer>();
        return services;
    }

    [Test]
    public async Task Firestore_schedules_compose_with_a_non_gcp_queue()
    {
        // Asserted on the collection: a real FirestoreDb needs credentials.
        var services = AnotherTransportsQueue();

        services.AddScheduler(s => s.UseFirestoreStorage(EmptyConfiguration).UseInHostSweeper());

        var sweep = services.Single(d => d.ServiceType == typeof(ISchedulerSweep));
        await Assert.That(sweep.ImplementationType!.FullName)
            .IsEqualTo("Spinneret.Scheduler.Firestore.FirestoreSchedulerDispatcher");
        await Assert.That(services
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType?.FullName))
            .Contains("Spinneret.Scheduler.InHostSweeperService");
    }

    [Test]
    public async Task The_queue_and_scheduler_registrations_commute()
    {
        // The natural order, which used to throw.
        var schedulerFirst = new ServiceCollection();
        schedulerFirst.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        schedulerFirst.AddScheduler(s => s.UseFirestoreStorage(EmptyConfiguration).UseInHostSweeper());
        schedulerFirst.AddSingleton(new QueueTypeRegistry([]));
        schedulerFirst.AddSingleton<IQueuePayloadSerializer, FakeQueuePayloadSerializer>();

        var queueFirst = AnotherTransportsQueue();
        queueFirst.AddScheduler(s => s.UseFirestoreStorage(EmptyConfiguration).UseInHostSweeper());

        await Assert.That(schedulerFirst.Select(d => d.ServiceType.FullName).OrderBy(n => n))
            .IsEquivalentTo(queueFirst.Select(d => d.ServiceType.FullName).OrderBy(n => n));
    }

    [Test]
    public async Task The_scheduler_package_needs_no_web_stack()
    {
        var referenced = typeof(FirestoreSchedulerOptions).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToArray();

        await Assert.That(referenced.Any(n => n!.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)))
            .IsFalse();
    }
}
