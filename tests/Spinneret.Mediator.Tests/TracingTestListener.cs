using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Spinneret.Mediator.Tests;

// A module initializer: a static field's beforefieldinit could run after the call under test.
// The listener is process-global, so each tracing test uses a request type no other test sends.
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
