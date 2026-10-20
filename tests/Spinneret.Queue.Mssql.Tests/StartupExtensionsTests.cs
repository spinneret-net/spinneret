using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Spinneret.Queue;
using Spinneret.Queue.Sql;

namespace Spinneret.Queue.Mssql.Tests;

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

    private static IServiceCollection AddQueue(
        IConfiguration configuration, IServiceCollection? services = null, bool inHost = false)
    {
        services ??= new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        return services.AddQueue(q =>
        {
            q.AddRequestAssemblies(typeof(StartupExtensionsTests).Assembly).UseMssqlStorage(configuration);
            if (inHost)
                q.UseInHostDelivery(configuration);
        });
    }

    // -------------------------------------------------------------------- registrations ---

    [Test]
    public async Task UseMssqlStorage_exposes_producer_envelope_and_transactional_queue_as_one_instance()
    {
        var provider = AddQueue(Configuration()).BuildServiceProvider();

        var queue = provider.GetRequiredService<IQueue>();
        await Assert.That(queue).IsTypeOf<SqlOutboxQueue>();
        await Assert.That(provider.GetRequiredService<IEnvelopeQueue>()).IsSameReferenceAs(queue);
        await Assert.That(provider.GetRequiredService<ISqlTransactionalQueue>()).IsSameReferenceAs(queue);
    }

    [Test]
    public async Task UseMssqlStorage_registers_the_savepoint_dispatch_boundary()
    {
        var services = AddQueue(Configuration());

        var descriptor = services.Single(d => d.ServiceType == typeof(IQueueDispatchBoundary));

        await Assert.That(descriptor.Lifetime).IsEqualTo(ServiceLifetime.Scoped);
        await Assert.That(descriptor.ImplementationType).IsEqualTo(typeof(SavepointDispatchBoundary));
    }

    [Test]
    public async Task UseMssqlStorage_runs_units_of_work_in_a_sql_transaction()
    {
        var provider = AddQueue(Configuration()).BuildServiceProvider();

        await Assert.That(provider.GetRequiredService<IQueueUnitOfWorkFactory>()).IsTypeOf<SqlUnitOfWorkFactory>();
    }

    [Test]
    public async Task UseMssqlStorage_respects_a_custom_boundary_the_host_registered()
    {
        var services = new ServiceCollection();
        services.AddScoped<IQueueDispatchBoundary, CustomBoundary>();

        AddQueue(Configuration(), services);
        var descriptor = services.Single(d => d.ServiceType == typeof(IQueueDispatchBoundary));

        await Assert.That(descriptor.ImplementationType).IsEqualTo(typeof(CustomBoundary));
    }

    [Test]
    public async Task UseMssqlStorage_registers_the_table_backed_dead_letters()
    {
        var provider = AddQueue(Configuration()).BuildServiceProvider();

        await Assert.That(provider.GetRequiredService<IDeadLetterWriter>()).IsTypeOf<SqlDeadLetterWriter>();
        await Assert.That(provider.GetRequiredService<IDeadLetterStore>()).IsTypeOf<SqlDeadLetterStore>();
    }

    [Test]
    public async Task UseMssqlStorage_registers_the_ambient_transaction_provider()
    {
        var provider = AddQueue(Configuration()).BuildServiceProvider();

        await Assert.That(provider.GetRequiredService<ISqlTransactionProvider>())
            .IsTypeOf<AsyncLocalSqlTransactionProvider>();
    }

    [Test]
    public async Task AddQueue_registers_the_registry_scanned_from_the_request_assemblies()
    {
        var provider = AddQueue(Configuration()).BuildServiceProvider();

        var registry = provider.GetRequiredService<QueueTypeRegistry>();

        await Assert.That(registry.GetName(typeof(PingCommand))).IsEqualTo(typeof(PingCommand).FullName!);
    }

    [Test]
    public async Task UseMssqlStorage_respects_host_registered_overrides()
    {
        var services = new ServiceCollection();
        var serializer = new StubSerializer();
        var deadLetters = new StubDeadLetterWriter();
        services.AddSingleton<IQueuePayloadSerializer>(serializer);
        services.AddSingleton<IDeadLetterWriter>(deadLetters);

        var provider = AddQueue(Configuration(), services).BuildServiceProvider();

        await Assert.That(provider.GetRequiredService<IQueuePayloadSerializer>()).IsSameReferenceAs(serializer);
        await Assert.That(provider.GetRequiredService<IDeadLetterWriter>()).IsSameReferenceAs(deadLetters);
    }

    [Test]
    public async Task UseInHostDelivery_registers_the_polling_worker()
    {
        var services = AddQueue(Configuration(), inHost: true);

        await Assert.That(services.Any(d =>
                d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
                && d.ImplementationType == typeof(SqlQueueWorker)))
            .IsTrue();
    }

    [Test]
    public async Task Storage_without_a_delivery_registers_no_polling_worker()
    {
        // A producer-only host: the same storage, nothing consuming it.
        var services = AddQueue(Configuration());

        await Assert.That(services.Any(d => d.ImplementationType == typeof(SqlQueueWorker))).IsFalse();
    }

    [Test]
    public async Task UseInHostDelivery_without_a_storage_fails_at_registration()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddQueue(q => q
            .AddRequestAssemblies(typeof(StartupExtensionsTests).Assembly)
            .UseInHostDelivery()));

        await Assert.That(ex.Message).Contains("nowhere to put messages");
    }

    // ------------------------------------------------------------------ options binding ---

    [Test]
    public async Task Storage_options_defaults_hold_when_only_the_connection_string_is_configured()
    {
        var provider = AddQueue(Configuration()).BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<MssqlStorageOptions>>().Value;

        await Assert.That(options.SchemaName).IsEqualTo("dbo");
        await Assert.That(options.QueueTableName).IsEqualTo("SpinneretQueue");
        await Assert.That(options.DeadLetterTableName).IsEqualTo("SpinneretDeadLetters");
        await Assert.That(options.CreateSchema).IsTrue();
    }

    [Test]
    public async Task In_host_options_defaults_hold_when_nothing_is_configured()
    {
        var provider = AddQueue(Configuration(), inHost: true).BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<InHostDeliveryOptions>>().Value;

        await Assert.That(options.PollInterval).IsEqualTo(TimeSpan.FromSeconds(2));
        await Assert.That(options.ChannelParallelism).IsEmpty();
    }

    [Test]
    public async Task Storage_options_bind_from_the_queue_mssql_section()
    {
        var configuration = Configuration(new()
        {
            ["Queue:Mssql:SchemaName"] = "queues",
            ["Queue:Mssql:QueueTableName"] = "MyQueue",
        });

        var provider = AddQueue(configuration).BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<MssqlStorageOptions>>().Value;

        await Assert.That(options.SchemaName).IsEqualTo("queues");
        await Assert.That(options.QueueTableName).IsEqualTo("MyQueue");
    }

    [Test]
    public async Task In_host_options_bind_from_the_queue_in_host_section()
    {
        var configuration = Configuration(new()
        {
            ["Queue:InHost:PollInterval"] = "00:00:05",
            ["Queue:InHost:ChannelParallelism:default"] = "3",
        });

        var provider = AddQueue(configuration, inHost: true).BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<InHostDeliveryOptions>>().Value;

        await Assert.That(options.PollInterval).IsEqualTo(TimeSpan.FromSeconds(5));
        await Assert.That(options.ChannelParallelism["default"]).IsEqualTo(3);
    }

    [Test]
    public async Task Connection_string_resolves_through_the_standard_connection_strings_section()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:QueueConnection"] = "Server=elsewhere;Database=q;Integrated Security=true;",
            ["Queue:Mssql:ConnectionStringName"] = "QueueConnection",
        }).Build();

        var provider = AddQueue(configuration).BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<MssqlStorageOptions>>().Value;

        await Assert.That(options.ConnectionString).Contains("Server=elsewhere");
    }

    // ----------------------------------------------------------------- eager validation ---

    [Test]
    public async Task Missing_connection_string_fails_at_registration()
    {
        var configuration = new ConfigurationBuilder().Build();

        var ex = Assert.Throws<InvalidOperationException>(() => AddQueue(configuration));

        await Assert.That(ex.Message).Contains("Queue:Mssql:ConnectionString");
    }

    [Test]
    public async Task Unresolvable_connection_string_name_fails_at_registration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Queue:Mssql:ConnectionStringName"] = "Nope",
        }).Build();

        var ex = Assert.Throws<InvalidOperationException>(() => AddQueue(configuration));

        await Assert.That(ex.Message).Contains("Queue:Mssql:ConnectionString");
    }

    [Test]
    [Arguments("Queue:Mssql:SchemaName", "bad schema!")]
    [Arguments("Queue:Mssql:QueueTableName", "Robert'); DROP TABLE Students;--")]
    [Arguments("Queue:Mssql:DeadLetterTableName", "[Sneaky]")]
    public async Task Invalid_identifiers_fail_at_registration_naming_the_key(string key, string value)
    {
        var configuration = Configuration(new() { [key] = value });

        var ex = Assert.Throws<InvalidOperationException>(() => AddQueue(configuration));

        await Assert.That(ex.Message).Contains(key);
    }

    [Test]
    public async Task Non_positive_poll_interval_fails_at_registration()
    {
        var configuration = Configuration(new() { ["Queue:InHost:PollInterval"] = "00:00:00" });

        var ex = Assert.Throws<InvalidOperationException>(() => AddQueue(configuration, inHost: true));

        await Assert.That(ex.Message).Contains("Queue:InHost:PollInterval");
    }

    [Test]
    public async Task Parallelism_for_an_undeclared_channel_fails_at_registration()
    {
        var configuration = Configuration(new() { ["Queue:InHost:ChannelParallelism:no-such-channel"] = "2" });

        var ex = Assert.Throws<InvalidOperationException>(() => AddQueue(configuration, inHost: true));

        await Assert.That(ex.Message).Contains("no-such-channel");
    }

    [Test]
    public async Task Parallelism_below_one_fails_at_registration()
    {
        var configuration = Configuration(new() { ["Queue:InHost:ChannelParallelism:default"] = "0" });

        var ex = Assert.Throws<InvalidOperationException>(() => AddQueue(configuration, inHost: true));

        await Assert.That(ex.Message).Contains("at least 1");
    }

    [Test]
    public async Task Parallelism_for_a_declared_channel_passes_validation()
    {
        // "bulk" is declared by BulkChannelCommand's [QueuePolicy].
        var configuration = Configuration(new() { ["Queue:InHost:ChannelParallelism:bulk"] = "2" });

        AddQueue(configuration, inHost: true);
    }
}

internal sealed class CustomBoundary : IQueueDispatchBoundary
{
    public Task ExecuteAsync(QueueDeliveryContext context, Func<Task> dispatch, CancellationToken ct) => dispatch();
}

internal sealed class StubSerializer : IQueuePayloadSerializer
{
    public string Serialize(object request, Type requestType) => "{}";
    public object? Deserialize(string json, Type requestType) => null;
}

internal sealed class StubDeadLetterWriter : IDeadLetterWriter
{
    public Task WriteAsync(DeadLetterEntry entry, CancellationToken ct = default) => Task.CompletedTask;
}
