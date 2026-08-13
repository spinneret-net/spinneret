using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Spinneret.Scheduler.Tests;

public class StartupExtensionsTests
{
    private static readonly DateTimeZone Stockholm = DateTimeZoneProviders.Tzdb["Europe/Stockholm"];

    private static readonly Schedule EveryFiveMinutes = Schedule.Cron(Stockholm, "*/5 * * * *");

    // ------------------------------------------------------------------- AddRecurringJob ---

    [Test]
    public async Task AddRecurringJob_registers_a_recurring_job_with_the_given_definition()
    {
        var services = new ServiceCollection();

        services.AddRecurringJob("update-projections", EveryFiveMinutes, () => new TestRequest("x"));
        var job = services.BuildServiceProvider().GetRequiredService<IRecurringJob>();

        await Assert.That(job.Key).IsEqualTo("update-projections");
        await Assert.That(job.Schedule).IsEqualTo(EveryFiveMinutes);
        await Assert.That(job.CreateRequest()).IsEqualTo(new TestRequest("x"));
    }

    [Test]
    public async Task AddRecurringJob_invokes_the_factory_per_request()
    {
        var services = new ServiceCollection();
        var calls = 0;

        services.AddRecurringJob("job", EveryFiveMinutes, () =>
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

        services.AddRecurringJob("job-a", EveryFiveMinutes, () => new TestRequest("a"));
        services.AddRecurringJob("job-b", Schedule.Cron(Stockholm, "0 * * * *"), () => new TestRequest("b"));
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

        await Assert.That(() => services.AddRecurringJob(key!, EveryFiveMinutes, () => new TestRequest("x")))
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

        await Assert.That(() => services.AddRecurringJob("job", EveryFiveMinutes, null!))
            .Throws<ArgumentNullException>();
    }

    // ------------------------------------------------------- schedules from configuration ---

    [Test]
    public async Task AddRecurringJob_takes_a_schedule_read_from_configuration()
    {
        // The supported way to vary cadence per environment: the job's declaration is still the one
        // place the schedule is decided, and the configuration read is visible right beside it.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>("Jobs:MonthClose", "cron:Europe/Stockholm:0 3 * * *")])
            .Build();
        var services = new ServiceCollection();

        services.AddRecurringJob(
            "month-close", Schedule.Parse(configuration["Jobs:MonthClose"]!), () => new TestRequest("x"));
        var job = services.BuildServiceProvider().GetRequiredService<IRecurringJob>();

        await Assert.That(job.Schedule).IsEqualTo(Schedule.Cron(Stockholm, "0 3 * * *"));
    }
}
