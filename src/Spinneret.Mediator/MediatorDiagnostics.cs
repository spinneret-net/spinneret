using System.Diagnostics;

namespace Spinneret.Mediator;

/// <summary>
/// Identifiers for the mediator's tracing instrumentation.
/// </summary>
public static class MediatorDiagnostics
{
    /// <summary>
    /// Name of the <see cref="ActivitySource"/> carrying one span per <see cref="ISpinneretMediator.Send{TResponse}"/>.
    /// Pass it to an <see cref="ActivityListener"/>, or to OpenTelemetry's <c>AddSource</c>, to record them.
    /// </summary>
    public const string ActivitySourceName = "Spinneret.Mediator";
}

/// <summary>Tag keys the mediator's spans carry.</summary>
internal static class MediatorTags
{
    internal const string RequestType = "spinneret.request.type";
    internal const string Cache = "spinneret.mediator.cache";
}

/// <summary>Values of the <see cref="MediatorTags.Cache"/> tag, documented in the README.</summary>
internal static class MediatorCacheOutcome
{
    internal const string Hit = "hit";
    internal const string Miss = "miss";

    /// <summary>Not a cacheable send: no <see cref="CacheAttribute"/>, or a <c>Unit</c> response.</summary>
    internal const string Bypass = "bypass";
}

internal static class MediatorTracing
{
    private static readonly string? Version = typeof(MediatorTracing).Assembly.GetName().Version?.ToString();

    private static readonly ActivitySource Source = new(MediatorDiagnostics.ActivitySourceName, Version);

    /// <summary>Starts the span for one send. It covers the cache path too: a hit is what explains an
    /// absent handler span further down the trace.</summary>
    internal static Activity? StartSend(Type requestType) =>
        // The tag goes in at creation rather than after: a sampler only sees what the creation
        // options carry.
        Source.StartActivity($"Send {requestType.Name}", ActivityKind.Internal, default(ActivityContext),
            new ActivityTagsCollection { [MediatorTags.RequestType] = requestType.FullName });
}
