using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spinneret.Functional;
using Spinneret.Mediator;
using Spinneret.Queue.Sql;
using Spinneret.Scheduler;
using Spinneret.Scheduler.Sql;

namespace Spinneret.Sql.Conformance;

// ---------------------------------------------------------------------------------------------
// Test command types and handlers.
// ---------------------------------------------------------------------------------------------

public sealed record TickCommand(string Name) : IRequest<Unit>;

public sealed class TickHandler(DeliveryLog log) : IRequestHandler<TickCommand, Unit>
{
    public Task<Unit> Handle(TickCommand request, CancellationToken cancellationToken)
    {
        log.RecordDelivery($"tick:{request.Name}");
        return Task.FromResult(Unit.Value);
    }
}

/// <summary>
/// A request whose handler returns a value. A scheduled run discards it — the response type is here
/// to prove a job is not restricted to <c>IRequest&lt;Unit&gt;</c>, and that the sweep can enqueue a
/// stored job whose response type it only learns at runtime.
/// </summary>
public sealed record ReportCommand(string Name) : IRequest<string>;

public sealed class ReportHandler(DeliveryLog log) : IRequestHandler<ReportCommand, string>
{
    public Task<string> Handle(ReportCommand request, CancellationToken cancellationToken)
    {
        log.RecordDelivery($"report:{request.Name}");
        return Task.FromResult($"report for {request.Name}");
    }
}

// ---------------------------------------------------------------------------------------------
// Host harness.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// A running scheduler host: queue + scheduler registered against test-unique tables, with the
/// queue worker and the scheduler sweeper active (unless disabled per test).
/// </summary>
public sealed class SchedulerTestHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IHostedService[] _hostedServices;

    private SchedulerTestHost(ServiceProvider provider, IHostedService[] hostedServices, SqlTestDialect dialect, string suffix)
    {
        _provider = provider;
        _hostedServices = hostedServices;
        Dialect = dialect;
        Suffix = suffix;
        QueueOptions = dialect.StorageOptions(provider);
        SchedulerOptions = dialect.SchedulerOptions(provider);
        Log = provider.GetRequiredService<DeliveryLog>();
    }

    public SqlTestDialect Dialect { get; }
    public string Suffix { get; }
    public SqlStorageOptions QueueOptions { get; }
    public SqlSchedulerOptions SchedulerOptions { get; }
    public DeliveryLog Log { get; }

    public IServiceProvider Services => _provider;
    public IRecurringJobScheduler Scheduler => _provider.GetRequiredService<IRecurringJobScheduler>();
    public ISqlTransactionalScheduler TransactionalScheduler =>
        _provider.GetRequiredService<ISqlTransactionalScheduler>();

    public static async Task<SchedulerTestHost> StartAsync(
        SqlTestDialect dialect,
        bool sweeper = true,
        Action<IServiceCollection>? configure = null,
        string? reuseSuffix = null)
    {
        var suffix = reuseSuffix ?? Guid.NewGuid().ToString("N")[..12];
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{dialect.StorageSection}:ConnectionString"] = dialect.ConnectionString,
            [$"{dialect.StorageSection}:QueueTableName"] = $"Q_{suffix}",
            [$"{dialect.StorageSection}:DeadLetterTableName"] = $"DL_{suffix}",
            ["Queue:InHost:PollInterval"] = "00:00:00.050",
            [$"{dialect.SchedulerSection}:TableName"] = $"Jobs_{suffix}",
        }).Build();

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<DeliveryLog>();
        services.AddMediator([typeof(SchedulerTestHost).Assembly]);
        services.AddQueue(q => dialect
            .UseStorage(q.AddRequestAssemblies(typeof(SchedulerTestHost).Assembly), configuration)
            .UseInHostDelivery(configuration));
        services.AddScheduler(s =>
        {
            dialect.UseScheduler(s, configuration);
            if (sweeper)
                // Fast ticks so integration tests do not wait out the 15s default.
                s.UseSweeper(o => o.SweepInterval = TimeSpan.FromMilliseconds(100));
        });
        configure?.Invoke(services);

        var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToArray();
        foreach (var hostedService in hostedServices)
            await hostedService.StartAsync(CancellationToken.None);

        return new SchedulerTestHost(provider, hostedServices, dialect, suffix);
    }

    // ------------------------------------------------------------------------- SQL helpers ---

    public string JobsTable => SchedulerOptions.TableName;

    public Task<System.Data.Common.DbConnection> OpenConnectionAsync() => Dialect.OpenConnectionAsync();

    public Task<T> ScalarAsync<T>(string sql) => Dialect.ScalarAsync<T>(sql);

    public Task ExecuteAsync(string sql) => Dialect.ExecuteAsync(sql);

    /// <summary>
    /// Whether the job is still outstanding work. There is no status column — a job is deleted once
    /// it has run, been cancelled, or had its failure dead-lettered — so existence is the state.
    /// </summary>
    public async Task<bool> JobExists(string jobKey) =>
        await Dialect.CountAsync(JobsTable, $"JobKey = '{jobKey}'") == 1;

    public Task<DateTime> JobNextExecuteAt(string jobKey) =>
        ScalarAsync<DateTime>($"SELECT NextExecuteAt FROM {Dialect.Quote(JobsTable)} WHERE JobKey = '{jobKey}'");

    public Task<int> DeadLetterCount() => Dialect.CountAsync(QueueOptions.DeadLetterTableName);

    public async ValueTask DisposeAsync()
    {
        foreach (var hostedService in _hostedServices.Reverse())
            await hostedService.StopAsync(CancellationToken.None);
        await _provider.DisposeAsync();
    }
}
