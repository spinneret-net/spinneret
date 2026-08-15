using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Spinneret.Queue.Mssql;

namespace Spinneret.Scheduler.Mssql.Tests;

public sealed class StartupExtensionsTests
{
    private static IConfiguration Configuration(Dictionary<string, string?>? overrides = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Queue:Mssql:ConnectionString"] = "Server=localhost;Database=q;Integrated Security=true;",
        };
        foreach (var (key, value) in overrides ?? [])
            settings[key] = value;

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private static IServiceCollection ServicesWithQueue(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        services.AddMssqlQueue(configuration, o => o.RequestAssemblies = [typeof(StartupExtensionsTests).Assembly]);
        return services;
    }

    [Test]
    public async Task AddMssqlScheduler_composes_in_either_order_relative_to_the_queue()
    {
        // Previously this threw when the scheduler was registered first, even though the resulting
        // container was identical. Nothing here reads the collection, so the calls commute.
        var configuration = Configuration();

        var after = new ServiceCollection();
        after.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        after.AddMssqlScheduler(configuration);
        after.AddMssqlQueue(configuration, o => o.RequestAssemblies = [typeof(StartupExtensionsTests).Assembly]);

        var provider = after.BuildServiceProvider();
        await Assert.That(provider.GetRequiredService<IRecurringJobScheduler>()).IsTypeOf<MssqlScheduler>();
        await Assert.That(provider.GetRequiredService<ISchedulerSweep>()).IsTypeOf<MssqlSchedulerSweeper>();
    }

    [Test]
    public async Task AddMssqlScheduler_registers_recurring_and_transactional_schedulers()
    {
        var configuration = Configuration();
        var services = ServicesWithQueue(configuration);

        services.AddMssqlScheduler(configuration);
        var provider = services.BuildServiceProvider();

        await Assert.That(provider.GetRequiredService<IRecurringJobScheduler>()).IsTypeOf<MssqlScheduler>();
        await Assert.That(provider.GetRequiredService<IMssqlTransactionalScheduler>())
            .IsTypeOf<MssqlTransactionalScheduler>();
    }

    [Test]
    public async Task AddMssqlScheduler_registers_installer_and_schema_initializer_but_no_sweeper()
    {
        var configuration = Configuration();
        var services = ServicesWithQueue(configuration);

        services.AddMssqlScheduler(configuration);

        var hostedTypes = services
            .Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService))
            .Select(d => d.ImplementationType)
            .ToArray();
        // The installer is shared with the other transports, so it is named rather than typed: it is
        // internal to Spinneret.Scheduler.
        await Assert.That(hostedTypes.Select(t => t?.FullName))
            .Contains("Spinneret.Scheduler.RecurringJobInstaller");
        await Assert.That(hostedTypes).Contains(typeof(MssqlSchedulerSchemaInitializer));
        await Assert.That(hostedTypes).DoesNotContain(typeof(MssqlSchedulerSweeper));
    }

    [Test]
    public async Task AddMssqlScheduler_registers_the_sweep_engine_but_not_a_trigger()
    {
        // The engine is always available; what drives it is the host's choice — a timer via
        // AddSchedulerSweeper(), or the HTTP endpoint in Spinneret.Scheduler.Http.
        var configuration = Configuration();
        var services = ServicesWithQueue(configuration);

        services.AddMssqlScheduler(configuration);

        var sweep = services.Single(d => d.ServiceType == typeof(ISchedulerSweep));
        await Assert.That(sweep.ImplementationType).IsEqualTo(typeof(MssqlSchedulerSweeper));
        await Assert.That(services
            .Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService))
            .Select(d => d.ImplementationType?.FullName))
            .DoesNotContain("Spinneret.Scheduler.SchedulerSweeperService");
    }

    [Test]
    public async Task Options_defaults_hold_without_configuration()
    {
        var configuration = Configuration();
        var services = ServicesWithQueue(configuration);

        services.AddMssqlScheduler(configuration);
        var options = services.BuildServiceProvider()
            .GetRequiredService<IOptions<MssqlSchedulerOptions>>().Value;

        await Assert.That(options.TableName).IsEqualTo("SpinneretScheduledJobs");
    }

    [Test]
    public async Task Options_bind_from_the_scheduler_mssql_section()
    {
        var configuration = Configuration(new()
        {
            ["Scheduler:Mssql:TableName"] = "MyJobs",
        });
        var services = ServicesWithQueue(configuration);

        services.AddMssqlScheduler(configuration);
        var options = services.BuildServiceProvider()
            .GetRequiredService<IOptions<MssqlSchedulerOptions>>().Value;

        await Assert.That(options.TableName).IsEqualTo("MyJobs");
    }

    [Test]
    public async Task Invalid_table_name_fails_at_startup_naming_the_key()
    {
        var configuration = Configuration(new() { ["Scheduler:Mssql:TableName"] = "bad name!" });
        var services = ServicesWithQueue(configuration);

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddMssqlScheduler(configuration));

        await Assert.That(ex.Message).Contains("Scheduler:Mssql:TableName");
    }

    // The sweep interval moved to Scheduler:SweepInterval on the core options, along with the
    // trigger that reads it; its validation is covered by SchedulerSweeperTests.

    [Test]
    public async Task Schema_script_uses_the_configured_names()
    {
        var script = MssqlSchedulerSchema.CreateScript(
            new MssqlQueueOptions { SchemaName = "jobs" },
            new MssqlSchedulerOptions { TableName = "MyJobs" });

        await Assert.That(script).Contains("[jobs].[MyJobs]");
        await Assert.That(script).Contains("IF OBJECT_ID(N'[jobs].[MyJobs]', N'U') IS NULL");
        await Assert.That(script).Contains("(Status, NextExecuteAt)");
    }
}
