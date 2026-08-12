using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Spinneret.Mediator;

namespace Spinneret.Scheduler.Tests;

public sealed record TestRequest(string Name) : IRequest<Unit>;

public class StartupExtensionsTests
{
    [Test]
    public async Task AddRecurringJob_registers_a_recurring_job_with_the_given_definition()
    {
        var services = new ServiceCollection();
        var schedule = Schedule.Every(Duration.FromMinutes(5));

        services.AddRecurringJob("update-projections", schedule, () => new TestRequest("x"));
        var job = services.BuildServiceProvider().GetRequiredService<IRecurringJob>();

        await Assert.That(job.Key).IsEqualTo("update-projections");
        await Assert.That(job.Schedule).IsEqualTo(schedule);
        await Assert.That(job.CreateRequest()).IsEqualTo(new TestRequest("x"));
    }

    [Test]
    public async Task AddRecurringJob_invokes_the_factory_per_request()
    {
        var services = new ServiceCollection();
        var calls = 0;

        services.AddRecurringJob("job", Schedule.Every(Duration.FromMinutes(5)), () =>
        {
            calls++;
            return new TestRequest("x");
        });
        var job = services.BuildServiceProvider().GetRequiredService<IRecurringJob>();

        job.CreateRequest();
        job.CreateRequest();

        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    public async Task AddRecurringJob_multiple_jobs_accumulate()
    {
        var services = new ServiceCollection();

        services.AddRecurringJob("job-a", Schedule.Every(Duration.FromMinutes(5)), () => new TestRequest("a"));
        services.AddRecurringJob("job-b", Schedule.Every(Duration.FromMinutes(9)), () => new TestRequest("b"));
        var jobs = services.BuildServiceProvider().GetServices<IRecurringJob>().ToArray();

        await Assert.That(jobs.Select(j => j.Key)).IsEquivalentTo(["job-a", "job-b"]);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task AddRecurringJob_missing_key_throws_argument_exception(string? key)
    {
        var services = new ServiceCollection();

        await Assert.That(() => services.AddRecurringJob(
                key!, Schedule.Every(Duration.FromMinutes(5)), () => new TestRequest("x")))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task AddRecurringJob_null_schedule_throws_argument_null()
    {
        var services = new ServiceCollection();

        await Assert.That(() => services.AddRecurringJob("job", null!, () => new TestRequest("x")))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task AddRecurringJob_null_factory_throws_argument_null()
    {
        var services = new ServiceCollection();

        await Assert.That(() => services.AddRecurringJob("job", Schedule.Every(Duration.FromMinutes(5)), null!))
            .Throws<ArgumentNullException>();
    }
}
