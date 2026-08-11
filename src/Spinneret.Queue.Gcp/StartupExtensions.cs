using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Spinneret.Queue.Gcp;

public static class StartupExtensions
{
    /// <summary>
    /// Registers the GCP Cloud Tasks queue: <see cref="IQueue"/>, the dispatcher,
    /// the OIDC validation scheme used by the dispatch endpoint, and the type
    /// registry built from the supplied assemblies.
    /// </summary>
    /// <remarks>
    /// Call <c>endpoints.MapGcpQueueDispatch()</c> in the request pipeline to expose
    /// the worker endpoint. Configuration is read from the <c>Queue:Gcp</c> section.
    /// </remarks>
    public static IServiceCollection AddGcpQueue(
        this IServiceCollection services,
        IConfiguration configuration,
        params Assembly[] requestAssemblies)
    {
        var section = configuration.GetSection(GcpQueueOptions.SectionName);
        services.Configure<GcpQueueOptions>(section);

        var bound = new GcpQueueOptions();
        section.Bind(bound);

        var registry = new QueueTypeRegistry(requestAssemblies);
        Validate(bound, registry);

        services.AddQueueCore(registry);
        services.TryAddSingleton<IQueuePayloadSerializer, HostJsonPayloadSerializer>();

        services.AddSingleton(sp =>
            CloudTasksClientFactory.Create(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GcpQueueOptions>>()));
        services.AddSingleton<CloudTasksQueue>();
        services.AddSingleton<IQueue>(sp => sp.GetRequiredService<CloudTasksQueue>());
        services.AddSingleton<IEnvelopeQueue>(sp => sp.GetRequiredService<CloudTasksQueue>());

        services.AddQueueOidcAuth(bound);

        return services;
    }

    public static Microsoft.AspNetCore.Routing.IEndpointRouteBuilder MapGcpQueueDispatch(
        this Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints)
        => QueueDispatchEndpoint.MapGcpQueueDispatch(endpoints);

    private static void Validate(GcpQueueOptions o, QueueTypeRegistry registry)
    {
        if (string.IsNullOrWhiteSpace(o.ProjectId))
            throw new InvalidOperationException("Queue:Gcp:ProjectId must be set.");
        if (string.IsNullOrWhiteSpace(o.LocationId))
            throw new InvalidOperationException("Queue:Gcp:LocationId must be set.");
        if (!o.Channels.TryGetValue(QueuePolicy.DefaultChannel, out var defaultQueue) || string.IsNullOrWhiteSpace(defaultQueue))
            throw new InvalidOperationException($"Queue:Gcp:Channels:{QueuePolicy.DefaultChannel} must be set.");

        // Every channel a registered command declares must be mapped, so a missing mapping fails the
        // host at boot instead of throwing at first enqueue in some handler.
        foreach (var channel in registry.DeclaredChannels)
        {
            if (!o.Channels.TryGetValue(channel, out var queueId) || string.IsNullOrWhiteSpace(queueId))
                throw new InvalidOperationException(
                    $"Queue channel '{channel}' is declared by a [QueuePolicy] but not mapped. " +
                    $"Add Queue:Gcp:Channels:{channel}.");
        }
        if (string.IsNullOrWhiteSpace(o.DispatcherUrl))
            throw new InvalidOperationException("Queue:Gcp:DispatcherUrl must be set.");
        if (string.IsNullOrWhiteSpace(o.ServiceAccountEmail))
            throw new InvalidOperationException("Queue:Gcp:ServiceAccountEmail must be set.");
    }
}
