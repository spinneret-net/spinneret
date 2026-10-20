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
using Spinneret.Queue.Sql;

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

public sealed class WriteThenFailHandler(ISqlTransactionProvider transactions)
    : IRequestHandler<WriteThenFailCommand, Unit>
{
    public async Task<Unit> Handle(WriteThenFailCommand request, CancellationToken cancellationToken)
    {
        await SideEffects.Insert(transactions, request.Table, cancellationToken);
        throw new QueueHandlerPermanentException("Failing after the side-effect write");
    }
}

public sealed class WriteHandler(ISqlTransactionProvider transactions) : IRequestHandler<WriteCommand, Unit>
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
    public static async Task Insert(ISqlTransactionProvider transactions, string table, CancellationToken ct)
    {
        var transaction = transactions.Current
            ?? throw new InvalidOperationException("Expected the delivery's ambient transaction.");
        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO [{table}] (Value) VALUES ('written');";
        await command.ExecuteNonQueryAsync(ct);
    }
}

// ---------------------------------------------------------------------------------------------
// Docker fixture and host harness.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// A running queue host: DI container with the Mssql queue registered and all hosted services
/// started, plus SQL helpers scoped to this test's unique tables.
/// </summary>
public sealed class QueueTestHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IHostedService[] _hostedServices;

    public string Suffix { get; }
    public MssqlStorageOptions Options { get; }
    public DeliveryLog Log { get; }

    private QueueTestHost(ServiceProvider provider, IHostedService[] hostedServices, string suffix)
    {
        _provider = provider;
        _hostedServices = hostedServices;
        Suffix = suffix;
        Options = provider.GetRequiredService<IOptions<MssqlStorageOptions>>().Value;
        Log = provider.GetRequiredService<DeliveryLog>();
    }

    public IServiceProvider Services => _provider;
    public IQueue Queue => _provider.GetRequiredService<IQueue>();
    public ISqlTransactionalQueue TransactionalQueue => _provider.GetRequiredService<ISqlTransactionalQueue>();
    public ISqlTransactionProvider Transactions => _provider.GetRequiredService<ISqlTransactionProvider>();

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
            ["Queue:InHost:PollInterval"] = "00:00:00.050",
        };
        foreach (var (key, value) in extraConfig ?? [])
            settings[key] = value;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<DeliveryLog>();
        services.AddMediator([typeof(QueueTestHost).Assembly]);
        services.AddQueue(q =>
        {
            q.AddRequestAssemblies(typeof(QueueTestHost).Assembly).UseMssqlStorage(configuration);
            if (worker)
                q.UseInHostDelivery(configuration);
        });
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
