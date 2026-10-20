using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Spinneret.Queue;
using Spinneret.Queue.Mssql;
using Spinneret.Queue.Sql;

// ReSharper disable once CheckNamespace — DI namespace for discoverability
namespace Microsoft.Extensions.DependencyInjection;

public static class MssqlQueueBuilderExtensions
{
    private static readonly SqlStorageDescriptor Descriptor = new(
        nameof(UseMssqlStorage),
        MssqlStorageOptions.SectionName,
        MssqlQueueDialect.Instance,
        SqlClientFactory.Instance.CreateDataSource);

    /// <summary>Stores the queue in SQL Server, configured from <c>Queue:Mssql</c>.</summary>
    /// <remarks>Enqueues join the ambient transaction, committing atomically with the business write.</remarks>
    public static QueueBuilder UseMssqlStorage(this QueueBuilder queue, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return queue.UseSqlStorage<MssqlStorageOptions>(Descriptor, configuration, configure: null, dataSource: null);
    }

    /// <summary>As <see cref="UseMssqlStorage(QueueBuilder, IConfiguration)"/>, then adjusted by <paramref name="configure"/>.</summary>
    public static QueueBuilder UseMssqlStorage(
        this QueueBuilder queue, IConfiguration configuration, Action<MssqlStorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configure);
        return queue.UseSqlStorage(Descriptor, configuration, configure, dataSource: null);
    }

    /// <summary>As <see cref="UseMssqlStorage(QueueBuilder, IConfiguration)"/>, configured in code only.</summary>
    public static QueueBuilder UseMssqlStorage(this QueueBuilder queue, Action<MssqlStorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return queue.UseSqlStorage(Descriptor, configuration: null, configure, dataSource: null);
    }

    /// <summary>As <see cref="UseMssqlStorage(QueueBuilder, IConfiguration)"/>, using a host-owned data source it never disposes.</summary>
    public static QueueBuilder UseMssqlStorage(
        this QueueBuilder queue, Func<IServiceProvider, DbDataSource> dataSource, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(configuration);
        return queue.UseSqlStorage<MssqlStorageOptions>(Descriptor, configuration, configure: null, dataSource);
    }

    /// <summary>As <see cref="UseMssqlStorage(QueueBuilder, Func{IServiceProvider, DbDataSource}, IConfiguration)"/>, configured in code only.</summary>
    public static QueueBuilder UseMssqlStorage(
        this QueueBuilder queue, Func<IServiceProvider, DbDataSource> dataSource, Action<MssqlStorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(configure);
        return queue.UseSqlStorage(Descriptor, configuration: null, configure, dataSource);
    }
}
