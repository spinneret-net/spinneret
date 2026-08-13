using Spinneret.Functional;
using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Spinneret.Mediator;
using Spinneret.Queue.Mssql;
using Testcontainers.MsSql;
using TUnit.Core.Interfaces;

namespace Spinneret.Scheduler.Mssql.Tests;

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

public sealed class DeliveryLog
{
    private readonly ConcurrentQueue<string> _deliveries = new();

    public void RecordDelivery(string tag) => _deliveries.Enqueue(tag);
    public int DeliveryCount(string tag) => _deliveries.Count(d => d == tag);
}

// ---------------------------------------------------------------------------------------------
// Docker fixture and host harness.
// ---------------------------------------------------------------------------------------------

public sealed class MssqlContainerFixture : IAsyncInitializer, IAsyncDisposable
{
    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

/// <summary>
/// A running scheduler host: queue + scheduler registered against test-unique tables, with the
/// queue worker and the scheduler sweeper active (unless disabled per test).
/// </summary>
public sealed class SchedulerTestHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IHostedService[] _hostedServices;

    public string Suffix { get; }
    public MssqlQueueOptions QueueOptions { get; }
    public MssqlSchedulerOptions SchedulerOptions { get; }
    public DeliveryLog Log { get; }

    private SchedulerTestHost(ServiceProvider provider, IHostedService[] hostedServices, string suffix)
    {
        _provider = provider;
        _hostedServices = hostedServices;
        Suffix = suffix;
        QueueOptions = provider.GetRequiredService<IOptions<MssqlQueueOptions>>().Value;
        SchedulerOptions = provider.GetRequiredService<IOptions<MssqlSchedulerOptions>>().Value;
        Log = provider.GetRequiredService<DeliveryLog>();
    }

    public IServiceProvider Services => _provider;
    public IRecurringJobScheduler Scheduler => _provider.GetRequiredService<IRecurringJobScheduler>();
    public IMssqlTransactionalScheduler TransactionalScheduler =>
        _provider.GetRequiredService<IMssqlTransactionalScheduler>();

    public static async Task<SchedulerTestHost> StartAsync(
        string connectionString,
        bool sweeper = true,
        Action<IServiceCollection>? configure = null,
        string? reuseSuffix = null)
    {
        var suffix = reuseSuffix ?? Guid.NewGuid().ToString("N")[..12];
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Queue:Mssql:ConnectionString"] = connectionString,
            ["Queue:Mssql:QueueTableName"] = $"Q_{suffix}",
            ["Queue:Mssql:DeadLetterTableName"] = $"DL_{suffix}",
            ["Queue:Mssql:PollInterval"] = "00:00:00.050",
            ["Scheduler:Mssql:TableName"] = $"Jobs_{suffix}",
            ["Scheduler:Mssql:SweepInterval"] = "00:00:00.100",
        }).Build();

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<DeliveryLog>();
        services.AddMediator(typeof(SchedulerTestHost).Assembly);
        services.AddMssqlQueue(configuration, typeof(SchedulerTestHost).Assembly);
        services.AddMssqlQueueWorker();
        services.AddMssqlScheduler(configuration);
        if (sweeper)
            services.AddMssqlSchedulerSweeper();
        configure?.Invoke(services);

        var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToArray();
        foreach (var hostedService in hostedServices)
            await hostedService.StartAsync(CancellationToken.None);

        return new SchedulerTestHost(provider, hostedServices, suffix);
    }

    // ------------------------------------------------------------------------- SQL helpers ---

    public string JobsTable => SchedulerOptions.TableName;

    public async Task<SqlConnection> OpenConnectionAsync()
    {
        var connection = new SqlConnection(QueueOptions.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is DBNull or null ? default! : (T)value;
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public Task<string> JobStatus(string jobKey) =>
        ScalarAsync<string>($"SELECT Status FROM [{JobsTable}] WHERE JobKey = N'{jobKey}'");

    public Task<DateTime> JobNextExecuteAt(string jobKey) =>
        ScalarAsync<DateTime>($"SELECT NextExecuteAt FROM [{JobsTable}] WHERE JobKey = N'{jobKey}'");

    public Task<int> DeadLetterCount() =>
        ScalarAsync<int>($"SELECT COUNT(*) FROM [{QueueOptions.DeadLetterTableName}]");

    public async ValueTask DisposeAsync()
    {
        foreach (var hostedService in _hostedServices.Reverse())
            await hostedService.StopAsync(CancellationToken.None);
        await _provider.DisposeAsync();
    }
}

internal static class Wait
{
    public static async Task Until(Func<Task<bool>> condition, string because, int timeoutSeconds = 20)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return;
            await Task.Delay(25);
        }

        throw new TimeoutException($"Timed out waiting for: {because}");
    }

    public static Task Until(Func<bool> condition, string because, int timeoutSeconds = 20) =>
        Until(() => Task.FromResult(condition()), because, timeoutSeconds);
}
