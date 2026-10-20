using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Spinneret.Scheduler;
using Spinneret.Scheduler.Sql;

namespace Spinneret.Sql.Conformance;

/// <summary>
/// What a SQL scheduler registers, how it binds and validates its options, and the startup check
/// that it shares the queue's database — the same for every dialect. No database needed.
/// </summary>
public abstract class SqlSchedulerRegistrationConformance(SqlTestDialect dialect)
{
    protected SqlTestDialect Dialect => dialect;

    private IConfiguration Configuration(Dictionary<string, string?>? overrides = null)
    {
        var settings = new Dictionary<string, string?>
        {
            [$"{dialect.StorageSection}:ConnectionString"] = dialect.SampleConnectionString,
        };
        foreach (var (key, value) in overrides ?? [])
            settings[key] = value;

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private static IServiceCollection NewServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        return services;
    }

    private IServiceCollection ServicesWithQueue(IConfiguration configuration)
    {
        var services = NewServices();
        services.AddQueue(q => dialect.UseStorage(
            q.AddRequestAssemblies(typeof(SqlSchedulerRegistrationConformance).Assembly), configuration));
        return services;
    }

    [Test]
    public async Task Scheduler_composes_in_either_order_relative_to_the_queue()
    {
        // Nothing here reads the collection, so the calls commute.
        var configuration = Configuration();

        var services = NewServices();
        services.AddScheduler(s => dialect.UseScheduler(s, configuration));
        services.AddQueue(q => dialect.UseStorage(
            q.AddRequestAssemblies(typeof(SqlSchedulerRegistrationConformance).Assembly), configuration));

        var provider = services.BuildServiceProvider();
        await Assert.That(provider.GetRequiredService<IRecurringJobScheduler>()).IsTypeOf<SqlScheduler>();
        await Assert.That(provider.GetRequiredService<ISchedulerSweep>()).IsTypeOf<SqlSchedulerSweeper>();
    }

    [Test]
    public async Task Scheduler_registers_recurring_and_transactional_schedulers()
    {
        var configuration = Configuration();
        var services = ServicesWithQueue(configuration);

        services.AddScheduler(s => dialect.UseScheduler(s, configuration));
        var provider = services.BuildServiceProvider();

        await Assert.That(provider.GetRequiredService<IRecurringJobScheduler>()).IsTypeOf<SqlScheduler>();
        await Assert.That(provider.GetRequiredService<ISqlTransactionalScheduler>())
            .IsTypeOf<SqlTransactionalScheduler>();
    }

    [Test]
    public async Task Scheduler_registers_installer_and_schema_initializer_but_no_sweeper()
    {
        var configuration = Configuration();
        var services = ServicesWithQueue(configuration);

        services.AddScheduler(s => dialect.UseScheduler(s, configuration));

        var hostedTypes = services
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType)
            .ToArray();
        // The installer is shared with the other storages, so it is named rather than typed: it is
        // internal to Spinneret.Scheduler.
        await Assert.That(hostedTypes.Select(t => t?.FullName))
            .Contains("Spinneret.Scheduler.RecurringJobInstaller");
        await Assert.That(hostedTypes).Contains(typeof(SqlSchedulerSchemaInitializer));
        await Assert.That(hostedTypes).DoesNotContain(typeof(SqlSchedulerSweeper));
    }

    [Test]
    public async Task Scheduler_registers_the_sweep_engine_but_not_a_trigger()
    {
        // The engine is always available; what drives it is the host's choice — a timer via
        // UseSweeper(), or the HTTP endpoint in Spinneret.Scheduler.Http.
        var configuration = Configuration();
        var services = ServicesWithQueue(configuration);

        services.AddScheduler(s => dialect.UseScheduler(s, configuration));

        var sweep = services.Single(d => d.ServiceType == typeof(ISchedulerSweep));
        await Assert.That(sweep.ImplementationType).IsEqualTo(typeof(SqlSchedulerSweeper));
        await Assert.That(services
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType?.FullName))
            .DoesNotContain("Spinneret.Scheduler.SchedulerSweeperService");
    }

    [Test]
    public async Task Options_defaults_hold_without_configuration()
    {
        var configuration = Configuration();
        var services = ServicesWithQueue(configuration);

        services.AddScheduler(s => dialect.UseScheduler(s, configuration));
        var options = dialect.SchedulerOptions(services.BuildServiceProvider());

        await Assert.That(options.TableName).IsEqualTo(dialect.DefaultSchedulerTable);
    }

    [Test]
    public async Task Options_bind_from_the_dialects_scheduler_section()
    {
        var configuration = Configuration(new() { [$"{dialect.SchedulerSection}:TableName"] = "MyJobs" });
        var services = ServicesWithQueue(configuration);

        services.AddScheduler(s => dialect.UseScheduler(s, configuration));
        var options = dialect.SchedulerOptions(services.BuildServiceProvider());

        await Assert.That(options.TableName).IsEqualTo("MyJobs");
    }

    [Test]
    [Arguments("bad name!")]
    [Arguments("1Jobs")]
    public async Task Invalid_table_name_fails_at_registration_naming_the_key(string tableName)
    {
        var key = $"{dialect.SchedulerSection}:TableName";
        var configuration = Configuration(new() { [key] = tableName });
        var services = ServicesWithQueue(configuration);

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddScheduler(s => dialect.UseScheduler(s, configuration)));

        await Assert.That(ex.Message).Contains(key);
    }

    [Test]
    public async Task A_table_name_that_derives_an_index_name_too_long_fails_at_registration()
    {
        var key = $"{dialect.SchedulerSection}:TableName";
        var configuration = Configuration(new() { [key] = new string('j', dialect.MaxIdentifierLength - 2) });
        var services = ServicesWithQueue(configuration);

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddScheduler(s => dialect.UseScheduler(s, configuration)));

        await Assert.That(ex.Message).Contains(key);
    }
}
