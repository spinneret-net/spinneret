using System.Reflection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Spinneret.Queue;

// ReSharper disable once CheckNamespace — DI namespace, for discoverability.
namespace Microsoft.Extensions.DependencyInjection;

public static class QueueServiceCollectionExtensions
{
    /// <summary>Registers the queue composed in <paramref name="configure"/>.</summary>
    /// <example>
    /// <code>
    /// services.AddQueue(q => q
    ///     .AddRequestAssemblies(typeof(SyncCustomer).Assembly)
    ///     .UseMssqlStorage(configuration)
    ///     .UseInHostDelivery());
    /// </code>
    /// </example>
    /// <exception cref="InvalidOperationException">The composition is incomplete, contradictory or misconfigured.</exception>
    public static IServiceCollection AddQueue(this IServiceCollection services, Action<QueueBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new QueueBuilder(services);
        configure(builder);
        builder.Build();
        return services;
    }

    // Called last by QueueBuilder, so storage and delivery registrations win.
    internal static IServiceCollection AddQueueCore(
        this IServiceCollection services, IReadOnlyCollection<Assembly> requestAssemblies)
    {
        ArgumentNullException.ThrowIfNull(requestAssemblies);
        if (requestAssemblies.Count == 0)
            throw new ArgumentException("At least one assembly containing IRequest<> types must be provided.", nameof(requestAssemblies));

        return services.AddQueueCore(new QueueTypeRegistry(requestAssemblies));
    }

    internal static IServiceCollection AddQueueCore(this IServiceCollection services, QueueTypeRegistry registry)
    {
        services.AddSingleton(registry);
        services.TryAddScoped<IQueueDispatcher, QueueDispatcher>();
        services.TryAddScoped<IQueueDeliveryProcessor, QueueDeliveryProcessor>();
        services.TryAddScoped<IQueueDispatchBoundary, DirectDispatchBoundary>();
        services.TryAddScoped<QueueDeliveryPipeline>();
        services.TryAddSingleton<QueueEnvelopeFactory>();
        services.TryAddSingleton(TimeProvider.System);

        // Resolved lazily, so hosts without an IDeadLetterStore still work.
        services.TryAddSingleton<IQueueUnitOfWorkFactory, PassThroughQueueUnitOfWorkFactory>();
        services.TryAddSingleton<IDeadLetterResender, DeadLetterResender>();
        return services;
    }
}
