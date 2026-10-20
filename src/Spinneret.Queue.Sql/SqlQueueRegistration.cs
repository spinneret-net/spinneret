using System.Data.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Spinneret.Queue;
using Spinneret.Queue.Sql;

// ReSharper disable once CheckNamespace — deliberate: registration extensions live in the
// DI namespace so every Use* call is discoverable without a using directive.
namespace Microsoft.Extensions.DependencyInjection
{
    public static class SqlQueueBuilderExtensions
    {
        /// <summary>
        /// Delivers messages in this host: polling workers drain the SQL storage's table, one message
        /// per database transaction, so a handler's writes, its cascade enqueues and the delivery's
        /// outcome commit together. Requires a SQL storage such as <c>UseMssqlStorage</c> or <c>UsePostgresStorage</c>.
        /// Configuration is read from the <c>Queue:InHost</c> section.
        /// </summary>
        /// <remarks>
        /// Use it only on hosts that should consume: a producer-only host (a public API in front of a
        /// worker service) composes the same storage without a delivery.
        /// </remarks>
        public static QueueBuilder UseInHostDelivery(this QueueBuilder queue, IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            var section = configuration.GetSection(InHostDeliveryOptions.SectionName);
            return queue.UseInHostDeliveryCore(options => section.Bind(options));
        }

        /// <summary>As <see cref="UseInHostDelivery(QueueBuilder, IConfiguration)"/>, configured in code.</summary>
        public static QueueBuilder UseInHostDelivery(this QueueBuilder queue, Action<InHostDeliveryOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            return queue.UseInHostDeliveryCore(configure);
        }

        /// <summary>As <see cref="UseInHostDelivery(QueueBuilder, IConfiguration)"/>, with the defaults.</summary>
        public static QueueBuilder UseInHostDelivery(this QueueBuilder queue) =>
            queue.UseInHostDeliveryCore(_ => { });

        private static QueueBuilder UseInHostDeliveryCore(this QueueBuilder queue, Action<InHostDeliveryOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(queue);

            return queue.UseDelivery(new QueueDeliveryRole(nameof(UseInHostDelivery), (services, registry) =>
            {
                // Fail as early as possible on broken configuration; the options-pipeline validation
                // re-validates at host start to also cover later Configure/PostConfigure changes.
                var eagerlyBound = new InHostDeliveryOptions();
                configure(eagerlyBound);
                InHostDeliveryOptionsValidator.ValidateOrThrow(eagerlyBound, registry);

                services.AddOptions<InHostDeliveryOptions>().Configure(configure).ValidateOnStart();
                services.AddSingleton<IValidateOptions<InHostDeliveryOptions>>(new InHostDeliveryOptionsValidator(registry));
                services.AddHostedService<SqlQueueWorker>();
            })
            {
                RequiresClaimableStorage = true,
            });
        }
    }
}

namespace Spinneret.Queue.Sql
{
    /// <summary>
    /// A dialect package's storage, as the shared registration needs it: the <c>Use*Storage</c>
    /// method name (for error messages), the options section, the dialect, and how to build a data
    /// source from a connection string when the host does not supply one.
    /// </summary>
    internal sealed record SqlStorageDescriptor(
        string MethodName, string SectionName, SqlQueueDialect Dialect, Func<string, DbDataSource> CreateDataSource);

    /// <summary>
    /// The registrations every SQL dialect shares. A dialect package describes itself with a
    /// <see cref="SqlStorageDescriptor"/> and its options type; nothing below knows which database it
    /// is talking to.
    /// </summary>
    internal static class SqlQueueRegistration
    {
        /// <summary>
        /// Sets the queue's storage to a SQL dialect. Binds <typeparamref name="TOptions"/> from the
        /// descriptor's section of <paramref name="configuration"/> (when given), then
        /// <paramref name="configure"/>, then resolves a <c>ConnectionStringName</c>; validates eagerly
        /// and again at host start. With <paramref name="dataSource"/> the host's data source is used
        /// — and left for the host to dispose — and no connection string is needed.
        /// </summary>
        public static QueueBuilder UseSqlStorage<TOptions>(
            this QueueBuilder queue,
            SqlStorageDescriptor descriptor,
            IConfiguration? configuration,
            Action<TOptions>? configure,
            Func<IServiceProvider, DbDataSource>? dataSource)
            where TOptions : SqlStorageOptions, new()
        {
            ArgumentNullException.ThrowIfNull(queue);
            ArgumentNullException.ThrowIfNull(descriptor);

            var apply = BindOptions(descriptor.SectionName, configuration, configure);
            var validator = new SqlStorageOptionsValidator<TOptions>(
                descriptor.Dialect, descriptor.SectionName, requiresConnectionString: dataSource is null);

            return queue.UseStorage(StorageRole(
                descriptor.MethodName,
                register: (services, _) =>
                {
                    // Fail as early as possible on broken configuration; the options-pipeline
                    // validation re-validates at host start to also cover later Configure calls.
                    var eagerlyBound = new TOptions();
                    apply(eagerlyBound);
                    validator.ValidateOrThrow(eagerlyBound);

                    services.AddOptions<TOptions>().Configure(apply).ValidateOnStart();
                    services.AddSingleton<IValidateOptions<TOptions>>(validator);
                },
                storage: sp =>
                {
                    var options = sp.GetRequiredService<IOptions<TOptions>>().Value;
                    var names = NamesOf(options);
                    return dataSource is null
                        ? new SqlQueueStorage(
                            descriptor.CreateDataSource(options.ConnectionString), descriptor.Dialect, names, options.CreateSchema)
                        : new SqlQueueStorage(
                            dataSource(sp) ?? throw new InvalidOperationException(
                                $"The data source given to {descriptor.MethodName} resolved to null."),
                            descriptor.Dialect, names, options.CreateSchema, ownsDataSource: false);
                }));
        }

        public static SqlQueueNames NamesOf(SqlStorageOptions options) =>
            new(options.SchemaName, options.QueueTableName, options.DeadLetterTableName);

        private static Action<TOptions> BindOptions<TOptions>(
            string sectionName, IConfiguration? configuration, Action<TOptions>? configure)
            where TOptions : SqlStorageOptions =>
            options =>
            {
                configuration?.GetSection(sectionName).Bind(options);
                configure?.Invoke(options);
                // The section may carry only the name; resolve it into ConnectionString for runtime
                // consumers — after configure, so a name set in code resolves too.
                if (configuration is not null
                    && string.IsNullOrWhiteSpace(options.ConnectionString)
                    && !string.IsNullOrWhiteSpace(options.ConnectionStringName))
                    options.ConnectionString = configuration.GetConnectionString(options.ConnectionStringName) ?? string.Empty;
            };

        /// <summary>
        /// The storage role for a SQL dialect. <paramref name="register"/> adds the dialect's own
        /// options and validation; the role then registers the outbox as <see cref="IQueue"/>,
        /// <see cref="IEnvelopeQueue"/> and <see cref="ISqlTransactionalQueue"/>, the ambient-transaction
        /// seam, the unit of work and savepoint boundary, the table-backed dead-letter writer and store,
        /// and the schema initializer.
        /// </summary>
        internal static QueueStorageRole StorageRole(
            string name,
            Action<IServiceCollection, QueueTypeRegistry> register,
            Func<IServiceProvider, SqlQueueStorage> storage)
        {
            ArgumentNullException.ThrowIfNull(register);
            ArgumentNullException.ThrowIfNull(storage);

            return new QueueStorageRole(name, (services, registry) =>
            {
                register(services, registry);
                services.AddSqlQueueStorage(storage);
            })
            {
                Claimable = true,
                RegisterRelay = (services, _) => services.AddSqlOutboxRelay(),
            };
        }

        /// <summary>
        /// Registers the relay that forwards the outbox to the delivery's push transport, its
        /// background pass, and eager forwarding of what units of work commit. Runs before the
        /// storage's own registration, so the forwarding observer wins over the storage's silent one.
        /// The transport supplies the <see cref="QueueRelaySettings"/>.
        /// </summary>
        internal static IServiceCollection AddSqlOutboxRelay(this IServiceCollection services)
        {
            services.TryAddSingleton<SqlOutboxRelay>();
            services.TryAddSingleton<IQueueRelay>(sp => sp.GetRequiredService<SqlOutboxRelay>());
            services.TryAddSingleton<ISqlOutboxObserver, EagerForwardingObserver>();
            services.AddHostedService<SqlOutboxRelayService>();
            return services;
        }

        /// <summary>
        /// Registers the storage services. Everything is added with <c>TryAdd</c> ahead of the core
        /// defaults, so the storage's unit of work and boundary win over the pass-through ones while a
        /// dead-letter store chosen explicitly — registered earlier still — wins over the table.
        /// </summary>
        internal static IServiceCollection AddSqlQueueStorage(
            this IServiceCollection services, Func<IServiceProvider, SqlQueueStorage> storage)
        {
            services.TryAddSingleton<SqlQueueStorage>(storage);
            services.TryAddSingleton<ISqlTransactionProvider, AsyncLocalSqlTransactionProvider>();
            services.TryAddSingleton<ISqlOutboxObserver, NoSqlOutboxObserver>();
            services.TryAddSingleton<SqlConnectionSource>();
            // Also registered as itself: deliveries and sweeps must run in a SQL unit of work even
            // when a host replaced IQueueUnitOfWorkFactory, since their claims are SQL statements.
            services.TryAddSingleton<SqlUnitOfWorkFactory>();
            services.TryAddSingleton<IQueueUnitOfWorkFactory>(sp => sp.GetRequiredService<SqlUnitOfWorkFactory>());
            services.TryAddScoped<IQueueDispatchBoundary, SavepointDispatchBoundary>();
            services.TryAddSingleton<IDeadLetterWriter, SqlDeadLetterWriter>();
            services.TryAddSingleton<IDeadLetterStore, SqlDeadLetterStore>();
            services.TryAddSingleton<SqlOutboxQueue>();
            services.TryAddSingleton<IQueue>(sp => sp.GetRequiredService<SqlOutboxQueue>());
            services.TryAddSingleton<IEnvelopeQueue>(sp => sp.GetRequiredService<SqlOutboxQueue>());
            services.TryAddSingleton<ISqlTransactionalQueue>(sp => sp.GetRequiredService<SqlOutboxQueue>());
            services.AddHostedService<SqlQueueSchemaInitializer>();

            return services;
        }
    }

    /// <summary>
    /// Validates <see cref="InHostDeliveryOptions"/> against the command registry: every configured
    /// channel-parallelism key matches a declared channel, so a typo fails the host at boot instead of
    /// silently spinning workers for a channel no command rides on.
    /// </summary>
    internal sealed class InHostDeliveryOptionsValidator(QueueTypeRegistry registry) : IValidateOptions<InHostDeliveryOptions>
    {
        public ValidateOptionsResult Validate(string? name, InHostDeliveryOptions options)
        {
            try
            {
                ValidateOrThrow(options, registry);
                return ValidateOptionsResult.Success;
            }
            catch (InvalidOperationException ex)
            {
                return ValidateOptionsResult.Fail(ex.Message);
            }
        }

        public static void ValidateOrThrow(InHostDeliveryOptions o, QueueTypeRegistry registry)
        {
            if (o.PollInterval <= TimeSpan.Zero)
                throw new InvalidOperationException("Queue:InHost:PollInterval must be positive.");

            foreach (var (channel, parallelism) in o.ChannelParallelism)
            {
                if (channel != QueuePolicy.DefaultChannel && !registry.DeclaredChannels.Contains(channel, StringComparer.Ordinal))
                    throw new InvalidOperationException(
                        $"Queue:InHost:ChannelParallelism:{channel} does not match any channel declared by a [QueuePolicy].");
                if (parallelism < 1)
                    throw new InvalidOperationException(
                        $"Queue:InHost:ChannelParallelism:{channel} must be at least 1.");
            }
        }
    }

    /// <summary>
    /// Validates a dialect's <see cref="SqlStorageOptions"/>: a connection to use, and identifiers
    /// that are safe to splice into SQL — including the index and constraint names the DDL derives
    /// from them, which must fit the database's identifier limit too.
    /// </summary>
    internal sealed class SqlStorageOptionsValidator<TOptions>(
        SqlQueueDialect dialect, string sectionName, bool requiresConnectionString)
        : IValidateOptions<TOptions>
        where TOptions : SqlStorageOptions
    {
        public ValidateOptionsResult Validate(string? name, TOptions options)
        {
            try
            {
                ValidateOrThrow(options);
                return ValidateOptionsResult.Success;
            }
            catch (InvalidOperationException ex)
            {
                return ValidateOptionsResult.Fail(ex.Message);
            }
        }

        public void ValidateOrThrow(TOptions o)
        {
            if (requiresConnectionString && string.IsNullOrWhiteSpace(o.ConnectionString))
                throw new InvalidOperationException(
                    $"{sectionName}:ConnectionString must be set — directly, or as a ConnectionStrings entry named by {sectionName}:ConnectionStringName.");

            SqlIdentifiers.Validate(dialect, o.SchemaName, $"{sectionName}:SchemaName");
            SqlIdentifiers.Validate(dialect, o.QueueTableName, $"{sectionName}:QueueTableName");
            SqlIdentifiers.Validate(dialect, o.DeadLetterTableName, $"{sectionName}:DeadLetterTableName");
            SqlIdentifiers.ValidateDerived(
                dialect, dialect.DerivedIdentifiers(SqlQueueRegistration.NamesOf(o)),
                $"{sectionName}:QueueTableName and {sectionName}:DeadLetterTableName");
        }
    }

    /// <summary>The identifier checks every SQL options validator shares, worded once.</summary>
    internal static class SqlIdentifiers
    {
        public static void Validate(SqlQueueDialect dialect, string value, string configKey)
        {
            if (!dialect.IsValidIdentifier(value))
                throw new InvalidOperationException(
                    $"{configKey} must be a plain SQL identifier (letters, digits, underscore; at most "
                    + $"{dialect.MaxIdentifierLength} characters); got '{value}'.");
        }

        public static void ValidateDerived(SqlQueueDialect dialect, IEnumerable<string> derived, string configKeys)
        {
            foreach (var identifier in derived)
            {
                if (identifier.Length > dialect.MaxIdentifierLength)
                    throw new InvalidOperationException(
                        $"{configKeys}: the name derives the index or constraint name '{identifier}', longer than "
                        + $"the {dialect.MaxIdentifierLength} characters the database allows. Shorten the table name.");
            }
        }
    }
}
