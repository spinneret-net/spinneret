using System.Reflection;
using System.Runtime.ExceptionServices;
using Spinneret.Mediator;

namespace Spinneret.Queue;

/// <summary>
/// Enqueues a request whose response type is known only at runtime — a scheduled job read back out
/// of its store, where the payload was persisted under a type name and the CLR types come from
/// <see cref="QueueTypeRegistry"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IQueue.Enqueue{TResponse}"/> is generic on purpose: naming the response type is what
/// holds callers to a request the queue can actually dispatch, rather than accepting anything and
/// failing on delivery. A store-driven caller cannot satisfy that statically, so it bridges the gap
/// here — and the response type is not guessed, it comes from the same registry entry the request
/// type was resolved through, so the constructed <c>IRequest&lt;TResponse&gt;</c> always matches
/// what was enqueued. This is how <see cref="QueueDispatcher"/> already reaches
/// <see cref="ISpinneretMediator.Send{TResponse}"/> on the delivery side.
/// </para>
/// <para>
/// Internal, and visible only to the scheduler providers: the untyped shape is plumbing for the one
/// caller that cannot avoid it, not something to offer on the public surface.
/// </para>
/// </remarks>
internal static class ResolvedRequestEnqueuer
{
    private static readonly MethodInfo EnqueueOpenGeneric =
        typeof(IQueue).GetMethods()
            .Single(m => m is { Name: nameof(IQueue.Enqueue), IsGenericMethod: true });

    /// <param name="queue">The queue to enqueue onto.</param>
    /// <param name="request">
    /// The deserialized request. Implements <c>IRequest&lt;TResponse&gt;</c> for
    /// <paramref name="responseType"/> whenever both were taken from one
    /// <see cref="QueueTypeRegistry.RegisteredRequest"/>, which the registry guarantees by only ever
    /// recording a request type alongside the single response type it declares.
    /// </param>
    /// <param name="responseType">The registry-resolved response type of <paramref name="request"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    public static Task Enqueue(IQueue queue, object request, Type responseType, CancellationToken ct)
    {
        try
        {
            return (Task)EnqueueOpenGeneric.MakeGenericMethod(responseType).Invoke(queue, [request, null, ct])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // A transport that throws synchronously rather than returning a faulted task — the MSSQL
            // queue resolves the type before its first await — comes back wrapped in a
            // TargetInvocationException whose own message says nothing about what went wrong. The
            // sweepers put this message straight into the dead letter, so unwrap to the real one.
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
