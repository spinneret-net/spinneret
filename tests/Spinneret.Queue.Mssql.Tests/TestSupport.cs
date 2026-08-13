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
using Spinneret.Queue;
using Testcontainers.MsSql;
using TUnit.Core.Interfaces;

namespace Spinneret.Queue.Mssql.Tests;

// ---------------------------------------------------------------------------------------------
// Test command types scanned by QueueTypeRegistry from this assembly, with mediator handlers
// recording deliveries into the DeliveryLog. All timing-relevant policies use millisecond
// backoffs so retry scenarios complete in test time.
// ---------------------------------------------------------------------------------------------

public sealed record PingCommand(string Name) : IRequest<Unit>;

[QueuePolicy(MinBackoff = "00:00:00.050", MaxBackoff = "00:00:00.100")]
public sealed record FailTwiceCommand(string Key) : IRequest<Unit>;

[QueuePolicy(MaxAttempts = 2, MinBackoff = "00:00:00.050", MaxBackoff = "00:00:00.100")]
public sealed record AlwaysFailCommand(string Key) : IRequest<Unit>;

public sealed record PermanentFailCommand(string Key) : IRequest<Unit>;

public sealed record DeferOnceCommand(string Key) : IRequest<Unit>;

/// <summary>Writes a row to <paramref name="Table"/> on the ambient transaction, then fails permanently.</summary>
public sealed record WriteThenFailCommand(string Table) : IRequest<Unit>;

public sealed record WriteCommand(string Table) : IRequest<Unit>;

/// <summary>Enqueues a <see cref="PingCommand"/> and then fails permanently.</summary>
public sealed record CascadeThenFailCommand(string Key) : IRequest<Unit>;

public sealed record CascadeCommand(string Key) : IRequest<Unit>;

[QueuePolicy(Channel = "bulk")]
public sealed record BulkChannelCommand(string Name) : IRequest<Unit>;

// ---------------------------------------------------------------------------------------------
// Handlers.
// ---------------------------------------------------------------------------------------------

public sealed class PingHandler(DeliveryLog log) : IRequestHandler<PingCommand, Unit>
{
    public Task<Unit> Handle(PingCommand request, CancellationToken cancellationToken)
    {
        log.RecordDelivery($"ping:{request.Name}");
        return Task.FromResult(Unit.Value);
    }
}

public sealed class FailTwiceHandler(DeliveryLog log) : IRequestHandler<FailTwiceCommand, Unit>
{
    public Task<Unit> Handle(FailTwiceCommand request, CancellationToken cancellationToken)
    {
        if (log.RecordAttempt(request.Key) <= 2)
            throw new InvalidOperationException($"Transient failure for {request.Key}");

        log.RecordDelivery($"failtwice:{request.Key}");
        return Task.FromResult(Unit.Value);
    }
}

public sealed class AlwaysFailHandler(DeliveryLog log) : IRequestHandler<AlwaysFailCommand, Unit>
{
    public Task<Unit> Handle(AlwaysFailCommand request, CancellationToken cancellationToken)
    {
        log.RecordAttempt(request.Key);
        throw new InvalidOperationException($"Permanent trouble for {request.Key}");
    }
}

public sealed class PermanentFailHandler(DeliveryLog log) : IRequestHandler<PermanentFailCommand, Unit>
{
    public Task<Unit> Handle(PermanentFailCommand request, CancellationToken cancellationToken)
    {
        log.RecordAttempt(request.Key);
        throw new QueueHandlerPermanentException($"Unrecoverable for {request.Key}");
    }
}

public sealed class DeferOnceHandler(DeliveryLog log) : IRequestHandler<DeferOnceCommand, Unit>
{
    public Task<Unit> Handle(DeferOnceCommand request, CancellationToken cancellationToken)
    {
        if (log.RecordAttempt(request.Key) == 1)
            throw new QueueHandlerRetryAfterException(TimeSpan.FromMilliseconds(50));

        log.RecordDelivery($"defer:{request.Key}");
        return Task.FromResult(Unit.Value);
    }
}

public sealed class WriteThenFailHandler(IMssqlTransactionProvider transactions)
    : IRequestHandler<WriteThenFailCommand, Unit>
{
    public async Task<Unit> Handle(WriteThenFailCommand request, CancellationToken cancellationToken)
    {
        await SideEffects.Insert(transactions, request.Table, cancellationToken);
        throw new QueueHandlerPermanentException("Failing after the side-effect write");
    }
}

public sealed class WriteHandler(IMssqlTransactionProvider transactions) : IRequestHandler<WriteCommand, Unit>
{
    public async Task<Unit> Handle(WriteCommand request, CancellationToken cancellationToken)
    {
        await SideEffects.Insert(transactions, request.Table, cancellationToken);
        return Unit.Value;
    }
}

public sealed class CascadeThenFailHandler(IQueue queue) : IRequestHandler<CascadeThenFailCommand, Unit>
{
    public async Task<Unit> Handle(CascadeThenFailCommand request, CancellationToken cancellationToken)
    {
        await queue.Enqueue(new PingCommand($"cascade-{request.Key}"), ct: cancellationToken);
        throw new QueueHandlerPermanentException("Failing after the cascade enqueue");
    }
}

public sealed class CascadeHandler(IQueue queue) : IRequestHandler<CascadeCommand, Unit>
{
    public async Task<Unit> Handle(CascadeCommand request, CancellationToken cancellationToken)
    {
        await queue.Enqueue(new PingCommand($"cascade-{request.Key}"), ct: cancellationToken);
        return Unit.Value;
    }
}

public sealed class BulkChannelHandler(DeliveryLog log) : IRequestHandler<BulkChannelCommand, Unit>
{
    public Task<Unit> Handle(BulkChannelCommand request, CancellationToken cancellationToken)
    {
        log.RecordDelivery($"bulk:{request.Name}");
        return Task.FromResult(Unit.Value);
    }
}

internal static class SideEffects
{
    public static async Task Insert(IMssqlTransactionProvider transactions, string table, CancellationToken ct)
    {
        var transaction = transactions.Current
            ?? throw new InvalidOperationException("Expected the delivery's ambient transaction.");
        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO [{table}] (Value) VALUES ('written');";
        await command.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>Thread-safe record of handler activity, shared through DI as a singleton.</summary>
public sealed class DeliveryLog
{
    private readonly ConcurrentDictionary<string, int> _attempts = new();
    private readonly ConcurrentQueue<string> _deliveries = new();

    public int RecordAttempt(string key) => _attempts.AddOrUpdate(key, 1, (_, n) => n + 1);
    public void RecordDelivery(string tag) => _deliveries.Enqueue(tag);

    public int Attempts(string key) => _attempts.GetValueOrDefault(key);
    public IReadOnlyCollection<string> Deliveries => [.. _deliveries];
    public int DeliveryCount(string tag) => _deliveries.Count(d => d == tag);
}

// ---------------------------------------------------------------------------------------------
// Docker fixture and host harness.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// One SQL Server container for the whole test session; each test isolates itself with unique
/// table names inside the shared database.
/// </summary>
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
/// A running queue host: DI container with the Mssql queue registered and all hosted services
/// started, plus SQL helpers scoped to this test's unique tables.
/// </summary>
public sealed class QueueTestHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IHostedService[] _hostedServices;

    public string Suffix { get; }
    public MssqlQueueOptions Options { get; }
    public DeliveryLog Log { get; }

    private QueueTestHost(ServiceProvider provider, IHostedService[] hostedServices, string suffix)
    {
        _provider = provider;
        _hostedServices = hostedServices;
        Suffix = suffix;
        Options = provider.GetRequiredService<IOptions<MssqlQueueOptions>>().Value;
        Log = provider.GetRequiredService<DeliveryLog>();
    }

    public IServiceProvider Services => _provider;
    public IQueue Queue => _provider.GetRequiredService<IQueue>();
    public IMssqlTransactionalQueue TransactionalQueue => _provider.GetRequiredService<IMssqlTransactionalQueue>();
    public IMssqlTransactionProvider Transactions => _provider.GetRequiredService<IMssqlTransactionProvider>();

    public static async Task<QueueTestHost> StartAsync(
        string connectionString,
        bool worker = true,
        Dictionary<string, string?>? extraConfig = null,
        Action<IServiceCollection>? configure = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var settings = new Dictionary<string, string?>
        {
            ["Queue:Mssql:ConnectionString"] = connectionString,
            ["Queue:Mssql:QueueTableName"] = $"Q_{suffix}",
            ["Queue:Mssql:DeadLetterTableName"] = $"DL_{suffix}",
            ["Queue:Mssql:PollInterval"] = "00:00:00.050",
        };
        foreach (var (key, value) in extraConfig ?? [])
            settings[key] = value;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<DeliveryLog>();
        services.AddMediator(typeof(QueueTestHost).Assembly);
        services.AddMssqlQueue(configuration, typeof(QueueTestHost).Assembly);
        if (worker)
            services.AddMssqlQueueWorker();
        configure?.Invoke(services);

        var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToArray();
        foreach (var hostedService in hostedServices)
            await hostedService.StartAsync(CancellationToken.None);

        return new QueueTestHost(provider, hostedServices, suffix);
    }

    // ------------------------------------------------------------------------- SQL helpers ---

    public async Task<SqlConnection> OpenConnectionAsync()
    {
        var connection = new SqlConnection(Options.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)(await command.ExecuteScalarAsync())!;
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public Task<int> QueueRowCount() =>
        ScalarAsync<int>($"SELECT COUNT(*) FROM [{Options.QueueTableName}]");

    public Task<int> DeadLetterRowCount() =>
        ScalarAsync<int>($"SELECT COUNT(*) FROM [{Options.DeadLetterTableName}]");

    public async ValueTask DisposeAsync()
    {
        foreach (var hostedService in _hostedServices.Reverse())
            await hostedService.StopAsync(CancellationToken.None);
        await _provider.DisposeAsync();
    }
}

internal static class Wait
{
    /// <summary>Polls until <paramref name="condition"/> holds; fails the test on timeout.</summary>
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
