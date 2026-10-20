using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Spinneret.Queue;

/// <summary>Composes the host's queue from a storage, a delivery and a dead-letter store.</summary>
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

    /// <summary>Adds assemblies scanned for queueable <c>IRequest&lt;&gt;</c> types; at least one is required.</summary>
    /// <remarks>
    /// Types are registered by <see cref="Type.FullName"/>, so renaming or moving one breaks messages in flight.
    /// </remarks>
    public QueueBuilder AddRequestAssemblies(params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        _requestAssemblies.AddRange(assemblies);
        return this;
    }

    /// <summary>
    /// Serializes payloads with <typeparamref name="TSerializer"/>; every host sharing a queue must use the same.
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

    // Registration order is priority order: everything uses TryAdd, so the first registration wins.
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
                "The queue has nowhere to put messages. Choose a storage (e.g. q.UseMssqlStorage(...) or q.UsePostgresStorage(...)), "
                + "a push transport (e.g. q.UseCloudTasksDelivery(...)), or both.");

        if (Delivery is { RequiresClaimableStorage: true } && Storage is not { Claimable: true })
            throw new InvalidOperationException(
                $"{Delivery.Name} delivers messages from a storage table, so it needs a SQL storage "
                + "such as q.UseMssqlStorage(...) or q.UsePostgresStorage(...).");

        if (Storage is not null && Delivery is { ProvidesTransport: true } && Storage.RegisterRelay is null)
            throw new InvalidOperationException(
                $"{Storage.Name} cannot forward its messages to {Delivery.Name}: the storage has no relay "
                + "to a push transport.");

        var registry = new QueueTypeRegistry(_requestAssemblies.Distinct());

        Services.AddSingleton<QueueRegistered>();
        if (_serializer is not null)
            Services.TryAddSingleton(typeof(IQueuePayloadSerializer), _serializer);

        DeadLetters?.Register(Services);
        // Relay before storage, so its registrations (forwarding on commit) win over the storage defaults.
        if (Storage is not null && Delivery is { ProvidesTransport: true })
            Storage.RegisterRelay!(Services, registry);
        Storage?.Register(Services, registry);
        Delivery?.Register(Services, registry);

        if (Delivery?.SuggestedSerializer is { } suggested)
            Services.TryAddSingleton(typeof(IQueuePayloadSerializer), suggested);
        Services.TryAddSingleton<IQueuePayloadSerializer, DefaultJsonPayloadSerializer>();

        if (Storage is null)
        {
            Services.TryAddSingleton<DirectQueue>();
            Services.TryAddSingleton<IQueue>(sp => sp.GetRequiredService<DirectQueue>());
            Services.TryAddSingleton<IEnvelopeQueue>(sp => sp.GetRequiredService<DirectQueue>());
        }

        Services.AddQueueCore(registry);
    }

    private sealed class QueueRegistered;
}

internal abstract record QueueRole(string Name);

internal sealed record QueueStorageRole(string Name, Action<IServiceCollection, QueueTypeRegistry> Register)
    : QueueRole(Name)
{
    public bool Claimable { get; init; }

    public Action<IServiceCollection, QueueTypeRegistry>? RegisterRelay { get; init; }
}

internal sealed record QueueDeliveryRole(string Name, Action<IServiceCollection, QueueTypeRegistry> Register)
    : QueueRole(Name)
{
    public bool RequiresClaimableStorage { get; init; }

    public bool ProvidesTransport { get; init; }

    public Type? SuggestedSerializer { get; init; }
}

internal sealed record QueueDeadLetterRole(string Name, Action<IServiceCollection> Register) : QueueRole(Name);
