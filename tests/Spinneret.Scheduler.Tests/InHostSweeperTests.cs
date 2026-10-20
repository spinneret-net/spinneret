using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Spinneret.Scheduler.Tests;

public class InHostSweeperTests
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
        services.AddScheduler(s => s.UseTestStorage(sweep).UseInHostSweeper(o => o.SweepInterval = interval));

        var provider = services.BuildServiceProvider();
        var sweeper = provider.GetServices<IHostedService>()
            .Single(s => s.GetType().Name == "InHostSweeperService");
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
        // A store still warming up must cost a tick, not the sweeper.
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
        // Generous bound: a spin runs thousands of times, and a tight margin flakes on loaded CI.
        var sweep = new RecordingSweep();
        var sweeper = await StartSweeper(new ServiceCollection(), sweep, TimeSpan.FromSeconds(30));

        await Task.Delay(200);
        var calls = Volatile.Read(ref sweep.Calls);
        await sweeper.StopAsync(CancellationToken.None);

        await Assert.That(calls).IsLessThanOrEqualTo(2);
    }

    [Test]
    public async Task A_sweeper_without_a_storage_fails_at_registration()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddScheduler(s => s.UseInHostSweeper()));

        await Assert.That(ex.Message).Contains("nowhere to keep its jobs");
    }

    [Test]
    public async Task A_non_positive_interval_fails_validation()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddScheduler(s => s.UseTestStorage(new RecordingSweep()).UseInHostSweeper(o => o.SweepInterval = TimeSpan.Zero));

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
        services.AddScheduler(s => s.UseTestStorage(new RecordingSweep()).UseInHostSweeper());

        var options = services.BuildServiceProvider()
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<SchedulerOptions>>().Value;

        await Assert.That(options.SweepInterval).IsEqualTo(TimeSpan.FromSeconds(15));
    }
}
