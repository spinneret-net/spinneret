using Google.Cloud.Tasks.V2;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
// Google.Cloud.Tasks.V2 declares its own Task; alias so the framework one wins here.
using Task = System.Threading.Tasks.Task;

namespace Spinneret.Queue.CloudTasks.Tests;

public sealed class EmulatorQueueInitializerTests
{
    private static readonly RpcException Unavailable =
        new(new Status(StatusCode.Unavailable, "no emulator is listening"));

    // Queue creation is background work, so StartAsync returns before it finishes.
    private static async Task<FakeCloudTasksClient> Start(
        bool emulator, Exception? throwOnCreateQueue = null)
    {
        var client = new FakeCloudTasksClient { ThrowOnCreateQueue = throwOnCreateQueue };
        var provider = TestSetup.BuildProvider(
            emulator ? TestSetup.EmulatorConfig() : TestSetup.Config(),
            client);

        foreach (var service in provider.GetServices<IHostedService>())
        {
            await service.StartAsync(CancellationToken.None);
            if (service is BackgroundService background)
                await background.ExecuteTask!;
        }

        return client;
    }

    // Instant backoff; maxRounds ends the indefinite retry loop the way a shutdown would.
    private static (EmulatorQueueInitializer Initializer, List<TimeSpan> Delays) CreateInitializer(
        IServiceProvider provider, int maxRounds = 20)
    {
        var delays = new List<TimeSpan>();
        var initializer = new EmulatorQueueInitializer(
            provider,
            provider.GetRequiredService<IOptions<CloudTasksOptions>>(),
            NullLogger<EmulatorQueueInitializer>.Instance,
            (delay, _) =>
            {
                delays.Add(delay);
                return delays.Count > maxRounds
                    ? throw new OperationCanceledException()
                    : Task.CompletedTask;
            });
        return (initializer, delays);
    }

    private static async Task RunAsync(EmulatorQueueInitializer initializer)
    {
        await initializer.StartAsync(CancellationToken.None);
        await initializer.ExecuteTask!;
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
        // Building a real client resolves credentials, which a dispatch-only host shouldn't need to boot.
        var resolved = false;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestQueue();
        // Registered last so it wins over the library's own factory.
        services.AddSingleton<CloudTasksClient>(_ =>
        {
            resolved = true;
            throw new InvalidOperationException("credentials would be resolved here");
        });

        var provider = services.BuildServiceProvider();
        var (initializer, _) = CreateInitializer(provider);

        await RunAsync(initializer);

        await Assert.That(resolved).IsFalse();
    }

    [Test]
    public async Task An_already_existing_queue_is_not_an_error()
    {
        var client = await Start(
            emulator: true,
            throwOnCreateQueue: new RpcException(new Status(StatusCode.AlreadyExists, "exists")));

        await Assert.That(client.CreatedQueues).IsEmpty();
    }

    [Test]
    public async Task An_emulator_without_queue_creation_support_is_not_retried()
    {
        // Some emulator builds only serve queues declared with -queue flags; retrying can't change that.
        var client = new FakeCloudTasksClient
        {
            ThrowOnCreateQueue = new RpcException(new Status(StatusCode.Unimplemented, "nope")),
        };
        var provider = TestSetup.BuildProvider(TestSetup.EmulatorConfig(), client);
        var (initializer, delays) = CreateInitializer(provider);

        await RunAsync(initializer);

        await Assert.That(client.CreatedQueues).IsEmpty();
        await Assert.That(delays).IsEmpty();
    }

    [Test]
    public async Task An_unreachable_emulator_does_not_fail_the_host()
    {
        // Hosts with an emulator configured include test hosts that have none.
        var client = new FakeCloudTasksClient { ThrowOnCreateQueue = Unavailable };
        var provider = TestSetup.BuildProvider(TestSetup.EmulatorConfig(), client);
        var (initializer, _) = CreateInitializer(provider, maxRounds: 3);

        await RunAsync(initializer);

        await Assert.That(client.CreatedQueues).IsEmpty();
    }

    [Test]
    public async Task Keeps_retrying_until_the_emulator_answers()
    {
        // Giving up would leave the queues missing until the next restart.
        var client = new FakeCloudTasksClient { TransientCreateQueueFailures = 2 };
        var provider = TestSetup.BuildProvider(TestSetup.EmulatorConfig(), client);
        var (initializer, delays) = CreateInitializer(provider);

        await RunAsync(initializer);

        await Assert.That(client.CreatedQueues.Count).IsEqualTo(2);
        await Assert.That(delays.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Backs_off_once_per_round_rather_than_once_per_queue()
    {
        // Channels share one attempt budget: four rounds over two channels is eight attempts, four waits.
        var client = new FakeCloudTasksClient { ThrowOnCreateQueue = Unavailable };
        var provider = TestSetup.BuildProvider(TestSetup.EmulatorConfig(), client);
        var (initializer, delays) = CreateInitializer(provider, maxRounds: 3);

        await RunAsync(initializer);

        await Assert.That(delays.Count).IsEqualTo(4);
        await Assert.That(client.CreateQueueAttempts).IsEqualTo(8);
    }

    [Test]
    public async Task A_queue_that_was_created_is_not_attempted_again_on_a_later_round()
    {
        var client = new FakeCloudTasksClient { TransientCreateQueueFailures = 1 };
        var provider = TestSetup.BuildProvider(TestSetup.EmulatorConfig(), client);
        var (initializer, _) = CreateInitializer(provider);

        await RunAsync(initializer);

        // Two channels: the first fails and is retried, the second succeeds.
        await Assert.That(client.CreateQueueAttempts).IsEqualTo(3);
        await Assert.That(client.CreatedQueues.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Backoff_doubles_from_the_base_delay()
    {
        await Assert.That(EmulatorQueueInitializer.BaseDelayForAttempt(1))
            .IsEqualTo(EmulatorQueueInitializer.BaseRetryDelay);
        await Assert.That(EmulatorQueueInitializer.BaseDelayForAttempt(2))
            .IsEqualTo(EmulatorQueueInitializer.BaseRetryDelay * 2);
        await Assert.That(EmulatorQueueInitializer.BaseDelayForAttempt(3))
            .IsEqualTo(EmulatorQueueInitializer.BaseRetryDelay * 4);
    }

    [Test]
    [Arguments(10)]
    [Arguments(100)]
    [Arguments(100_000)]
    public async Task Backoff_is_capped_and_never_overflows(int attempt)
    {
        // Retrying is indefinite, so the cap bounds the delay and the attempt counter grows unbounded.
        await Assert.That(EmulatorQueueInitializer.BaseDelayForAttempt(attempt))
            .IsEqualTo(EmulatorQueueInitializer.MaxRetryDelay);
    }

    [Test]
    public async Task Actual_delays_stay_within_the_jitter_band_of_their_attempt()
    {
        // Equal jitter: half the delay plus a random share of the other half.
        var client = new FakeCloudTasksClient { ThrowOnCreateQueue = Unavailable };
        var provider = TestSetup.BuildProvider(TestSetup.EmulatorConfig(), client);
        var (initializer, delays) = CreateInitializer(provider, maxRounds: 6);

        await RunAsync(initializer);

        foreach (var (delay, index) in delays.Select((d, i) => (d, i)))
        {
            var full = EmulatorQueueInitializer.BaseDelayForAttempt(index + 1);
            await Assert.That(delay).IsGreaterThanOrEqualTo(full / 2);
            await Assert.That(delay).IsLessThanOrEqualTo(full);
        }
    }
}
