namespace Spinneret.Queue;

/// <summary>A delivery named a request type not in the <see cref="QueueTypeRegistry"/>.</summary>
public sealed class UnknownRequestTypeException(string typeName)
    : Exception($"Received queue task for unknown request type '{typeName}'. " +
                "The producer and consumer are out of sync, or the assembly containing the type was not registered.")
{
    public string TypeName { get; } = typeName;
}
