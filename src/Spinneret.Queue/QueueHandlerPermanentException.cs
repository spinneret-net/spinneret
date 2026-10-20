namespace Spinneret.Queue;

/// <summary>Thrown for a failure no retry can fix; the task is dead-lettered immediately.</summary>
public sealed class QueueHandlerPermanentException(string message, Exception? inner = null)
    : Exception(message, inner);
