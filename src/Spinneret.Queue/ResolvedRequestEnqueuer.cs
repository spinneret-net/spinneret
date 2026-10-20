using System.Reflection;
using System.Runtime.ExceptionServices;
using Spinneret.Mediator;

namespace Spinneret.Queue;

// Enqueues a request whose response type is only known at runtime (read back from a store).
// The response type comes from the same registry entry as the request type, so they always match.
internal static class ResolvedRequestEnqueuer
{
    private static readonly MethodInfo EnqueueOpenGeneric =
        typeof(IQueue).GetMethods()
            .Single(m => m is { Name: nameof(IQueue.Enqueue), IsGenericMethod: true });

    public static Task Enqueue(IQueue queue, object request, Type responseType, CancellationToken ct)
    {
        try
        {
            return (Task)EnqueueOpenGeneric.MakeGenericMethod(responseType).Invoke(queue, [request, null, ct])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // A synchronous throw arrives wrapped; unwrap so the dead letter records the real message.
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
