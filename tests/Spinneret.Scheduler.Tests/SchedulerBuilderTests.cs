using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Spinneret.Scheduler.Tests;

public class SchedulerBuilderTests
{
    private sealed class NoSweep : ISchedulerSweep
    {
        public Task<SweepResult> SweepAsync(CancellationToken ct) => Task.FromResult(SweepResult.Nothing);
    }

    [Test]
    public async Task AddScheduler_registers_the_recurring_job_installer_after_the_storage()
    {
        // Hosted services start in registration order, and the installer writes into whatever the
        // storage's own hosted services (schema initializers) create.
        var services = new ServiceCollection();
        services.AddScheduler(s => s.UseStorage(new SchedulerStorageRole("UseTestStorage", sv =>
        {
            sv.AddSingleton<ISchedulerSweep, NoSweep>();
            sv.AddHostedService<StorageInitializer>();
        })));

        var hosted = services.Where(d => d.ServiceType == typeof(IHostedService)).Select(d => d.ImplementationType).ToList();

        await Assert.That(hosted).IsEquivalentTo([typeof(StorageInitializer), typeof(RecurringJobInstaller)]);
    }

    [Test]
    public async Task AddScheduler_without_a_sweeper_registers_no_timer()
    {
        var services = new ServiceCollection();
        services.AddScheduler(s => s.UseTestStorage(new NoSweep()));

        await Assert.That(services.Any(d => d.ImplementationType == typeof(SchedulerSweeperService))).IsFalse();
    }

    [Test]
    public async Task Two_storages_fail_naming_both()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddScheduler(s => s
            .UseTestStorage(new NoSweep())
            .UseStorage(new SchedulerStorageRole("UseOtherStorage", _ => { }))));

        await Assert.That(ex.Message).Contains("UseTestStorage");
        await Assert.That(ex.Message).Contains("UseOtherStorage");
    }

    [Test]
    public async Task AddScheduler_twice_fails()
    {
        var services = new ServiceCollection();
        services.AddScheduler(s => s.UseTestStorage(new NoSweep()));

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddScheduler(s => s.UseTestStorage(new NoSweep())));

        await Assert.That(ex.Message).Contains("already called");
    }

    private sealed class StorageInitializer : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
