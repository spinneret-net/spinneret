using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Spinneret.Queue;
using Spinneret.Queue.Firestore;

// ReSharper disable once CheckNamespace — deliberate: registration extensions live in the
// DI namespace so every Add* call is discoverable without a using directive.
namespace Microsoft.Extensions.DependencyInjection;

public static class FirestoreDeadLetterServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Firestore-backed <see cref="IDeadLetterWriter"/>. Independent of the queue
    /// transport — a host on Cloud Tasks, SQL Server, or anything else can store dead letters here.
    /// </summary>
    /// <remarks>
    /// Requires a host-registered <c>FirestoreDb</c>. Registered with <c>TryAdd</c>, so a writer the
    /// host registered itself always wins. Configuration is read from the <c>Queue:Firestore</c>
    /// section.
    /// </remarks>
    public static IServiceCollection AddFirestoreDeadLetters(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(FirestoreDeadLetterOptions.SectionName);
        return services.AddFirestoreDeadLetters(options => section.Bind(options));
    }

    /// <summary>
    /// Registers the Firestore-backed <see cref="IDeadLetterWriter"/> with default options —
    /// the <c>dead_letters</c> collection.
    /// </summary>
    public static IServiceCollection AddFirestoreDeadLetters(this IServiceCollection services) =>
        services.AddFirestoreDeadLettersCore(configure: null);

    /// <summary>
    /// Overload for hosts that configure the dead-letter store in code instead of via
    /// <see cref="IConfiguration"/> (tests, embedded scenarios).
    /// </summary>
    public static IServiceCollection AddFirestoreDeadLetters(
        this IServiceCollection services,
        Action<FirestoreDeadLetterOptions> configure) =>
        services.AddFirestoreDeadLettersCore(configure);

    private static IServiceCollection AddFirestoreDeadLettersCore(
        this IServiceCollection services,
        Action<FirestoreDeadLetterOptions>? configure)
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
        return services;
    }
}
