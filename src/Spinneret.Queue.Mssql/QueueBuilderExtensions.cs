using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Spinneret.Queue;
using Spinneret.Queue.Mssql;
using Spinneret.Queue.Sql;

// ReSharper disable once CheckNamespace — deliberate: registration extensions live in the
// DI namespace so every Use* call is discoverable without a using directive.
namespace Microsoft.Extensions.DependencyInjection;

public static class MssqlQueueBuilderExtensions
{
    private static readonly SqlStorageDescriptor Descriptor = new(
        nameof(UseMssqlStorage),
        MssqlStorageOptions.SectionName,
        MssqlQueueDialect.Instance,
        SqlClientFactory.Instance.CreateDataSource);

    /// <summary>
    /// Stores the queue in SQL Server, in the application's own database: every enqueue is an insert
    /// that joins the ambient (or a supplied) transaction, so it commits atomically with the business
    /// write that caused it. Provides <see cref="ISqlTransactionalQueue"/> and
    /// <see cref="ISqlTransactionProvider"/>, and the table-backed <see cref="IDeadLetterWriter"/> and
    /// <see cref="IDeadLetterStore"/> unless another dead-letter store is chosen.
    /// </summary>
    /// <remarks>
    /// Pair it with a delivery: <c>UseInHostDelivery()</c> to drain the table in this host, or
    /// <c>UseCloudTasksDelivery(...)</c> to relay it to Cloud Tasks. A host that only produces needs
    /// none. Configuration is read from the <c>Queue:Mssql</c> section. Invalid configuration fails
    /// at <c>AddQueue</c> when bindable, and again at host start for values changed by later
    /// Configure/PostConfigure calls.
    /// </remarks>
    public static QueueBuilder UseMssqlStorage(this QueueBuilder queue, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return queue.UseSqlStorage<MssqlStorageOptions>(Descriptor, configuration, configure: null, dataSource: null);
    }

    /// <summary>
    /// As <see cref="UseMssqlStorage(QueueBuilder, IConfiguration)"/>, then adjusted by
    /// <paramref name="configure"/>.
    /// </summary>
    public static QueueBuilder UseMssqlStorage(
        this QueueBuilder queue, IConfiguration configuration, Action<MssqlStorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configure);
        return queue.UseSqlStorage(Descriptor, configuration, configure, dataSource: null);
    }

    /// <summary>
    /// As <see cref="UseMssqlStorage(QueueBuilder, IConfiguration)"/>,
    /// configured in code only (tests, embedded scenarios).
    /// </summary>
    public static QueueBuilder UseMssqlStorage(this QueueBuilder queue, Action<MssqlStorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return queue.UseSqlStorage(Descriptor, configuration: null, configure, dataSource: null);
    }

    /// <summary>
    /// As <see cref="UseMssqlStorage(QueueBuilder, IConfiguration)"/>,
    /// connecting through a SqlClient data source the host owns instead of a connection string; the
    /// storage never disposes it. Table names and schema creation are still read from the
    /// <c>Queue:Mssql</c> section.
    /// </summary>
    public static QueueBuilder UseMssqlStorage(
        this QueueBuilder queue, Func<IServiceProvider, DbDataSource> dataSource, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(configuration);
        return queue.UseSqlStorage<MssqlStorageOptions>(Descriptor, configuration, configure: null, dataSource);
    }

    /// <summary>
    /// As <see cref="UseMssqlStorage(QueueBuilder, Func{IServiceProvider, DbDataSource}, IConfiguration)"/>,
    /// configured in code only.
    /// </summary>
    public static QueueBuilder UseMssqlStorage(
        this QueueBuilder queue, Func<IServiceProvider, DbDataSource> dataSource, Action<MssqlStorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(configure);
        return queue.UseSqlStorage(Descriptor, configuration: null, configure, dataSource);
    }
}
