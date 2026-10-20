using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Spinneret.Queue.Tests;

// A module initializer: a static field is beforefieldinit and could initialize after the call under test.
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
