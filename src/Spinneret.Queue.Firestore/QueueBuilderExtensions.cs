using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Spinneret.Queue;
using Spinneret.Queue.Firestore;

// ReSharper disable once CheckNamespace — DI namespace, for discoverability.
namespace Microsoft.Extensions.DependencyInjection;

public static class FirestoreQueueBuilderExtensions
{
    /// <summary>Keeps dead letters in Firestore, configured from <c>Queue:Firestore</c>; requires a registered <c>FirestoreDb</c>.</summary>
    /// <remarks>
    /// Overrides a SQL storage's own dead-letter table, but isn't part of its delivery transaction.
    /// </remarks>
    public static QueueBuilder UseFirestoreDeadLetters(this QueueBuilder queue, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(FirestoreDeadLetterOptions.SectionName);
        return queue.UseFirestoreDeadLettersCore(options => section.Bind(options));
    }

    /// <summary>As <see cref="UseFirestoreDeadLetters(QueueBuilder, IConfiguration)"/>, configured in code.</summary>
    public static QueueBuilder UseFirestoreDeadLetters(this QueueBuilder queue, Action<FirestoreDeadLetterOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return queue.UseFirestoreDeadLettersCore(configure);
    }

    /// <summary>As <see cref="UseFirestoreDeadLetters(QueueBuilder, IConfiguration)"/>, with the defaults.</summary>
    public static QueueBuilder UseFirestoreDeadLetters(this QueueBuilder queue) =>
        queue.UseFirestoreDeadLettersCore(configure: null);

    private static QueueBuilder UseFirestoreDeadLettersCore(
        this QueueBuilder queue, Action<FirestoreDeadLetterOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(queue);

        return queue.UseDeadLetters(new QueueDeadLetterRole(nameof(UseFirestoreDeadLetters), services =>
        {
            var builder = services.AddOptions<FirestoreDeadLetterOptions>();
            if (configure is not null)
                builder.Configure(configure);

            builder
                .Validate(o => !string.IsNullOrWhiteSpace(o.Collection),
                    "Queue:Firestore:Collection must be set.")
                .ValidateOnStart();

            services.TryAddSingleton(TimeProvider.System);
            services.TryAddSingleton<IDeadLetterWriter, FirestoreDeadLetterWriter>();
            services.TryAddSingleton<IDeadLetterStore, FirestoreDeadLetterStore>();
        }));
    }
}
