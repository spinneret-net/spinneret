using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Spinneret.Queue;
using Spinneret.Queue.Mssql;

// ReSharper disable once CheckNamespace — deliberate: registration extensions live in the
// DI namespace so every Add* call is discoverable without a using directive.
namespace Microsoft.Extensions.DependencyInjection;

public static class MssqlQueueServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SQL Server queue: <see cref="IQueue"/> and <see cref="IMssqlTransactionalQueue"/>
    /// for producers, the ambient-transaction seam, the table-backed <see cref="IDeadLetterWriter"/>
    /// and <see cref="IDeadLetterStore"/>, the schema initializer, and the type registry built from
    /// the supplied assemblies.
    /// </summary>
    /// <remarks>
    /// <paramref name="configure"/> runs after the section is bound, and is where
    /// <see cref="MssqlQueueOptions.RequestAssemblies"/> is set — assemblies cannot come from
    /// configuration. Call <c>AddMssqlQueueWorker()</c> on the host that should consume messages —
    /// producers-only hosts skip it. Configuration is read from the <c>Queue:Mssql</c> section.
    /// Invalid configuration fails here when bindable, and again at host start via options
    /// validation for values changed by later Configure/PostConfigure calls.
    /// </remarks>
    /// <example>
    /// <code>
    /// services.AddMssqlQueue(configuration, o => o.RequestAssemblies = [typeof(SyncCustomer).Assembly]);
    /// </code>
    /// </example>
    public static IServiceCollection AddMssqlQueue(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<MssqlQueueOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configure);

        var section = configuration.GetSection(MssqlQueueOptions.SectionName);
        void Apply(MssqlQueueOptions options)
        {
            section.Bind(options);
            // The section carries only the name when ConnectionStringName is used; resolve it
            // into ConnectionString for runtime consumers.
            ResolveConnectionString(options, configuration);
            configure(options);
        }

        var bound = new MssqlQueueOptions();
        Apply(bound);

        return services.AddMssqlQueueCore(Apply, bound);
    }

    /// <summary>
    /// Overload for hosts that configure the queue in code instead of via
    /// <see cref="IConfiguration"/> (tests, embedded scenarios).
    /// </summary>
    public static IServiceCollection AddMssqlQueue(
        this IServiceCollection services,
        Action<MssqlQueueOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var bound = new MssqlQueueOptions();
        configure(bound);

        return services.AddMssqlQueueCore(configure, bound);
    }

    private static IServiceCollection AddMssqlQueueCore(
        this IServiceCollection services,
        Action<MssqlQueueOptions> configure,
        MssqlQueueOptions eagerlyBound)
    {
        var registry = new QueueTypeRegistry(eagerlyBound.RequestAssemblies);

        // Fail as early as possible on broken configuration; the options-pipeline validation
        // below re-validates at host start to also cover later Configure/PostConfigure changes.
        MssqlQueueOptionsValidator.ValidateOrThrow(eagerlyBound, registry);

        services.AddOptions<MssqlQueueOptions>()
            .Configure(configure)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<MssqlQueueOptions>>(new MssqlQueueOptionsValidator(registry));

        // The savepoint boundary must win over the pass-through default even when something else
        // (another transport, a direct AddQueueCore call) already registered it — but a custom
        // boundary the host registered deliberately is respected.
        var boundary = services.LastOrDefault(d => d.ServiceType == typeof(IQueueDispatchBoundary));
        if (boundary is null || boundary.ImplementationType == typeof(DirectDispatchBoundary))
            services.Replace(ServiceDescriptor.Scoped<IQueueDispatchBoundary, MssqlDispatchBoundary>());

        // Same reasoning as the boundary above: a real transaction must win over the pass-through
        // default whoever registered it first, while a scope the host chose deliberately stands.
        var transactionScope = services.LastOrDefault(d => d.ServiceType == typeof(IQueueTransactionScope));
        if (transactionScope is null
            || transactionScope.ImplementationType == typeof(PassThroughQueueTransactionScope))
            services.Replace(ServiceDescriptor.Singleton<IQueueTransactionScope, MssqlQueueTransactionScope>());

        services.AddQueueCore(registry);

        services.TryAddSingleton<IQueuePayloadSerializer, DefaultJsonPayloadSerializer>();
        services.TryAddSingleton<IMssqlTransactionProvider, AsyncLocalMssqlTransactionProvider>();
        services.TryAddSingleton<IDeadLetterWriter, MssqlDeadLetterWriter>();
        services.TryAddSingleton<IDeadLetterStore, MssqlDeadLetterStore>();
        services.TryAddSingleton<MssqlConnectionSource>();
        services.TryAddSingleton(sp => new MssqlQueueSql(sp.GetRequiredService<IOptions<MssqlQueueOptions>>().Value));
        services.TryAddSingleton<MssqlQueue>();
        services.TryAddSingleton<IQueue>(sp => sp.GetRequiredService<MssqlQueue>());
        services.TryAddSingleton<IEnvelopeQueue>(sp => sp.GetRequiredService<MssqlQueue>());
        services.TryAddSingleton<IMssqlTransactionalQueue>(sp => sp.GetRequiredService<MssqlQueue>());
        services.AddHostedService<MssqlQueueSchemaInitializer>();

        return services;
    }

    /// <summary>
    /// Registers the polling delivery worker. Separate from <c>AddMssqlQueue</c> so hosts
    /// that only produce (e.g. a public API next to a worker service) never consume messages.
    /// </summary>
    public static IServiceCollection AddMssqlQueueWorker(this IServiceCollection services)
    {
        services.AddHostedService<MssqlQueueWorker>();
        return services;
    }

    private static void ResolveConnectionString(MssqlQueueOptions o, IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(o.ConnectionString) && !string.IsNullOrWhiteSpace(o.ConnectionStringName))
            o.ConnectionString = configuration.GetConnectionString(o.ConnectionStringName) ?? string.Empty;
    }
}
