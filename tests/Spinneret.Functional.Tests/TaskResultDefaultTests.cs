using Spinneret.Functional;

namespace Spinneret.Functional.Tests;

public class TaskResultDefaultTests
{
    [Test]
    public async Task Default_TaskResult_throws_a_descriptive_error_instead_of_NRE()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => default(TaskResult<int, string>).AsTask());

        await Assert.That(ex!.Message).Contains("default-constructed");
    }

    [Test]
    public async Task Default_unit_TaskResult_throws_a_descriptive_error_instead_of_NRE()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => default(TaskResult<string>).AsTask());

        await Assert.That(ex!.Message).Contains("default-constructed");
    }
}
