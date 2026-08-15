using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Spinneret.Scheduler.Tests;

/// <summary>
/// The sweeper is only a clock, but three of its properties are load-bearing for the providers
/// behind it: it keeps ticking after a failed sweep, it never overlaps two sweeps, and it always
/// waits between them — that delay is the backoff a provider relies on when it cannot make progress.
/// </summary>
public class SchedulerSweeperTests
{
    private sealed class RecordingSweep : ISchedulerSweep
    {
        private int _running;

        public int Calls;
        public bool Overlapped { get; private set; }
        public Func<int, Task>? OnSweep { get; init; }

        public async Task<SweepResult> SweepAsync(CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _running, 1) == 1)
                Overlapped = true;

            var call = Interlocked.Increment(ref Calls);
            try
            {
                if (OnSweep is not null)
                    await OnSweep(call);

                return SweepResult.Nothing;
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }
    }

    private static async Task<IHostedService> StartSweeper(
        IServiceCollection services, ISchedulerSweep sweep, TimeSpan interval)
    {
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(sweep);
        services.AddSchedulerSweeper(o => o.SweepInterval = interval);

        var provider = services.BuildServiceProvider();
        var sweeper = provider.GetServices<IHostedService>()
            .Single(s => s.GetType().Name == "SchedulerSweeperService");
        await sweeper.StartAsync(CancellationToken.None);
        return sweeper;
    }

    private static async Task<int> WaitForCalls(RecordingSweep sweep, int atLeast)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref sweep.Calls) < atLeast && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        return Volatile.Read(ref sweep.Calls);
    }

    [Test]
    public async Task Sweeps_repeatedly_on_the_interval()
    {
        var sweep = new RecordingSweep();
        var sweeper = await StartSweeper(new ServiceCollection(), sweep, TimeSpan.FromMilliseconds(10));

        var calls = await WaitForCalls(sweep, 3);
        await sweeper.StopAsync(CancellationToken.None);

        await Assert.That(calls).IsGreaterThanOrEqualTo(3);
    }

    [Test]
    public async Task Keeps_ticking_after_a_sweep_throws()
    {
        // A store still warming up next to the host must cost a tick, not the sweeper.
        var sweep = new RecordingSweep
        {
            OnSweep = call => call <= 2 ? Task.FromException(new InvalidOperationException("boom")) : Task.CompletedTask,
        };
        var sweeper = await StartSweeper(new ServiceCollection(), sweep, TimeSpan.FromMilliseconds(10));

        var calls = await WaitForCalls(sweep, 4);
        await sweeper.StopAsync(CancellationToken.None);

        await Assert.That(calls).IsGreaterThanOrEqualTo(4);
    }

    [Test]
    public async Task Never_runs_two_sweeps_at_once()
    {
        // Serial execution is what lets a provider publish an ambient transaction for a whole pass.
        var sweep = new RecordingSweep { OnSweep = _ => Task.Delay(30) };
        var sweeper = await StartSweeper(new ServiceCollection(), sweep, TimeSpan.FromMilliseconds(1));

        await WaitForCalls(sweep, 3);
        await sweeper.StopAsync(CancellationToken.None);

        await Assert.That(sweep.Overlapped).IsFalse();
    }

    [Test]
    public async Task Waits_between_sweeps_rather_than_spinning()
    {
        // The delay is the backoff a provider depends on when it cannot make progress; without it a
        // permanently failing job would be retried in a tight loop.
        //
        // Deliberately generous: the assertion is "far fewer sweeps than a spin would produce",
        // not an exact count. A tight margin here fails on a loaded CI agent for reasons that have
        // nothing to do with the behaviour under test — a spin would run thousands of times, so a
        // wide bound still catches the regression this guards against.
        var sweep = new RecordingSweep();
        var sweeper = await StartSweeper(new ServiceCollection(), sweep, TimeSpan.FromSeconds(30));

        await Task.Delay(200);
        var calls = Volatile.Read(ref sweep.Calls);
        await sweeper.StopAsync(CancellationToken.None);

        await Assert.That(calls).IsLessThanOrEqualTo(2);
    }

    [Test]
    public async Task Starting_without_a_scheduler_registered_fails()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSchedulerSweeper();
        var provider = services.BuildServiceProvider();
        var sweeper = provider.GetServices<IHostedService>()
            .Single(s => s.GetType().Name == "SchedulerSweeperService");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sweeper.StartAsync(CancellationToken.None));

        await Assert.That(ex!.Message).Contains("AddFirestoreScheduler");
    }

    [Test]
    public async Task A_non_positive_interval_fails_validation()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSchedulerSweeper(o => o.SweepInterval = TimeSpan.Zero);

        var options = services.BuildServiceProvider()
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<SchedulerOptions>>();

        var ex = Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => _ = options.Value);
        await Assert.That(ex.Message).Contains("SweepInterval");
    }

    [Test]
    public async Task Defaults_to_a_fifteen_second_interval()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSchedulerSweeper();

        var options = services.BuildServiceProvider()
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<SchedulerOptions>>().Value;

        await Assert.That(options.SweepInterval).IsEqualTo(TimeSpan.FromSeconds(15));
    }
}
