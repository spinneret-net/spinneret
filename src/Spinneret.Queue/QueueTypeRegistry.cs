using System.Reflection;
using Spinneret.Mediator;

namespace Spinneret.Queue;

/// <summary>
/// Resolves queue payload type names to the actual <see cref="IRequest{TResponse}"/> CLR types
/// they were enqueued as. Built once at startup by scanning the registered assemblies.
/// Stores types by <see cref="Type.FullName"/> — never assembly-qualified — so the dispatcher
/// cannot be coerced into instantiating arbitrary types.
/// </summary>
public sealed class QueueTypeRegistry
{
    private readonly IReadOnlyDictionary<string, RegisteredRequest> _byName;

    public QueueTypeRegistry(IEnumerable<Assembly> assemblies)
    {
        var entries = new Dictionary<string, RegisteredRequest>(StringComparer.Ordinal);

        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (type is { IsAbstract: true } or { IsInterface: true })
                    continue;

                foreach (var iface in type.GetInterfaces())
                {
                    if (!iface.IsGenericType || iface.GetGenericTypeDefinition() != typeof(IRequest<>))
                        continue;

                    var responseType = iface.GetGenericArguments()[0];
                    var key = type.FullName ?? throw new InvalidOperationException(
                        $"Cannot register request type without a FullName: {type}");

                    if (entries.TryGetValue(key, out var existing) && existing.RequestType != type)
                        throw new InvalidOperationException(
                            $"Duplicate IRequest type name '{key}' in registered assemblies: {existing.RequestType.AssemblyQualifiedName} vs {type.AssemblyQualifiedName}.");

                    entries[key] = new RegisteredRequest(type, responseType, BuildPolicy(type));
                }
            }
        }

        _byName = entries;
        DeclaredChannels = entries.Values
            .Select(e => e.Policy.Channel)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Every non-default channel declared by a registered command's [QueuePolicy], for transports to
    /// validate their channel→queue configuration at startup.
    /// </summary>
    public IReadOnlyCollection<string> DeclaredChannels { get; }

    public string GetName(Type requestType)
    {
        var name = requestType.FullName ?? throw new InvalidOperationException(
            $"Cannot enqueue request type without a FullName: {requestType}");

        if (!_byName.ContainsKey(name))
            throw new InvalidOperationException(
                $"Request type '{name}' is not registered with the queue. " +
                $"Ensure its containing assembly was passed to AddGcpQueue/AddQueueCore.");

        return name;
    }

    public RegisteredRequest Resolve(string typeName)
    {
        if (!_byName.TryGetValue(typeName, out var entry))
            throw new InvalidOperationException(
                $"Received queue task for unknown request type '{typeName}'. " +
                $"The producer and consumer are out of sync, or the assembly containing the type was not registered.");

        return entry;
    }

    public QueuePolicy GetPolicy(Type requestType) => _byName[GetName(requestType)].Policy;

    private static QueuePolicy BuildPolicy(Type requestType)
    {
        var attribute = (QueuePolicyAttribute?)Attribute.GetCustomAttribute(requestType, typeof(QueuePolicyAttribute));
        if (attribute is null)
            return QueuePolicy.Default;

        try
        {
            return attribute.ToPolicy();
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"Invalid [QueuePolicy] on '{requestType.FullName}': {ex.Message}", ex);
        }
    }

    public sealed record RegisteredRequest(Type RequestType, Type ResponseType, QueuePolicy Policy);
}
