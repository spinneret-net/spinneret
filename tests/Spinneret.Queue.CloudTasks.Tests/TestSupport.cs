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

namespace Spinneret.Queue.CloudTasks.Tests;

public sealed record PlainCommand(string Name, int Count) : IRequest<Unit>;

[QueuePolicy(Channel = "bulk")]
public sealed record BulkCommand(string Id) : IRequest<Unit>;

public sealed record StringResponseCommand(string Value) : IRequest<string>;

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

    // The issuer travels with the endpoint: without it, every emulator-minted token is rejected.
    public static IConfiguration EmulatorConfig(Action<Dictionary<string, string?>>? mutate = null) =>
        Config(values =>
        {
            values["Queue:CloudTasks:EmulatorEndpoint"] = EmulatorEndpoint;
            values["Queue:CloudTasks:OidcIssuer"] = EmulatorIssuer;
            mutate?.Invoke(values);
        });

    public static IConfiguration Config(Action<Dictionary<string, string?>>? mutate = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Queue:CloudTasks:ProjectId"] = ProjectId,
            ["Queue:CloudTasks:LocationId"] = LocationId,
            ["Queue:CloudTasks:Channels:default"] = DefaultQueueId,
            ["Queue:CloudTasks:Channels:bulk"] = BulkQueueId,
            ["Queue:CloudTasks:DispatcherUrl"] = DispatcherUrl,
            ["Queue:CloudTasks:ServiceAccountEmail"] = ServiceAccountEmail,
        };

        mutate?.Invoke(values);
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    public static IServiceCollection AddTestQueue(
        this IServiceCollection services, IConfiguration? config = null, Assembly? requestAssembly = null) =>
        services.AddQueue(q => q
            .AddRequestAssemblies(requestAssembly ?? typeof(PlainCommand).Assembly)
            .UseCloudTasksDelivery(config ?? Config()));

    public static ServiceProvider BuildProvider(
        IConfiguration? config = null,
        CloudTasksClient? client = null,
        Action<IServiceCollection>? configure = null,
        Assembly? requestAssembly = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        configure?.Invoke(services);
        services.AddTestQueue(config, requestAssembly);

        // Last registration wins, so this replaces the real client factory.
        if (client is not null)
            services.AddSingleton(client);

        return services.BuildServiceProvider();
    }
}
