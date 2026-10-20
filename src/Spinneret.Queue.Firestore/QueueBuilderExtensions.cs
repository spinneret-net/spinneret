using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Spinneret.Queue;
using Spinneret.Queue.Firestore;

// ReSharper disable once CheckNamespace — deliberate: registration extensions live in the
// DI namespace so every Use* call is discoverable without a using directive.
namespace Microsoft.Extensions.DependencyInjection;

public static class FirestoreQueueBuilderExtensions
{
    /// <summary>
    /// Keeps dead letters in Firestore: the <see cref="IDeadLetterWriter"/> the queue files
    /// exhausted work with, and the <see cref="IDeadLetterStore"/> an admin page lists, resends and
    /// discards from. Independent of the queue's storage and delivery — and chosen over a SQL
    /// storage's own dead-letter table when both are present.
    /// </summary>
    /// <remarks>
    /// Requires a host-registered <c>FirestoreDb</c>. A dead letter written here is not part of a SQL
    /// delivery's transaction, so it relies on its idempotency key alone; prefer the SQL storage's own
    /// table when the queue has one. Configuration is read from the <c>Queue:Firestore</c> section.
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

    /// <summary>
    /// As <see cref="UseFirestoreDeadLetters(QueueBuilder, IConfiguration)"/>, with the defaults —
    /// the <c>dead_letters</c> collection.
    /// </summary>
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
