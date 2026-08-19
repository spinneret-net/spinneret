using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Spinneret.Queue.Tests;

/// <summary>
/// Enables the queue's spans for the whole test assembly.
/// </summary>
/// <remarks>
/// A module initializer rather than a static field on a test class: static initialization is
/// <c>beforefieldinit</c>, so it runs on first access to a static member — which can be after the
/// call under test, leaving <see cref="ActivitySource.StartActivity(string, ActivityKind)"/>
/// returning null and the test passing or failing on incidental ordering.
/// </remarks>
internal static class TracingTestListener
{
    [ModuleInitializer]
    internal static void Register() =>
        ActivitySource.AddActivityListener(new ActivityListener
        {
            ShouldListenTo = source => source.Name == QueueDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
        });
}
