using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Spinneret.Queue;
using Spinneret.Queue.CloudTasks;

// ReSharper disable once CheckNamespace — deliberate: registration extensions live in the
// DI namespace so every Use* call is discoverable without a using directive.
namespace Microsoft.Extensions.DependencyInjection;

public static class CloudTasksQueueBuilderExtensions
{
    /// <summary>
    /// Delivers messages through Google Cloud Tasks: each message becomes a task that POSTs its
    /// envelope to <see cref="CloudTasksOptions.DispatcherUrl"/> with an OIDC token, and
    /// <c>endpoints.MapCloudTasksDispatch()</c> receives it on the consuming host. Registers the
    /// Cloud Tasks client, the transport, and the OIDC validation scheme the endpoint uses.
    /// </summary>
    /// <remarks>
    /// With a SQL storage, every enqueue lands in its outbox inside the caller's transaction and a
    /// relay forwards it to Cloud Tasks (see <see cref="CloudTasksOptions.Relay"/>), and each delivery
    /// runs in one SQL transaction committed before the endpoint answers. Without a storage, each
    /// enqueue is a call to Cloud Tasks and is not atomic with anything the caller writes; choose a
    /// dead-letter store (e.g. <c>q.UseFirestoreDeadLetters()</c>) for the consuming host.
    /// Configuration is read from the <c>Queue:CloudTasks</c> section, then <paramref name="configure"/>.
    /// Invalid configuration fails at <c>AddQueue</c> when bindable, and again at host start for
    /// values changed by later Configure/PostConfigure calls.
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
    /// configured in code only (tests, embedded scenarios).
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
            // Fail as early as possible on broken configuration; the options-pipeline validation
            // re-validates at host start to also cover later Configure/PostConfigure changes.
            var eagerlyBound = new CloudTasksOptions();
            configure(eagerlyBound);
            CloudTasksOptionsValidator.ValidateOrThrow(eagerlyBound, registry);

            services.AddOptions<CloudTasksOptions>().Configure(configure).ValidateOnStart();
            services.AddSingleton<IValidateOptions<CloudTasksOptions>>(new CloudTasksOptionsValidator(registry));

            services.AddSingleton(sp => CloudTasksClientFactory.Create(sp.GetRequiredService<IOptions<CloudTasksOptions>>()));
            services.AddSingleton<IQueueTransport, CloudTasksTransport>();
            // What a storage's relay forwards with, when the queue has a storage; unused otherwise.
            services.AddSingleton(sp =>
            {
                var relay = sp.GetRequiredService<IOptions<CloudTasksOptions>>().Value.Relay;
                return new QueueRelaySettings(
                    relay.RunInBackground, relay.PollInterval, relay.BatchSize, relay.LeaseDuration, relay.ForwardAhead);
            });
            services.AddQueueOidcAuth(eagerlyBound);

            // No-ops unless an emulator endpoint is configured, so it is safe to always register:
            // production queues stay owned by infrastructure-as-code, and the initializer resolves the
            // Cloud Tasks client only after that check so no host builds one just to start up.
            services.AddHostedService<EmulatorQueueInitializer>();
        })
        {
            ProvidesTransport = true,
            // ASP.NET's JSON options, so payloads round-trip exactly as the host's wire APIs do.
            SuggestedSerializer = typeof(HostJsonPayloadSerializer),
        });
    }
}
