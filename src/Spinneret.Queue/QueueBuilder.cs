using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Spinneret.Queue;

/// <summary>
/// Composes the queue a host runs, inside <c>services.AddQueue(q => ...)</c>. A queue is assembled
/// from up to three independent choices, each made by a provider package's <c>Use*</c> extension:
/// <list type="bullet">
/// <item><b>Storage</b> — where messages wait: a SQL outbox in the application database
/// (<c>UseMssqlStorage</c>). With storage, every enqueue joins the caller's transaction.</item>
/// <item><b>Delivery</b> — how they reach a handler: the in-host worker draining that table
/// (<c>UseInHostDelivery</c>), or a push transport (<c>UseCloudTasksDelivery</c>).</item>
/// <item><b>Dead letters</b> — where exhausted work is kept, when not in the storage itself
/// (<c>UseFirestoreDeadLetters</c>).</item>
/// </list>
/// Each choice is made at most once, and the combination is checked when <c>AddQueue</c> returns.
/// </summary>
public sealed class QueueBuilder
{
    private readonly List<Assembly> _requestAssemblies = [];
    private Type? _serializer;

    internal QueueBuilder(IServiceCollection services) => Services = services;

    /// <summary>The service collection the queue is registered into.</summary>
    public IServiceCollection Services { get; }

    internal QueueStorageRole? Storage { get; private set; }
    internal QueueDeliveryRole? Delivery { get; private set; }
    internal QueueDeadLetterRole? DeadLetters { get; private set; }

    /// <summary>
    /// Adds assemblies scanned for the <c>IRequest&lt;&gt;</c> command types this queue can carry —
    /// every role, producer and consumer alike, reads the same registry. At least one is required.
    /// </summary>
    /// <remarks>
    /// Types are registered by <see cref="Type.FullName"/>, which is the name on the wire, so
    /// renaming or moving a queued command type breaks messages already in flight.
    /// </remarks>
    public QueueBuilder AddRequestAssemblies(params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        _requestAssemblies.AddRange(assemblies);
        return this;
    }

    /// <summary>
    /// Serializes command payloads with <typeparamref name="TSerializer"/> instead of the default:
    /// the delivery's own choice when it has one (Cloud Tasks uses ASP.NET's JSON options), else
    /// the <see cref="System.Text.Json.JsonSerializerOptions"/> registered in DI, else the defaults.
    /// Every host sharing a queue must serialize the same way.
    /// </summary>
    public QueueBuilder UsePayloadSerializer<TSerializer>() where TSerializer : class, IQueuePayloadSerializer
    {
        _serializer = typeof(TSerializer);
        return this;
    }

    internal QueueBuilder UseStorage(QueueStorageRole role)
    {
        Storage = Claim(Storage, role, "storage");
        return this;
    }

    internal QueueBuilder UseDelivery(QueueDeliveryRole role)
    {
        Delivery = Claim(Delivery, role, "delivery");
        return this;
    }

    internal QueueBuilder UseDeadLetters(QueueDeadLetterRole role)
    {
        DeadLetters = Claim(DeadLetters, role, "dead-letter store");
        return this;
    }

    private static TRole Claim<TRole>(TRole? existing, TRole role, string slot) where TRole : QueueRole
    {
        ArgumentNullException.ThrowIfNull(role);
        if (existing is not null)
            throw new InvalidOperationException(
                $"The queue's {slot} is already {existing.Name}; it cannot also be {role.Name}. Choose one.");
        return role;
    }

    /// <summary>
    /// Checks the combination and registers it. Registration order is the priority order — an
    /// explicit dead-letter store, then the storage's relay and the storage, then the delivery, then
    /// the core defaults — and
    /// everything is added with <c>TryAdd</c>, so the first to register a service wins and a service
    /// the host registered itself before <c>AddQueue</c> wins over all of them.
    /// </summary>
    internal void Build()
    {
        if (_requestAssemblies.Count == 0)
            throw new InvalidOperationException(
                "AddQueue requires at least one assembly containing IRequest<> command types: "
                + "q.AddRequestAssemblies(typeof(MyCommand).Assembly).");

        if (Services.Any(d => d.ServiceType == typeof(QueueRegistered)))
            throw new InvalidOperationException(
                "AddQueue was already called. A host has one queue; compose all of it in a single AddQueue call.");

        if (Services.Any(d => d.ServiceType == typeof(IQueue) || d.ServiceType == typeof(IEnvelopeQueue)))
            throw new InvalidOperationException(
                "An IQueue is already registered. The queue is composed by AddQueue alone — choose its "
                + "storage and delivery there rather than registering an IQueue directly.");

        if (Storage is null && Delivery is not { ProvidesTransport: true })
            throw new InvalidOperationException(
                "The queue has nowhere to put messages. Choose a storage (e.g. q.UseMssqlStorage(...)), "
                + "a push transport (e.g. q.UseCloudTasksDelivery(...)), or both.");

        if (Delivery is { RequiresClaimableStorage: true } && Storage is not { Claimable: true })
            throw new InvalidOperationException(
                $"{Delivery.Name} delivers messages from a storage table, so it needs a SQL storage "
                + "such as q.UseMssqlStorage(...).");

        if (Storage is not null && Delivery is { ProvidesTransport: true } && Storage.RegisterRelay is null)
            throw new InvalidOperationException(
                $"{Storage.Name} cannot forward its messages to {Delivery.Name}: the storage has no relay "
                + "to a push transport.");

        var registry = new QueueTypeRegistry(_requestAssemblies.Distinct());

        Services.AddSingleton<QueueRegistered>();
        if (_serializer is not null)
            Services.TryAddSingleton(typeof(IQueuePayloadSerializer), _serializer);

        DeadLetters?.Register(Services);
        // The relay ahead of the storage it belongs to, so what it brings (forwarding on commit)
        // wins over the storage's quieter defaults.
        if (Storage is not null && Delivery is { ProvidesTransport: true })
            Storage.RegisterRelay!(Services, registry);
        Storage?.Register(Services, registry);
        Delivery?.Register(Services, registry);

        if (Delivery?.SuggestedSerializer is { } suggested)
            Services.TryAddSingleton(typeof(IQueuePayloadSerializer), suggested);
        Services.TryAddSingleton<IQueuePayloadSerializer, DefaultJsonPayloadSerializer>();

        // No storage means a push transport with nothing in front of it: each enqueue is a call to
        // the transport.
        if (Storage is null)
        {
            Services.TryAddSingleton<DirectQueue>();
            Services.TryAddSingleton<IQueue>(sp => sp.GetRequiredService<DirectQueue>());
            Services.TryAddSingleton<IEnvelopeQueue>(sp => sp.GetRequiredService<DirectQueue>());
        }

        Services.AddQueueCore(registry);
    }

    /// <summary>Marks a collection AddQueue has composed, so a second call fails instead of half-overriding the first.</summary>
    private sealed class QueueRegistered;
}

/// <summary>A choice a provider package makes on the builder. <see cref="Name"/> is how errors refer to it.</summary>
internal abstract record QueueRole(string Name);

/// <summary>Where messages wait.</summary>
/// <param name="Name">How errors refer to this choice, e.g. <c>UseMssqlStorage</c>.</param>
/// <param name="Register">
/// Registers the storage: <see cref="IQueue"/> and <see cref="IEnvelopeQueue"/> over its table, its
/// unit of work and dispatch boundary, and its dead-letter writer and store (with <c>TryAdd</c>, so
/// an explicit dead-letter store wins).
/// </param>
internal sealed record QueueStorageRole(string Name, Action<IServiceCollection, QueueTypeRegistry> Register)
    : QueueRole(Name)
{
    /// <summary>Whether messages can be claimed straight from the storage, which in-host delivery needs.</summary>
    public bool Claimable { get; init; }

    /// <summary>
    /// Registers what forwards the storage's messages to a push transport, when the delivery is one.
    /// Null for storage that cannot.
    /// </summary>
    public Action<IServiceCollection, QueueTypeRegistry>? RegisterRelay { get; init; }
}

/// <summary>How messages reach their handlers.</summary>
internal sealed record QueueDeliveryRole(string Name, Action<IServiceCollection, QueueTypeRegistry> Register)
    : QueueRole(Name)
{
    /// <summary>Whether delivery drains the storage directly, which needs claimable storage.</summary>
    public bool RequiresClaimableStorage { get; init; }

    /// <summary>Whether delivery registers an <see cref="IQueueTransport"/> messages are pushed onto.</summary>
    public bool ProvidesTransport { get; init; }

    /// <summary>The payload serializer this delivery defaults to when the host chose none.</summary>
    public Type? SuggestedSerializer { get; init; }
}

/// <summary>Where exhausted work is kept, overriding the storage's own dead-letter table.</summary>
internal sealed record QueueDeadLetterRole(string Name, Action<IServiceCollection> Register) : QueueRole(Name);
