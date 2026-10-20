using Microsoft.Extensions.Configuration;
using Npgsql;
using Spinneret.Queue;
using Spinneret.Queue.Postgres;
using Spinneret.Queue.Sql;

// ReSharper disable once CheckNamespace — DI namespace for discoverability
namespace Microsoft.Extensions.DependencyInjection;

public static class PostgresQueueBuilderExtensions
{
    private static readonly SqlStorageDescriptor Descriptor = new(
        nameof(UsePostgresStorage),
        PostgresStorageOptions.SectionName,
        PostgresQueueDialect.Instance,
        connectionString => NpgsqlDataSource.Create(connectionString));

    /// <summary>Stores the queue in PostgreSQL, configured from <c>Queue:Postgres</c>.</summary>
    /// <remarks>Enqueues join the ambient transaction, committing atomically with the business write.</remarks>
    public static QueueBuilder UsePostgresStorage(this QueueBuilder queue, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return queue.UseSqlStorage<PostgresStorageOptions>(Descriptor, configuration, configure: null, dataSource: null);
    }

    /// <summary>As <see cref="UsePostgresStorage(QueueBuilder, IConfiguration)"/>, then adjusted by <paramref name="configure"/>.</summary>
    public static QueueBuilder UsePostgresStorage(
        this QueueBuilder queue, IConfiguration configuration, Action<PostgresStorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configure);
        return queue.UseSqlStorage(Descriptor, configuration, configure, dataSource: null);
    }

    /// <summary>As <see cref="UsePostgresStorage(QueueBuilder, IConfiguration)"/>, configured in code only.</summary>
    public static QueueBuilder UsePostgresStorage(this QueueBuilder queue, Action<PostgresStorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return queue.UseSqlStorage(Descriptor, configuration: null, configure, dataSource: null);
    }

    /// <summary>As <see cref="UsePostgresStorage(QueueBuilder, IConfiguration)"/>, using a host-owned <see cref="NpgsqlDataSource"/> it never disposes.</summary>
    public static QueueBuilder UsePostgresStorage(
        this QueueBuilder queue, Func<IServiceProvider, NpgsqlDataSource> dataSource, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(configuration);
        return queue.UseSqlStorage<PostgresStorageOptions>(Descriptor, configuration, configure: null, dataSource);
    }

    /// <summary>As <see cref="UsePostgresStorage(QueueBuilder, Func{IServiceProvider, NpgsqlDataSource}, IConfiguration)"/>, configured in code only.</summary>
    public static QueueBuilder UsePostgresStorage(
        this QueueBuilder queue, Func<IServiceProvider, NpgsqlDataSource> dataSource, Action<PostgresStorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(configure);
        return queue.UseSqlStorage(Descriptor, configuration: null, configure, dataSource);
    }
}
