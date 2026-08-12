using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Spinneret.Queue.Mssql;

public static class StartupExtensions
{
    /// <summary>
    /// Registers the SQL Server queue: <see cref="IQueue"/> and <see cref="IMssqlTransactionalQueue"/>
    /// for producers, the ambient-transaction seam, the table-backed dead-letter store, the schema
    /// initializer, and the type registry built from the supplied assemblies.
    /// </summary>
    /// <remarks>
    /// Call <c>AddMssqlQueueWorker()</c> on the host that should consume messages — producers-only
    /// hosts skip it. Configuration is read from the <c>Queue:Mssql</c> section.
    /// </remarks>
    public static IServiceCollection AddMssqlQueue(
        this IServiceCollection services,
        IConfiguration configuration,
        params Assembly[] requestAssemblies)
    {
        var section = configuration.GetSection(MssqlQueueOptions.SectionName);
        services.Configure<MssqlQueueOptions>(section);
        // The section carries only the name when ConnectionStringName is used; resolve it into
        // ConnectionString both for the eager validation below and for runtime consumers.
        services.PostConfigure<MssqlQueueOptions>(o => ResolveConnectionString(o, configuration));

        var bound = new MssqlQueueOptions();
        section.Bind(bound);
        ResolveConnectionString(bound, configuration);

        var registry = new QueueTypeRegistry(requestAssemblies);
        Validate(bound, registry);

        // The savepoint boundary must win over the pass-through default even when something else
        // (another transport, a direct AddQueueCore call) already registered it — but a custom
        // boundary the host registered deliberately is respected.
        var boundary = services.LastOrDefault(d => d.ServiceType == typeof(IQueueDispatchBoundary));
        if (boundary is null || boundary.ImplementationType == typeof(DirectDispatchBoundary))
            services.Replace(ServiceDescriptor.Scoped<IQueueDispatchBoundary, MssqlDispatchBoundary>());

        services.AddQueueCore(registry);

        services.TryAddSingleton<IQueuePayloadSerializer, DefaultJsonPayloadSerializer>();
        services.TryAddSingleton<IMssqlTransactionProvider, AsyncLocalMssqlTransactionProvider>();
        services.TryAddSingleton<IDeadLetterWriter, MssqlDeadLetterWriter>();
        services.TryAddSingleton(sp => new MssqlQueueSql(sp.GetRequiredService<IOptions<MssqlQueueOptions>>().Value));
        services.TryAddSingleton<MssqlQueue>();
        services.TryAddSingleton<IQueue>(sp => sp.GetRequiredService<MssqlQueue>());
        services.TryAddSingleton<IEnvelopeQueue>(sp => sp.GetRequiredService<MssqlQueue>());
        services.TryAddSingleton<IMssqlTransactionalQueue>(sp => sp.GetRequiredService<MssqlQueue>());
        services.AddHostedService<MssqlQueueSchemaInitializer>();

        return services;
    }

    /// <summary>
    /// Registers the polling delivery worker. Separate from <see cref="AddMssqlQueue"/> so hosts
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

    private static void Validate(MssqlQueueOptions o, QueueTypeRegistry registry)
    {
        if (string.IsNullOrWhiteSpace(o.ConnectionString))
            throw new InvalidOperationException(
                "Queue:Mssql:ConnectionString must be set — directly, or as a ConnectionStrings entry named by Queue:Mssql:ConnectionStringName.");

        ValidateIdentifier(o.SchemaName, "Queue:Mssql:SchemaName");
        ValidateIdentifier(o.QueueTableName, "Queue:Mssql:QueueTableName");
        ValidateIdentifier(o.DeadLetterTableName, "Queue:Mssql:DeadLetterTableName");

        if (o.PollInterval <= TimeSpan.Zero)
            throw new InvalidOperationException("Queue:Mssql:PollInterval must be positive.");

        // Parallelism keys are validated against the declared channels so a typo fails the host at
        // boot instead of silently spinning workers for a channel no command rides on.
        foreach (var (channel, parallelism) in o.ChannelParallelism)
        {
            if (channel != QueuePolicy.DefaultChannel && !registry.DeclaredChannels.Contains(channel, StringComparer.Ordinal))
                throw new InvalidOperationException(
                    $"Queue:Mssql:ChannelParallelism:{channel} does not match any channel declared by a [QueuePolicy].");
            if (parallelism < 1)
                throw new InvalidOperationException(
                    $"Queue:Mssql:ChannelParallelism:{channel} must be at least 1.");
        }
    }

    private static void ValidateIdentifier(string value, string configKey)
    {
        if (!Identifier.IsValid(value))
            throw new InvalidOperationException(
                $"{configKey} must be a plain SQL identifier (letters, digits, underscore); got '{value}'.");
    }
}
