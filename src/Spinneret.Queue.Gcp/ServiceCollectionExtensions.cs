using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Spinneret.Queue;
using Spinneret.Queue.Gcp;

// ReSharper disable once CheckNamespace — deliberate: registration extensions live in the
// DI namespace so every Add* call is discoverable without a using directive.
namespace Microsoft.Extensions.DependencyInjection;

public static class GcpQueueServiceCollectionExtensions
{
    /// <summary>
    /// Registers the GCP Cloud Tasks queue: <see cref="IQueue"/>, the dispatcher,
    /// the OIDC validation scheme used by the dispatch endpoint, and the type
    /// registry built from the supplied assemblies.
    /// </summary>
    /// <remarks>
    /// Call <c>endpoints.MapGcpQueueDispatch()</c> in the request pipeline to expose
    /// the worker endpoint. Configuration is read from the <c>Queue:Gcp</c> section.
    /// Invalid configuration fails here when bindable, and again at host start via
    /// options validation for values changed by later Configure/PostConfigure calls.
    /// </remarks>
    public static IServiceCollection AddGcpQueue(
        this IServiceCollection services,
        IConfiguration configuration,
        params Assembly[] requestAssemblies)
    {
        var section = configuration.GetSection(GcpQueueOptions.SectionName);

        var bound = new GcpQueueOptions();
        section.Bind(bound);

        return services.AddGcpQueueCore(
            options => section.Bind(options),
            bound,
            requestAssemblies);
    }

    /// <summary>
    /// Overload for hosts that configure the queue in code instead of via
    /// <see cref="IConfiguration"/> (tests, embedded scenarios).
    /// </summary>
    public static IServiceCollection AddGcpQueue(
        this IServiceCollection services,
        Action<GcpQueueOptions> configure,
        params Assembly[] requestAssemblies)
    {
        var bound = new GcpQueueOptions();
        configure(bound);

        return services.AddGcpQueueCore(configure, bound, requestAssemblies);
    }

    private static IServiceCollection AddGcpQueueCore(
        this IServiceCollection services,
        Action<GcpQueueOptions> configure,
        GcpQueueOptions eagerlyBound,
        Assembly[] requestAssemblies)
    {
        var registry = new QueueTypeRegistry(requestAssemblies);

        // Fail as early as possible on broken configuration; the options-pipeline validation
        // below re-validates at host start to also cover later Configure/PostConfigure changes.
        GcpQueueOptionsValidator.ValidateOrThrow(eagerlyBound, registry);

        services.AddOptions<GcpQueueOptions>()
            .Configure(configure)
            .ValidateOnStart();
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<GcpQueueOptions>>(
            new GcpQueueOptionsValidator(registry));

        services.AddQueueCore(registry);
        services.TryAddSingleton<IQueuePayloadSerializer, HostJsonPayloadSerializer>();

        services.AddSingleton(sp =>
            CloudTasksClientFactory.Create(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GcpQueueOptions>>()));
        services.AddSingleton<CloudTasksQueue>();
        services.AddSingleton<IQueue>(sp => sp.GetRequiredService<CloudTasksQueue>());
        services.AddSingleton<IEnvelopeQueue>(sp => sp.GetRequiredService<CloudTasksQueue>());

        services.AddQueueOidcAuth(eagerlyBound);

        // No-ops unless an emulator endpoint is configured, so it is safe to always register:
        // production queues stay owned by infrastructure-as-code, and the initializer resolves the
        // Cloud Tasks client only after that check so no host builds one just to start up.
        services.AddHostedService<EmulatorQueueInitializer>();

        return services;
    }
}
