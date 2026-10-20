using System.Diagnostics;

namespace Spinneret.Mediator;

/// <summary>Identifiers for the mediator's tracing instrumentation.</summary>
public static class MediatorDiagnostics
{
    /// <summary>Name of the <see cref="ActivitySource"/> emitting one span per <see cref="ISpinneretMediator.Send{TResponse}"/>.</summary>
    public const string ActivitySourceName = "Spinneret.Mediator";
}

internal static class MediatorTags
{
    internal const string RequestType = "spinneret.request.type";
    internal const string Cache = "spinneret.mediator.cache";
}

// Documented in the README.
internal static class MediatorCacheOutcome
{
    internal const string Hit = "hit";
    internal const string Miss = "miss";

    // No CacheAttribute, or a Unit response.
    internal const string Bypass = "bypass";
}

internal static class MediatorTracing
{
    private static readonly string? Version = typeof(MediatorTracing).Assembly.GetName().Version?.ToString();

    private static readonly ActivitySource Source = new(MediatorDiagnostics.ActivitySourceName, Version);

    internal static Activity? StartSend(Type requestType) =>
        // Tag at creation: samplers only see creation options.
        Source.StartActivity($"Send {requestType.Name}", ActivityKind.Internal, default(ActivityContext),
            new ActivityTagsCollection { [MediatorTags.RequestType] = requestType.FullName });
}
