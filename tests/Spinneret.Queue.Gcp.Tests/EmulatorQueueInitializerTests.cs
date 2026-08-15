using Google.Cloud.Tasks.V2;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
// Google.Cloud.Tasks.V2 declares its own Task; alias so the framework one wins here.
using Task = System.Threading.Tasks.Task;

namespace Spinneret.Queue.Gcp.Tests;

/// <summary>
/// The initializer exists so the emulator's queues and <c>Queue:Gcp:Channels</c> cannot drift apart
/// in local development. It must stay inert against real Cloud Tasks, where queues are owned by
/// infrastructure-as-code — and it must never fail a host, since one boots with an emulator
/// configured long before any emulator is listening.
/// </summary>
public sealed class EmulatorQueueInitializerTests
{
    private static readonly RpcException Unavailable =
        new(new Status(StatusCode.Unavailable, "no emulator is listening"));

    /// <summary>
    /// Runs the registered hosted services to completion. Queue creation is background work, so
    /// <c>StartAsync</c> returns as soon as it is under way rather than when it is finished.
    /// </summary>
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

    /// <summary>
    /// An initializer whose backoff is instant, so a test drives the retry loop at full speed. The
    /// loop retries indefinitely, so <paramref name="maxRounds"/> ends it the way a shutdown does —
    /// the delay throws, exactly as <c>Task.Delay</c> does when the stopping token fires — letting a
    /// test assert on a permanently absent emulator without spinning forever.
    /// </summary>
    private static (EmulatorQueueInitializer Initializer, List<TimeSpan> Delays) CreateInitializer(
        IServiceProvider provider, int maxRounds = 20)
    {
        var delays = new List<TimeSpan>();
        var initializer = new EmulatorQueueInitializer(
            provider,
            provider.GetRequiredService<IOptions<GcpQueueOptions>>(),
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

    /// <summary>Starts the initializer and waits for its background work to finish.</summary>
    private static async Task RunAsync(EmulatorQueueInitializer initializer)
    {
        await initializer.StartAsync(CancellationToken.None);
        await initializer.ExecuteTask!;
    }

    // ------------------------------------------------------------------------------ creating ---

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
        // receives dispatches has no reason to hold any. Resolving it at host start would make every
        // production host need credentials just to boot — so the emulator check must come first.
        var resolved = false;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGcpQueue(TestSetup.Config(), o => o.RequestAssemblies = [typeof(PlainCommand).Assembly]);
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
        // Restarting against a warm emulator is the normal case, not a failure.
        var client = await Start(
            emulator: true,
            throwOnCreateQueue: new RpcException(new Status(StatusCode.AlreadyExists, "exists")));

        await Assert.That(client.CreatedQueues).IsEmpty();
    }

    [Test]
    public async Task An_emulator_without_queue_creation_support_is_not_retried()
    {
        // Some emulator builds only serve queues declared with -queue flags. Retrying cannot change
        // that answer, so it is a warning and the loop is done — not a queue pending forever.
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

    // ------------------------------------------------------------------------------- retrying ---

    [Test]
    public async Task An_unreachable_emulator_does_not_fail_the_host()
    {
        // The reason the work is off the startup path at all: a missing emulator is a local
        // development condition, and the hosts that boot with one configured include test hosts
        // that have none. Failing here would take a whole application down over queues that only
        // ever exist on a developer's machine.
        var client = new FakeCloudTasksClient { ThrowOnCreateQueue = Unavailable };
        var provider = TestSetup.BuildProvider(TestSetup.EmulatorConfig(), client);
        var (initializer, _) = CreateInitializer(provider, maxRounds: 3);

        await RunAsync(initializer);

        await Assert.That(client.CreatedQueues).IsEmpty();
    }

    [Test]
    public async Task Keeps_retrying_until_the_emulator_answers()
    {
        // An emulator container coming up alongside the host; one started minutes later takes the
        // same path, which is why giving up would leave the queues missing until the next restart.
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
        // The channels share one attempt budget: four rounds against two channels means eight
        // attempts and four waits. Per-queue budgets would multiply the wait by the number of
        // channels every time an emulator was slow to start.
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
        // Only what is still pending is retried, so a queue already created is left alone.
        var client = new FakeCloudTasksClient { TransientCreateQueueFailures = 1 };
        var provider = TestSetup.BuildProvider(TestSetup.EmulatorConfig(), client);
        var (initializer, _) = CreateInitializer(provider);

        await RunAsync(initializer);

        // Two channels: the first fails and is retried, the second succeeds straight away.
        await Assert.That(client.CreateQueueAttempts).IsEqualTo(3);
        await Assert.That(client.CreatedQueues.Count).IsEqualTo(2);
    }

    // -------------------------------------------------------------------------------- backoff ---

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
        // Retrying is indefinite, so the cap is what bounds an emulator that never arrives to about
        // one log line a minute — and the attempt counter climbs without limit on a long-lived host.
        await Assert.That(EmulatorQueueInitializer.BaseDelayForAttempt(attempt))
            .IsEqualTo(EmulatorQueueInitializer.MaxRetryDelay);
    }

    [Test]
    public async Task Actual_delays_stay_within_the_jitter_band_of_their_attempt()
    {
        // Equal jitter: half the computed delay plus a random share of the other half.
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
