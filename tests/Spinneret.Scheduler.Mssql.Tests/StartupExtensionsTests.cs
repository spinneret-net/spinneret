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
        services.AddMssqlQueue(configuration, typeof(StartupExtensionsTests).Assembly);
        return services;
    }

    [Test]
    public async Task AddMssqlScheduler_without_the_queue_fails_at_startup()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddMssqlScheduler(Configuration()));

        await Assert.That(ex.Message).Contains("AddMssqlQueue");
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
    public async Task AddMssqlSchedulerSweeper_registers_the_sweeper()
    {
        var configuration = Configuration();
        var services = ServicesWithQueue(configuration);
        services.AddMssqlScheduler(configuration);

        services.AddMssqlSchedulerSweeper();

        await Assert.That(services.Any(d => d.ImplementationType == typeof(MssqlSchedulerSweeper))).IsTrue();
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
        await Assert.That(options.SweepInterval).IsEqualTo(TimeSpan.FromSeconds(15));
    }

    [Test]
    public async Task Options_bind_from_the_scheduler_mssql_section()
    {
        var configuration = Configuration(new()
        {
            ["Scheduler:Mssql:TableName"] = "MyJobs",
            ["Scheduler:Mssql:SweepInterval"] = "00:01:00",
        });
        var services = ServicesWithQueue(configuration);

        services.AddMssqlScheduler(configuration);
        var options = services.BuildServiceProvider()
            .GetRequiredService<IOptions<MssqlSchedulerOptions>>().Value;

        await Assert.That(options.TableName).IsEqualTo("MyJobs");
        await Assert.That(options.SweepInterval).IsEqualTo(TimeSpan.FromMinutes(1));
    }

    [Test]
    public async Task Invalid_table_name_fails_at_startup_naming_the_key()
    {
        var configuration = Configuration(new() { ["Scheduler:Mssql:TableName"] = "bad name!" });
        var services = ServicesWithQueue(configuration);

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddMssqlScheduler(configuration));

        await Assert.That(ex.Message).Contains("Scheduler:Mssql:TableName");
    }

    [Test]
    public async Task Non_positive_sweep_interval_fails_at_startup()
    {
        var configuration = Configuration(new() { ["Scheduler:Mssql:SweepInterval"] = "00:00:00" });
        var services = ServicesWithQueue(configuration);

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddMssqlScheduler(configuration));

        await Assert.That(ex.Message).Contains("Scheduler:Mssql:SweepInterval");
    }

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
