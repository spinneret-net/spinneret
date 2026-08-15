using Spinneret.Functional;
using System.Reflection;
using Google.Api.Gax.Grpc;
using Google.Cloud.Tasks.V2;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Spinneret.Mediator;
using CloudTask = Google.Cloud.Tasks.V2.Task;
using Task = System.Threading.Tasks.Task;

namespace Spinneret.Queue.Gcp.Tests;

public sealed record PlainCommand(string Name, int Count) : IRequest<Unit>;

[QueuePolicy(Channel = "bulk")]
public sealed record BulkCommand(string Id) : IRequest<Unit>;

public sealed record StringResponseCommand(string Value) : IRequest<string>;

/// <summary>
/// Hand-rolled <see cref="CloudTasksClient"/> capturing task creation. All convenience
/// overloads of CreateTaskAsync funnel into the (CreateTaskRequest, CallSettings) overload,
/// so overriding it captures every enqueue the library performs.
/// </summary>
public sealed class FakeCloudTasksClient : CloudTasksClient
{
    public List<CreateTaskRequest> Requests { get; } = [];

    /// <summary>Queue resources created through <c>CreateQueueAsync</c>, in call order.</summary>
    public List<CreateQueueRequest> CreatedQueues { get; } = [];

    /// <summary>Every <c>CreateQueueAsync</c> call, including the ones made to fail.</summary>
    public int CreateQueueAttempts { get; private set; }

    /// <summary>
    /// Leading <c>CreateQueueAsync</c> calls that fail as an emulator that is not listening yet
    /// does, after which creation succeeds — an emulator container starting alongside the host.
    /// </summary>
    public int TransientCreateQueueFailures { get; set; }

    public Exception? ThrowOnCreateTask { get; set; }
    public Exception? ThrowOnCreateQueue { get; set; }

    public CreateTaskRequest SingleRequest => Requests.Single();

    public override Task<CloudTask> CreateTaskAsync(CreateTaskRequest request, CallSettings? callSettings = null)
    {
        if (ThrowOnCreateTask is not null)
            throw ThrowOnCreateTask;

        Requests.Add(request);
        return Task.FromResult(request.Task);
    }

    public override Task<Google.Cloud.Tasks.V2.Queue> CreateQueueAsync(
        CreateQueueRequest request, CallSettings? callSettings = null)
    {
        CreateQueueAttempts++;

        if (ThrowOnCreateQueue is not null)
            throw ThrowOnCreateQueue;

        if (TransientCreateQueueFailures > 0)
        {
            TransientCreateQueueFailures--;
            throw new RpcException(new Status(StatusCode.Unavailable, "the emulator is not up yet"));
        }

        CreatedQueues.Add(request);
        return Task.FromResult(request.Queue);
    }
}

internal static class TestSetup
{
    public const string ProjectId = "test-project";
    public const string LocationId = "europe-north1";
    public const string DefaultQueueId = "default-queue";
    public const string BulkQueueId = "bulk-queue";
    public const string DispatcherUrl = "https://worker.example.com/internal/queue/dispatch";
    public const string ServiceAccountEmail = "tasks@test-project.iam.gserviceaccount.com";
    public const string EmulatorEndpoint = "localhost:8123";
    public const string EmulatorIssuer = "http://localhost:8980";

    /// <summary>
    /// Emulator configuration. The issuer travels with the endpoint because the two are only valid
    /// together: with an emulator but no issuer, the dispatch endpoint would still validate against
    /// accounts.google.com and reject every emulator-minted token.
    /// </summary>
    public static IConfiguration EmulatorConfig(Action<Dictionary<string, string?>>? mutate = null) =>
        Config(values =>
        {
            values["Queue:Gcp:EmulatorEndpoint"] = EmulatorEndpoint;
            values["Queue:Gcp:OidcIssuer"] = EmulatorIssuer;
            mutate?.Invoke(values);
        });

    public static IConfiguration Config(Action<Dictionary<string, string?>>? mutate = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Queue:Gcp:ProjectId"] = ProjectId,
            ["Queue:Gcp:LocationId"] = LocationId,
            ["Queue:Gcp:Channels:default"] = DefaultQueueId,
            ["Queue:Gcp:Channels:bulk"] = BulkQueueId,
            ["Queue:Gcp:DispatcherUrl"] = DispatcherUrl,
            ["Queue:Gcp:ServiceAccountEmail"] = ServiceAccountEmail,
        };

        mutate?.Invoke(values);
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    public static ServiceProvider BuildProvider(
        IConfiguration? config = null,
        CloudTasksClient? client = null,
        Action<IServiceCollection>? configure = null,
        Assembly? requestAssembly = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        configure?.Invoke(services);
        services.AddGcpQueue(config ?? Config(), o => o.RequestAssemblies = [requestAssembly ?? typeof(PlainCommand).Assembly]);

        // Last registration wins for constructor injection, so this replaces the real
        // client factory registration without touching the library's wiring.
        if (client is not null)
            services.AddSingleton(client);

        return services.BuildServiceProvider();
    }
}
