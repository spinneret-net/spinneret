using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Spinneret.Queue;

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
        IConfiguration configuration, IServiceCollection? services = null)
    {
        services ??= new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        return services.AddMssqlQueue(configuration, typeof(StartupExtensionsTests).Assembly);
    }

    // -------------------------------------------------------------------- registrations ---

    [Test]
    public async Task AddMssqlQueue_exposes_producer_envelope_and_transactional_queue_as_one_instance()
    {
        var provider = AddQueue(Configuration()).BuildServiceProvider();

        var queue = provider.GetRequiredService<IQueue>();
        await Assert.That(provider.GetRequiredService<IEnvelopeQueue>()).IsSameReferenceAs(queue);
        await Assert.That(provider.GetRequiredService<IMssqlTransactionalQueue>()).IsSameReferenceAs(queue);
    }

    [Test]
    public async Task AddMssqlQueue_registers_the_savepoint_dispatch_boundary()
    {
        var services = AddQueue(Configuration());

        var descriptor = services.Single(d => d.ServiceType == typeof(IQueueDispatchBoundary));

        await Assert.That(descriptor.Lifetime).IsEqualTo(ServiceLifetime.Scoped);
        await Assert.That(descriptor.ImplementationType).IsEqualTo(typeof(MssqlDispatchBoundary));
    }

    [Test]
    public async Task AddMssqlQueue_replaces_a_pass_through_boundary_registered_earlier()
    {
        // An earlier AddQueueCore (another transport, a direct call) must not silently win with
        // the pass-through boundary — that would drop the savepoint semantics without any error.
        var services = new ServiceCollection();
        services.AddQueueCore(typeof(StartupExtensionsTests).Assembly);

        AddQueue(Configuration(), services);
        var descriptor = services.Single(d => d.ServiceType == typeof(IQueueDispatchBoundary));

        await Assert.That(descriptor.ImplementationType).IsEqualTo(typeof(MssqlDispatchBoundary));
    }

    [Test]
    public async Task AddMssqlQueue_respects_a_custom_boundary_the_host_registered()
    {
        var services = new ServiceCollection();
        services.AddScoped<IQueueDispatchBoundary, CustomBoundary>();

        AddQueue(Configuration(), services);
        var descriptor = services.Single(d => d.ServiceType == typeof(IQueueDispatchBoundary));

        await Assert.That(descriptor.ImplementationType).IsEqualTo(typeof(CustomBoundary));
    }

    [Test]
    public async Task AddMssqlQueue_registers_the_table_backed_dead_letter_writer()
    {
        var provider = AddQueue(Configuration()).BuildServiceProvider();

        await Assert.That(provider.GetRequiredService<IDeadLetterWriter>())
            .IsTypeOf<MssqlDeadLetterWriter>();
    }

    [Test]
    public async Task AddMssqlQueue_registers_the_ambient_transaction_provider()
    {
        var provider = AddQueue(Configuration()).BuildServiceProvider();

        await Assert.That(provider.GetRequiredService<IMssqlTransactionProvider>())
            .IsTypeOf<AsyncLocalMssqlTransactionProvider>();
    }

    [Test]
    public async Task AddMssqlQueue_registers_queue_core_with_the_scanned_registry()
    {
        var provider = AddQueue(Configuration()).BuildServiceProvider();

        var registry = provider.GetRequiredService<QueueTypeRegistry>();

        await Assert.That(registry.GetName(typeof(PingCommand))).IsEqualTo(typeof(PingCommand).FullName!);
    }

    [Test]
    public async Task AddMssqlQueue_respects_host_registered_overrides()
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
    public async Task AddMssqlQueue_twice_does_not_duplicate_the_queue_registration()
    {
        var configuration = Configuration();
        var services = new ServiceCollection();
        AddQueue(configuration, services);
        AddQueue(configuration, services);

        await Assert.That(services.Count(d => d.ServiceType == typeof(MssqlQueue))).IsEqualTo(1);
        await Assert.That(services.Count(d => d.ServiceType == typeof(IQueue))).IsEqualTo(1);
    }

    [Test]
    public async Task AddMssqlQueueWorker_registers_the_polling_worker()
    {
        var services = AddQueue(Configuration());

        services.AddMssqlQueueWorker();

        await Assert.That(services.Any(d =>
                d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
                && d.ImplementationType == typeof(MssqlQueueWorker)))
            .IsTrue();
    }

    [Test]
    public async Task AddMssqlQueue_without_worker_registers_no_polling_worker()
    {
        var services = AddQueue(Configuration());

        await Assert.That(services.Any(d => d.ImplementationType == typeof(MssqlQueueWorker))).IsFalse();
    }

    // ------------------------------------------------------------------ options binding ---

    [Test]
    public async Task Options_defaults_hold_when_only_the_connection_string_is_configured()
    {
        var provider = AddQueue(Configuration()).BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<MssqlQueueOptions>>().Value;

        await Assert.That(options.SchemaName).IsEqualTo("dbo");
        await Assert.That(options.QueueTableName).IsEqualTo("SpinneretQueue");
        await Assert.That(options.DeadLetterTableName).IsEqualTo("SpinneretDeadLetters");
        await Assert.That(options.CreateSchema).IsTrue();
        await Assert.That(options.PollInterval).IsEqualTo(TimeSpan.FromSeconds(2));
        await Assert.That(options.ChannelParallelism).IsEmpty();
    }

    [Test]
    public async Task Options_bind_from_the_queue_mssql_section()
    {
        var configuration = Configuration(new()
        {
            ["Queue:Mssql:SchemaName"] = "queues",
            ["Queue:Mssql:QueueTableName"] = "MyQueue",
            ["Queue:Mssql:PollInterval"] = "00:00:05",
            ["Queue:Mssql:ChannelParallelism:default"] = "3",
        });

        var provider = AddQueue(configuration).BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<MssqlQueueOptions>>().Value;

        await Assert.That(options.SchemaName).IsEqualTo("queues");
        await Assert.That(options.QueueTableName).IsEqualTo("MyQueue");
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
        var options = provider.GetRequiredService<IOptions<MssqlQueueOptions>>().Value;

        await Assert.That(options.ConnectionString).Contains("Server=elsewhere");
    }

    // ----------------------------------------------------------------- eager validation ---

    [Test]
    public async Task Missing_connection_string_fails_at_startup()
    {
        var configuration = new ConfigurationBuilder().Build();

        var ex = Assert.Throws<InvalidOperationException>(() => AddQueue(configuration));

        await Assert.That(ex.Message).Contains("Queue:Mssql:ConnectionString");
    }

    [Test]
    public async Task Unresolvable_connection_string_name_fails_at_startup()
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
    public async Task Invalid_identifiers_fail_at_startup_naming_the_key(string key, string value)
    {
        var configuration = Configuration(new() { [key] = value });

        var ex = Assert.Throws<InvalidOperationException>(() => AddQueue(configuration));

        await Assert.That(ex.Message).Contains(key);
    }

    [Test]
    public async Task Non_positive_poll_interval_fails_at_startup()
    {
        var configuration = Configuration(new() { ["Queue:Mssql:PollInterval"] = "00:00:00" });

        var ex = Assert.Throws<InvalidOperationException>(() => AddQueue(configuration));

        await Assert.That(ex.Message).Contains("Queue:Mssql:PollInterval");
    }

    [Test]
    public async Task Parallelism_for_an_undeclared_channel_fails_at_startup()
    {
        var configuration = Configuration(new() { ["Queue:Mssql:ChannelParallelism:no-such-channel"] = "2" });

        var ex = Assert.Throws<InvalidOperationException>(() => AddQueue(configuration));

        await Assert.That(ex.Message).Contains("no-such-channel");
    }

    [Test]
    public async Task Parallelism_below_one_fails_at_startup()
    {
        var configuration = Configuration(new() { ["Queue:Mssql:ChannelParallelism:default"] = "0" });

        var ex = Assert.Throws<InvalidOperationException>(() => AddQueue(configuration));

        await Assert.That(ex.Message).Contains("at least 1");
    }

    [Test]
    public async Task Parallelism_for_a_declared_channel_passes_validation()
    {
        // "bulk" is declared by BulkChannelCommand's [QueuePolicy].
        var configuration = Configuration(new() { ["Queue:Mssql:ChannelParallelism:bulk"] = "2" });

        AddQueue(configuration);
    }
}

internal sealed class CustomBoundary : IQueueDispatchBoundary
{
    public Task ExecuteAsync(QueueEnvelope envelope, Func<Task> dispatch, CancellationToken ct) => dispatch();
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
