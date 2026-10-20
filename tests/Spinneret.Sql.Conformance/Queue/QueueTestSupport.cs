using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spinneret.Functional;
using Spinneret.Mediator;
using Spinneret.Queue;
using Spinneret.Queue.Sql;

namespace Spinneret.Sql.Conformance;

// Retry policies use millisecond backoffs so retry scenarios complete in test time.

public sealed record PingCommand(string Name) : IRequest<Unit>;

[QueuePolicy(MinBackoff = "00:00:00.050", MaxBackoff = "00:00:00.100")]
public sealed record FailTwiceCommand(string Key) : IRequest<Unit>;

[QueuePolicy(MaxAttempts = 2, MinBackoff = "00:00:00.050", MaxBackoff = "00:00:00.100")]
public sealed record AlwaysFailCommand(string Key) : IRequest<Unit>;

public sealed record PermanentFailCommand(string Key) : IRequest<Unit>;

public sealed record DeferOnceCommand(string Key) : IRequest<Unit>;

public sealed record WriteThenFailCommand(string Table) : IRequest<Unit>;

public sealed record WriteCommand(string Table) : IRequest<Unit>;

// Swallows a failed statement: SQL Server undoes only that statement, Postgres aborts the transaction.
[QueuePolicy(MaxAttempts = 2, MinBackoff = "00:00:00.050", MaxBackoff = "00:00:00.100")]
public sealed record SwallowSqlErrorCommand(string Table) : IRequest<Unit>;

public sealed record CascadeThenFailCommand(string Key) : IRequest<Unit>;

public sealed record CascadeCommand(string Key) : IRequest<Unit>;

[QueuePolicy(Channel = "bulk")]
public sealed record BulkChannelCommand(string Name) : IRequest<Unit>;

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

public sealed class WriteThenFailHandler(ISqlTransactionProvider transactions, SqlTestDialect dialect)
    : IRequestHandler<WriteThenFailCommand, Unit>
{
    public async Task<Unit> Handle(WriteThenFailCommand request, CancellationToken cancellationToken)
    {
        await dialect.InsertSideEffect(SideEffects.Ambient(transactions), request.Table, "written", cancellationToken);
        throw new QueueHandlerPermanentException("Failing after the side-effect write");
    }
}

public sealed class WriteHandler(ISqlTransactionProvider transactions, SqlTestDialect dialect)
    : IRequestHandler<WriteCommand, Unit>
{
    public async Task<Unit> Handle(WriteCommand request, CancellationToken cancellationToken)
    {
        await dialect.InsertSideEffect(SideEffects.Ambient(transactions), request.Table, "written", cancellationToken);
        return Unit.Value;
    }
}

public sealed class SwallowSqlErrorHandler(ISqlTransactionProvider transactions, SqlTestDialect dialect, DeliveryLog log)
    : IRequestHandler<SwallowSqlErrorCommand, Unit>
{
    public async Task<Unit> Handle(SwallowSqlErrorCommand request, CancellationToken cancellationToken)
    {
        log.RecordAttempt(request.Table);
        var transaction = SideEffects.Ambient(transactions);
        await dialect.InsertSideEffect(transaction, request.Table, "written", cancellationToken);
        try
        {
            await dialect.InsertSideEffect(transaction, request.Table, value: null, cancellationToken);
        }
        catch (System.Data.Common.DbException)
        {
            // Deliberately swallowed: the bug under test is a handler that does this.
        }

        log.RecordDelivery($"swallow:{request.Table}");
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
    public static System.Data.Common.DbTransaction Ambient(ISqlTransactionProvider transactions) =>
        transactions.Current ?? throw new InvalidOperationException("Expected the delivery's ambient transaction.");
}

public sealed class QueueTestHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IHostedService[] _hostedServices;

    private QueueTestHost(ServiceProvider provider, IHostedService[] hostedServices, SqlTestDialect dialect, string suffix)
    {
        _provider = provider;
        _hostedServices = hostedServices;
        Dialect = dialect;
        Suffix = suffix;
        Options = dialect.StorageOptions(provider);
        Log = provider.GetRequiredService<DeliveryLog>();
    }

    public SqlTestDialect Dialect { get; }
    public string Suffix { get; }
    public SqlStorageOptions Options { get; }
    public DeliveryLog Log { get; }

    public IServiceProvider Services => _provider;
    public IQueue Queue => _provider.GetRequiredService<IQueue>();
    public ISqlTransactionalQueue TransactionalQueue => _provider.GetRequiredService<ISqlTransactionalQueue>();
    public ISqlTransactionProvider Transactions => _provider.GetRequiredService<ISqlTransactionProvider>();

    // Pins a host to a given pair of tables, so several hosts can share them.
    public static Dictionary<string, string?> Tables(SqlTestDialect dialect, string suffix) => new()
    {
        [$"{dialect.StorageSection}:QueueTableName"] = $"Q_{suffix}",
        [$"{dialect.StorageSection}:DeadLetterTableName"] = $"DL_{suffix}",
    };

    public static string NewSuffix() => Guid.NewGuid().ToString("N")[..12];

    public static async Task<QueueTestHost> StartAsync(
        SqlTestDialect dialect,
        bool worker = true,
        Dictionary<string, string?>? extraConfig = null,
        Action<IServiceCollection>? configure = null,
        string? suffix = null)
    {
        suffix ??= NewSuffix();
        var settings = new Dictionary<string, string?>
        {
            [$"{dialect.StorageSection}:ConnectionString"] = dialect.ConnectionString,
            ["Queue:InHost:PollInterval"] = "00:00:00.050",
        };
        foreach (var (key, value) in Tables(dialect, suffix))
            settings[key] = value;
        foreach (var (key, value) in extraConfig ?? [])
            settings[key] = value;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<DeliveryLog>();
        services.AddSingleton(dialect);
        services.AddMediator([typeof(QueueTestHost).Assembly]);
        services.AddQueue(q =>
        {
            dialect.UseStorage(q.AddRequestAssemblies(typeof(QueueTestHost).Assembly), configuration);
            if (worker)
                q.UseInHostDelivery(configuration);
        });
        configure?.Invoke(services);

        var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToArray();
        foreach (var hostedService in hostedServices)
            await hostedService.StartAsync(CancellationToken.None);

        return new QueueTestHost(provider, hostedServices, dialect, suffix);
    }

    public string QueueTable => Options.QueueTableName;
    public string DeadLetterTable => Options.DeadLetterTableName;

    public Task<System.Data.Common.DbConnection> OpenConnectionAsync() => Dialect.OpenConnectionAsync();

    public Task<T> ScalarAsync<T>(string sql) => Dialect.ScalarAsync<T>(sql);

    public Task ExecuteAsync(string sql) => Dialect.ExecuteAsync(sql);

    public Task<int> QueueRowCount() => Dialect.CountAsync(QueueTable);

    public Task<int> DeadLetterRowCount() => Dialect.CountAsync(DeadLetterTable);

    public async ValueTask DisposeAsync()
    {
        foreach (var hostedService in _hostedServices.Reverse())
            await hostedService.StopAsync(CancellationToken.None);
        await _provider.DisposeAsync();
    }
}
