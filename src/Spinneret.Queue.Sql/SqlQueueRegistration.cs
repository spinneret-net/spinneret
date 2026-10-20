using System.Data.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Spinneret.Queue;
using Spinneret.Queue.Sql;

// ReSharper disable once CheckNamespace — DI namespace for discoverability
namespace Microsoft.Extensions.DependencyInjection
{
    public static class SqlQueueBuilderExtensions
    {
        /// <summary>Delivers messages in this host by polling the SQL storage, configured from <c>Queue:InHost</c>.</summary>
        /// <remarks>Each delivery runs in one transaction, so a handler's writes and the outcome commit together.</remarks>
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
                // Fail fast; ValidateOnStart re-checks later Configure/PostConfigure changes.
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
    internal sealed record SqlStorageDescriptor(
        string MethodName, string SectionName, SqlQueueDialect Dialect, Func<string, DbDataSource> CreateDataSource);

    internal static class SqlQueueRegistration
    {
        // A host-supplied dataSource is left for the host to dispose.
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
                    // Fail fast; ValidateOnStart re-checks later Configure calls.
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
                // After configure, so a ConnectionStringName set in code resolves too.
                if (configuration is not null
                    && string.IsNullOrWhiteSpace(options.ConnectionString)
                    && !string.IsNullOrWhiteSpace(options.ConnectionStringName))
                    options.ConnectionString = configuration.GetConnectionString(options.ConnectionStringName) ?? string.Empty;
            };

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

        // Runs before the storage's registration, so the forwarding observer wins over the silent one.
        internal static IServiceCollection AddSqlOutboxRelay(this IServiceCollection services)
        {
            services.TryAddSingleton<SqlOutboxRelay>();
            services.TryAddSingleton<IQueueRelay>(sp => sp.GetRequiredService<SqlOutboxRelay>());
            services.TryAddSingleton<ISqlOutboxObserver, EagerForwardingObserver>();
            services.AddHostedService<SqlOutboxRelayService>();
            return services;
        }

        // TryAdd ahead of the core defaults, so these beat the pass-through ones but an explicit
        // dead-letter store (registered earlier) beats the table.
        internal static IServiceCollection AddSqlQueueStorage(
            this IServiceCollection services, Func<IServiceProvider, SqlQueueStorage> storage)
        {
            services.TryAddSingleton<SqlQueueStorage>(storage);
            services.TryAddSingleton<ISqlTransactionProvider, AsyncLocalSqlTransactionProvider>();
            services.TryAddSingleton<ISqlOutboxObserver, NoSqlOutboxObserver>();
            services.TryAddSingleton<SqlConnectionSource>();
            // Also as itself: claims are SQL, so deliveries need it even if IQueueUnitOfWorkFactory is replaced.
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
