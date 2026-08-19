using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Spinneret.Mediator.Tests;

/// <summary>
/// Records the mediator's spans for the whole test assembly.
/// </summary>
/// <remarks>
/// A module initializer rather than a static field on the test class: static initialization is
/// <c>beforefieldinit</c>, so it runs on first access to a static member — which is after the call
/// under test in any test that touches no statics beforehand, leaving the listener unregistered and
/// nothing recorded. Registering per assembly makes the ordering explicit instead of incidental.
/// <para>
/// <see cref="ActivitySource.AddActivityListener"/> is process-global while TUnit runs a class's
/// tests in parallel, so this collects everything and callers filter by span name — every tracing
/// test therefore uses a request type no other test sends.
/// </para>
/// </remarks>
internal static class TracingTestListener
{
    internal static readonly ConcurrentBag<Activity> Collected = [];

    [ModuleInitializer]
    internal static void Register() =>
        ActivitySource.AddActivityListener(new ActivityListener
        {
            ShouldListenTo = source => source.Name == MediatorDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
            ActivityStopped = Collected.Add,
        });
}
