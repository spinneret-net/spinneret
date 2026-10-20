using System.Net;
using System.Text;
using System.Text.Json;
using Google.Cloud.Tasks.V2;
using Task = System.Threading.Tasks.Task;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Spinneret.Functional;
using Spinneret.Mediator;
using Spinneret.Queue;
using Spinneret.Queue.CloudTasks;
using Spinneret.Queue.Sql;

namespace Spinneret.Sql.Conformance;

// Side effects ride the delivery's ambient transaction, so their survival proves it committed as one.

public sealed record PingCommand(string Name) : IRequest<Unit>;

public sealed record WriteAndCascadeCommand(string Table, string Name) : IRequest<Unit>;

public sealed record WriteThenFailCommand(string Table) : IRequest<Unit>;

[QueuePolicy(MinBackoff = "00:00:00.050", MaxBackoff = "00:00:00.100")]
public sealed record FailOnceCommand(string Key) : IRequest<Unit>;

public sealed class PingHandler(DeliveryLog log) : IRequestHandler<PingCommand, Unit>
{
    public Task<Unit> Handle(PingCommand request, CancellationToken cancellationToken)
    {
        log.RecordDelivery($"ping:{request.Name}");
        return Task.FromResult(Unit.Value);
    }
}

public sealed class WriteAndCascadeHandler(ISqlTransactionProvider transactions, IQueue queue, SqlTestDialect dialect)
    : IRequestHandler<WriteAndCascadeCommand, Unit>
{
    public async Task<Unit> Handle(WriteAndCascadeCommand request, CancellationToken cancellationToken)
    {
        await dialect.InsertSideEffect(SideEffects.Ambient(transactions), request.Table, "written", cancellationToken);
        await queue.Enqueue(new PingCommand(request.Name), ct: cancellationToken);
        return Unit.Value;
    }
}

public sealed class WriteThenFailHandler(ISqlTransactionProvider transactions, SqlTestDialect dialect)
    : IRequestHandler<WriteThenFailCommand, Unit>
{
    public async Task<Unit> Handle(WriteThenFailCommand request, CancellationToken cancellationToken)
    {
        await dialect.InsertSideEffect(SideEffects.Ambient(transactions), request.Table, "written", cancellationToken);
        throw new QueueHandlerPermanentException("Failing after the side-effect write");
    }
}

public sealed class FailOnceHandler(DeliveryLog log) : IRequestHandler<FailOnceCommand, Unit>
{
    public Task<Unit> Handle(FailOnceCommand request, CancellationToken cancellationToken)
    {
        if (log.RecordAttempt(request.Key) == 1)
            throw new InvalidOperationException($"Transient failure for {request.Key}");

        log.RecordDelivery($"failonce:{request.Key}");
        return Task.FromResult(Unit.Value);
    }
}

internal static class SideEffects
{
    public static System.Data.Common.DbTransaction Ambient(ISqlTransactionProvider transactions) =>
        transactions.Current ?? throw new InvalidOperationException("Expected the delivery's ambient transaction.");
}

// Cloud Tasks delivery over SQL storage, with a fake Cloud Tasks and an in-memory endpoint.
// The relay never runs on its own, so tests decide when a pass happens.

public sealed class CloudTasksOverSqlHost : IAsyncDisposable
{
    public const string DispatcherUrl = "https://worker.test/internal/queue/dispatch";

    private readonly IHost _host;

    private CloudTasksOverSqlHost(
        IHost host, SqlTestDialect dialect, string suffix, FakeCloudTasksClient tasks, FixedTimeProvider clock)
    {
        _host = host;
        Dialect = dialect;
        Suffix = suffix;
        Tasks = tasks;
        Clock = clock;
        Storage = dialect.StorageOptions(host.Services);
    }

    public SqlTestDialect Dialect { get; }
    public string Suffix { get; }
    public FakeCloudTasksClient Tasks { get; }
    public FixedTimeProvider Clock { get; }
    public SqlStorageOptions Storage { get; }
    public IServiceProvider Services => _host.Services;
    public IQueue Queue => Services.GetRequiredService<IQueue>();
    public IQueueRelay Relay => Services.GetRequiredService<IQueueRelay>();
    public DeliveryLog Log => Services.GetRequiredService<DeliveryLog>();
    public string QueueTable => Storage.QueueTableName;
    public string DeadLetterTable => Storage.DeadLetterTableName;

    public static async Task<CloudTasksOverSqlHost> StartAsync(
        SqlTestDialect dialect,
        Action<IServiceCollection, IConfiguration>? configure = null,
        Dictionary<string, string?>? extraConfig = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var settings = new Dictionary<string, string?>
        {
            [$"{dialect.StorageSection}:ConnectionString"] = dialect.ConnectionString,
            [$"{dialect.StorageSection}:QueueTableName"] = $"Q_{suffix}",
            [$"{dialect.StorageSection}:DeadLetterTableName"] = $"DL_{suffix}",
            ["Queue:CloudTasks:ProjectId"] = "test-project",
            ["Queue:CloudTasks:LocationId"] = "europe-north1",
            ["Queue:CloudTasks:Channels:default"] = "default-queue",
            ["Queue:CloudTasks:DispatcherUrl"] = DispatcherUrl,
            ["Queue:CloudTasks:ServiceAccountEmail"] = "tasks@test-project.iam.gserviceaccount.com",
            ["Queue:CloudTasks:Relay:RunInBackground"] = "false",
            [$"{dialect.SchedulerSection}:TableName"] = $"Jobs_{suffix}",
        };
        foreach (var (key, value) in extraConfig ?? [])
            settings[key] = value;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var tasks = new FakeCloudTasksClient();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);

        var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddLogging(logging => logging.ClearProviders());
                    services.AddRouting();
                    services.AddSingleton<DeliveryLog>();
                    services.AddSingleton(dialect);
                    services.AddSingleton<TimeProvider>(clock);
                    services.AddMediator([typeof(CloudTasksOverSqlHost).Assembly]);
                    services.AddQueue(q => dialect
                        .UseStorage(q.AddRequestAssemblies(typeof(CloudTasksOverSqlHost).Assembly), configuration)
                        .UseCloudTasksDelivery(configuration));
                    services.AddSingleton<CloudTasksClient>(tasks);
                    // OIDC validation is covered by the Cloud Tasks suite.
                    services.PostConfigure<AuthorizationOptions>(o =>
                        o.AddPolicy(CloudTasksAuth.PolicyName, p => p.RequireAssertion(_ => true)));
                    configure?.Invoke(services, configuration);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapCloudTasksDispatch());
                }))
            .StartAsync();

        return new CloudTasksOverSqlHost(host, dialect, suffix, tasks, clock);
    }

    public async Task<HttpStatusCode> Deliver(CreateTaskRequest task)
    {
        using var client = _host.GetTestClient();
        using var request = new HttpRequestMessage(System.Net.Http.HttpMethod.Post, DispatcherUrl)
        {
            Content = new StringContent(task.Task.HttpRequest.Body.ToStringUtf8(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-CloudTasks-TaskName", task.Task.Name);

        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    public static QueueEnvelope EnvelopeOf(CreateTaskRequest task) =>
        JsonSerializer.Deserialize<QueueEnvelope>(task.Task.HttpRequest.Body.ToByteArray())!;

    public static string TaskIdOf(CreateTaskRequest task) => task.Task.Name[(task.Task.Name.LastIndexOf('/') + 1)..];

    public Task<System.Data.Common.DbConnection> OpenConnectionAsync() => Dialect.OpenConnectionAsync();

    public Task<T> ScalarAsync<T>(string sql) => Dialect.ScalarAsync<T>(sql);

    public Task ExecuteAsync(string sql) => Dialect.ExecuteAsync(sql);

    public Task<int> OutboxCount() => Dialect.CountAsync(QueueTable);

    public Task<int> DeadLetterCount() => Dialect.CountAsync(DeadLetterTable);

    public async Task<string> CreateSideTable()
    {
        var table = $"Side_{Suffix}_{Guid.NewGuid().ToString("N")[..6]}";
        await Dialect.CreateSideTable(table);
        return table;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}
