namespace Spinneret.Queue;

/// <summary>
/// A queue delivery named a request type that is not in the <see cref="QueueTypeRegistry"/> —
/// the producer and consumer are out of sync, or the type's assembly was not registered.
/// </summary>
public sealed class UnknownRequestTypeException(string typeName)
    : Exception($"Received queue task for unknown request type '{typeName}'. " +
                "The producer and consumer are out of sync, or the assembly containing the type was not registered.")
{
    public string TypeName { get; } = typeName;
}
