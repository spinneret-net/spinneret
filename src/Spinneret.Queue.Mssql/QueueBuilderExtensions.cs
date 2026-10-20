using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Spinneret.Queue;
using Spinneret.Queue.Mssql;
using Spinneret.Queue.Sql;

// ReSharper disable once CheckNamespace — deliberate: registration extensions live in the
// DI namespace so every Use* call is discoverable without a using directive.
namespace Microsoft.Extensions.DependencyInjection;

public static class MssqlQueueBuilderExtensions
{
    /// <summary>
    /// Stores the queue in SQL Server, in the application's own database: every enqueue is an insert
    /// that joins the ambient (or a supplied) transaction, so it commits atomically with the business
    /// write that caused it. Provides <see cref="ISqlTransactionalQueue"/> and
    /// <see cref="ISqlTransactionProvider"/>, and the table-backed <see cref="IDeadLetterWriter"/> and
    /// <see cref="IDeadLetterStore"/> unless another dead-letter store is chosen.
    /// </summary>
    /// <remarks>
    /// Pair it with a delivery: <c>UseInHostDelivery()</c> to drain the table in this host. A host
    /// that only produces needs none. Configuration is read from the <c>Queue:Mssql</c> section,
    /// then <paramref name="configure"/>. Invalid configuration fails at <c>AddQueue</c> when
    /// bindable, and again at host start for values changed by later Configure/PostConfigure calls.
    /// </remarks>
    public static QueueBuilder UseMssqlStorage(
        this QueueBuilder queue, IConfiguration configuration, Action<MssqlStorageOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(MssqlStorageOptions.SectionName);
        return queue.UseMssqlStorageCore(options =>
        {
            section.Bind(options);
            // The section carries only the name when ConnectionStringName is used; resolve it into
            // ConnectionString for runtime consumers.
            if (string.IsNullOrWhiteSpace(options.ConnectionString) && !string.IsNullOrWhiteSpace(options.ConnectionStringName))
                options.ConnectionString = configuration.GetConnectionString(options.ConnectionStringName) ?? string.Empty;
            configure?.Invoke(options);
        });
    }

    /// <summary>
    /// As <see cref="UseMssqlStorage(QueueBuilder, IConfiguration, Action{MssqlStorageOptions})"/>,
    /// configured in code only (tests, embedded scenarios).
    /// </summary>
    public static QueueBuilder UseMssqlStorage(this QueueBuilder queue, Action<MssqlStorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return queue.UseMssqlStorageCore(configure);
    }

    private static QueueBuilder UseMssqlStorageCore(this QueueBuilder queue, Action<MssqlStorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(queue);

        return queue.UseStorage(SqlQueueRegistration.StorageRole(
            nameof(UseMssqlStorage),
            register: (services, _) =>
            {
                var eagerlyBound = new MssqlStorageOptions();
                configure(eagerlyBound);
                MssqlStorageOptionsValidator.ValidateOrThrow(eagerlyBound);

                services.AddOptions<MssqlStorageOptions>().Configure(configure).ValidateOnStart();
                services.AddSingleton<IValidateOptions<MssqlStorageOptions>, MssqlStorageOptionsValidator>();
            },
            storage: sp =>
            {
                var options = sp.GetRequiredService<IOptions<MssqlStorageOptions>>().Value;
                return new SqlQueueStorage(
                    SqlClientFactory.Instance.CreateDataSource(options.ConnectionString),
                    MssqlQueueDialect.Instance,
                    MssqlQueueSchema.NamesOf(options),
                    options.CreateSchema);
            }));
    }
}
