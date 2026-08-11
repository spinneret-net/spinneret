namespace Spinneret.Queue;

/// <summary>
/// Thrown by a handler (or the dispatcher itself) when a failure is known to be non-recoverable:
/// re-executing with the same input can never succeed — an unresolvable payload, an entity that no
/// longer exists, a producer/consumer version mismatch. The transport dead-letters the task
/// immediately instead of retrying.
/// </summary>
public sealed class QueueHandlerPermanentException(string message, Exception? inner = null)
    : Exception(message, inner);
