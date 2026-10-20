using System.Reflection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Spinneret.Queue;

// ReSharper disable once CheckNamespace — deliberate: registration extensions live in the
// DI namespace so every Add* call is discoverable without a using directive.
namespace Microsoft.Extensions.DependencyInjection;

public static class QueueServiceCollectionExtensions
{
    /// <summary>
    /// Registers the queue, composed in <paramref name="configure"/> from the request assemblies, a
    /// storage, a delivery and optionally a separate dead-letter store — see <see cref="QueueBuilder"/>.
    /// </summary>
    /// <example>
    /// <code>
    /// services.AddQueue(q => q
    ///     .AddRequestAssemblies(typeof(SyncCustomer).Assembly)
    ///     .UseMssqlStorage(configuration)
    ///     .UseInHostDelivery());
    /// </code>
    /// </example>
    /// <exception cref="InvalidOperationException">
    /// The composition is incomplete or contradictory — no request assemblies, nowhere to put
    /// messages, two choices for one role, a delivery the storage cannot feed, or invalid provider
    /// configuration. Thrown here rather than at the first enqueue.
    /// </exception>
    public static IServiceCollection AddQueue(this IServiceCollection services, Action<QueueBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new QueueBuilder(services);
        configure(builder);
        builder.Build();
        return services;
    }

    /// <summary>
    /// Registers transport-agnostic queue infrastructure: the type registry, the dispatcher, the
    /// delivery processor and pipeline, the resender, and the pass-through defaults for the seams a
    /// storage can do better at. Called last by <see cref="QueueBuilder"/>, so whatever the storage
    /// and delivery registered wins.
    /// </summary>
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

        // Resend is transport-agnostic, so it belongs here beside the dispatcher rather than being
        // repeated by every store package. It resolves only once something asks for it, which keeps
        // a host that registered no IDeadLetterStore — because it has no admin page — working.
        services.TryAddSingleton<IQueueUnitOfWorkFactory, PassThroughQueueUnitOfWorkFactory>();
        services.TryAddSingleton<IDeadLetterResender, DeadLetterResender>();
        return services;
    }
}
