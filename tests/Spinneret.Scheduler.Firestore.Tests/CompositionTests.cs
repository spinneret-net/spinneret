using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spinneret.Queue;

namespace Spinneret.Scheduler.Firestore.Tests;

/// <summary>
/// Transport, schedule storage and trigger are separate choices. These assert the combination that
/// motivated the split — Firestore schedules dispatched onto a queue that is not Cloud Tasks — and
/// that the three registrations commute.
/// </summary>
public class CompositionTests
{
    private static IConfiguration EmptyConfiguration => new ConfigurationBuilder().Build();

    /// <summary>Stands in for whichever transport the host chose; the scheduler only needs the seam.</summary>
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
        // Asserted on the collection rather than by resolving: constructing a real FirestoreDb needs
        // credentials, and what matters here is that the wiring lines up across three packages.
        var services = AnotherTransportsQueue();

        services.AddFirestoreScheduler(EmptyConfiguration);
        services.AddSchedulerSweeper();

        var sweep = services.Single(d => d.ServiceType == typeof(ISchedulerSweep));
        await Assert.That(sweep.ImplementationType!.FullName)
            .IsEqualTo("Spinneret.Scheduler.Firestore.FirestoreSchedulerDispatcher");
        await Assert.That(services
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType?.FullName))
            .Contains("Spinneret.Scheduler.SchedulerSweeperService");
    }

    [Test]
    public async Task The_three_registrations_commute()
    {
        // Sweeper first, storage second, transport last — the order a reader would naturally write,
        // and one that used to throw.
        var sweeperFirst = new ServiceCollection();
        sweeperFirst.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        sweeperFirst.AddSchedulerSweeper();
        sweeperFirst.AddFirestoreScheduler(EmptyConfiguration);
        sweeperFirst.AddSingleton(new QueueTypeRegistry([]));
        sweeperFirst.AddSingleton<IQueuePayloadSerializer, FakeQueuePayloadSerializer>();

        var storageFirst = AnotherTransportsQueue();
        storageFirst.AddFirestoreScheduler(EmptyConfiguration);
        storageFirst.AddSchedulerSweeper();

        await Assert.That(sweeperFirst.Select(d => d.ServiceType.FullName).OrderBy(n => n))
            .IsEquivalentTo(storageFirst.Select(d => d.ServiceType.FullName).OrderBy(n => n));
    }

    [Test]
    public async Task The_scheduler_package_needs_no_web_stack()
    {
        // The HTTP trigger moved to Spinneret.Scheduler.Http, so this package must not drag
        // ASP.NET Core in behind a worker service that has no use for it.
        var referenced = typeof(FirestoreSchedulerOptions).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToArray();

        await Assert.That(referenced.Any(n => n!.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)))
            .IsFalse();
    }
}
