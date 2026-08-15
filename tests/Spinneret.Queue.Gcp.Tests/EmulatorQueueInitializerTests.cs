using Google.Cloud.Tasks.V2;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
// Google.Cloud.Tasks.V2 declares its own Task; alias so the framework one wins here.
using Task = System.Threading.Tasks.Task;

namespace Spinneret.Queue.Gcp.Tests;

/// <summary>
/// The initializer exists so the emulator's queues and <c>Queue:Gcp:Channels</c> cannot drift apart
/// in local development. It must stay inert against real Cloud Tasks, where queues are owned by
/// infrastructure-as-code.
/// </summary>
public sealed class EmulatorQueueInitializerTests
{
    private static async Task<FakeCloudTasksClient> Start(
        bool emulator, Exception? throwOnCreateQueue = null)
    {
        var client = new FakeCloudTasksClient { ThrowOnCreateQueue = throwOnCreateQueue };
        var provider = TestSetup.BuildProvider(
            emulator ? TestSetup.EmulatorConfig() : TestSetup.Config(),
            client);

        foreach (var service in provider.GetServices<IHostedService>())
            await service.StartAsync(CancellationToken.None);

        return client;
    }

    [Test]
    public async Task Creates_a_queue_for_every_configured_channel_against_the_emulator()
    {
        var client = await Start(emulator: true);

        await Assert.That(client.CreatedQueues.Select(q => q.Queue.Name)).IsEquivalentTo(new[]
        {
            new QueueName(TestSetup.ProjectId, TestSetup.LocationId, TestSetup.DefaultQueueId).ToString(),
            new QueueName(TestSetup.ProjectId, TestSetup.LocationId, TestSetup.BulkQueueId).ToString(),
        });
    }

    [Test]
    public async Task Creates_each_queue_under_its_project_and_location()
    {
        var client = await Start(emulator: true);

        await Assert.That(client.CreatedQueues.Select(q => q.Parent).Distinct())
            .IsEquivalentTo(new[] { $"projects/{TestSetup.ProjectId}/locations/{TestSetup.LocationId}" });
    }

    [Test]
    public async Task Creates_nothing_when_no_emulator_is_configured()
    {
        // Production queues are Terraform's, not the app's.
        var client = await Start(emulator: false);

        await Assert.That(client.CreatedQueues).IsEmpty();
    }

    [Test]
    public async Task Without_an_emulator_it_never_resolves_the_cloud_tasks_client()
    {
        // Building a real client resolves Application Default Credentials, and a host that only
        // receives dispatches has no reason to hold any. Resolving it at host start would make
        // every production host need credentials just to boot — so the emulator check must come
        // first. A factory that throws stands in for "no credentials available".
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGcpQueue(TestSetup.Config(), o => o.RequestAssemblies = [typeof(PlainCommand).Assembly]);
        // Registered last so it wins over the library's own factory.
        services.AddSingleton<CloudTasksClient>(
            _ => throw new InvalidOperationException("credentials would be resolved here"));

        var provider = services.BuildServiceProvider();
        var initializer = provider.GetServices<IHostedService>()
            .Single(s => s.GetType().Name == "EmulatorQueueInitializer");

        await initializer.StartAsync(CancellationToken.None);
    }

    [Test]
    public async Task An_already_existing_queue_is_not_an_error()
    {
        // Restarting against a warm emulator is the normal case, not a failure.
        var client = await Start(
            emulator: true,
            throwOnCreateQueue: new RpcException(new Status(StatusCode.AlreadyExists, "exists")));

        await Assert.That(client.CreatedQueues).IsEmpty();
    }

    [Test]
    public async Task An_emulator_without_queue_creation_support_does_not_fail_startup()
    {
        // Some emulator builds only serve queues declared with -queue flags; that is a warning,
        // not a reason to stop a local host from booting.
        var client = await Start(
            emulator: true,
            throwOnCreateQueue: new RpcException(new Status(StatusCode.Unimplemented, "nope")));

        await Assert.That(client.CreatedQueues).IsEmpty();
    }
}
