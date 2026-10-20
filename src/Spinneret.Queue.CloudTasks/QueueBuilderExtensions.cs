using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Spinneret.Queue;
using Spinneret.Queue.CloudTasks;

// ReSharper disable once CheckNamespace — DI namespace, for discoverability.
namespace Microsoft.Extensions.DependencyInjection;

public static class CloudTasksQueueBuilderExtensions
{
    /// <summary>Delivers messages through Google Cloud Tasks, configured from <c>Queue:CloudTasks</c>.</summary>
    /// <remarks>
    /// With a SQL storage, enqueues join the caller's transaction and a relay forwards them; without one,
    /// each enqueue calls Cloud Tasks directly and is not atomic with the caller's writes.
    /// </remarks>
    public static QueueBuilder UseCloudTasksDelivery(
        this QueueBuilder queue, IConfiguration configuration, Action<CloudTasksOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(CloudTasksOptions.SectionName);
        return queue.UseCloudTasksDeliveryCore(options =>
        {
            section.Bind(options);
            configure?.Invoke(options);
        });
    }

    /// <summary>
    /// As <see cref="UseCloudTasksDelivery(QueueBuilder, IConfiguration, Action{CloudTasksOptions})"/>,
    /// configured in code only.
    /// </summary>
    public static QueueBuilder UseCloudTasksDelivery(this QueueBuilder queue, Action<CloudTasksOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return queue.UseCloudTasksDeliveryCore(configure);
    }

    private static QueueBuilder UseCloudTasksDeliveryCore(this QueueBuilder queue, Action<CloudTasksOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(queue);

        return queue.UseDelivery(new QueueDeliveryRole(nameof(UseCloudTasksDelivery), (services, registry) =>
        {
            // Validate eagerly; ValidateOnStart re-checks later Configure/PostConfigure changes.
            var eagerlyBound = new CloudTasksOptions();
            configure(eagerlyBound);
            CloudTasksOptionsValidator.ValidateOrThrow(eagerlyBound, registry);

            services.AddOptions<CloudTasksOptions>().Configure(configure).ValidateOnStart();
            services.AddSingleton<IValidateOptions<CloudTasksOptions>>(new CloudTasksOptionsValidator(registry));

            services.AddSingleton(sp => CloudTasksClientFactory.Create(sp.GetRequiredService<IOptions<CloudTasksOptions>>()));
            services.AddSingleton<IQueueTransport, CloudTasksTransport>();
            services.AddSingleton(sp =>
            {
                var relay = sp.GetRequiredService<IOptions<CloudTasksOptions>>().Value.Relay;
                return new QueueRelaySettings(
                    relay.RunInBackground, relay.PollInterval, relay.BatchSize, relay.LeaseDuration, relay.ForwardAhead);
            });
            services.AddQueueOidcAuth(eagerlyBound);

            // No-op unless an emulator endpoint is configured.
            services.AddHostedService<EmulatorQueueInitializer>();
        })
        {
            ProvidesTransport = true,
            SuggestedSerializer = typeof(HostJsonPayloadSerializer),
        });
    }
}
