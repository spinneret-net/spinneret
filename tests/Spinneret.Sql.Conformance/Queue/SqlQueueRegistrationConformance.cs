using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Spinneret.Queue;
using Spinneret.Queue.Sql;

namespace Spinneret.Sql.Conformance;

public abstract class SqlQueueRegistrationConformance(SqlTestDialect dialect)
{
    protected SqlTestDialect Dialect => dialect;

    private string Section => dialect.StorageSection;

    private IConfiguration Configuration(Dictionary<string, string?>? overrides = null, bool withConnectionString = true)
    {
        var settings = new Dictionary<string, string?>();
        if (withConnectionString)
            settings[$"{Section}:ConnectionString"] = dialect.SampleConnectionString;
        foreach (var (key, value) in overrides ?? [])
            settings[key] = value;

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private IServiceCollection AddQueue(
        IConfiguration configuration, IServiceCollection? services = null, bool inHost = false)
    {
        services ??= new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        return services.AddQueue(q =>
        {
            dialect.UseStorage(q.AddRequestAssemblies(typeof(SqlQueueRegistrationConformance).Assembly), configuration);
            if (inHost)
                q.UseInHostDelivery(configuration);
        });
    }

    [Test]
    public async Task Storage_exposes_producer_envelope_and_transactional_queue_as_one_instance()
    {
        var provider = AddQueue(Configuration()).BuildServiceProvider();

        var queue = provider.GetRequiredService<IQueue>();
        await Assert.That(queue).IsTypeOf<SqlOutboxQueue>();
        await Assert.That(provider.GetRequiredService<IEnvelopeQueue>()).IsSameReferenceAs(queue);
        await Assert.That(provider.GetRequiredService<ISqlTransactionalQueue>()).IsSameReferenceAs(queue);
    }

    [Test]
    public async Task Storage_reports_its_dialect()
    {
        var provider = AddQueue(Configuration()).BuildServiceProvider();

        await Assert.That(provider.GetRequiredService<SqlQueueStorage>().Dialect.Name).IsEqualTo(dialect.Name);
    }

    [Test]
    public async Task Storage_registers_the_savepoint_dispatch_boundary()
    {
        var services = AddQueue(Configuration());

        var descriptor = services.Single(d => d.ServiceType == typeof(IQueueDispatchBoundary));

        await Assert.That(descriptor.Lifetime).IsEqualTo(ServiceLifetime.Scoped);
        await Assert.That(descriptor.ImplementationType).IsEqualTo(typeof(SavepointDispatchBoundary));
    }

    [Test]
    public async Task Storage_runs_units_of_work_in_a_sql_transaction()
    {
        var provider = AddQueue(Configuration()).BuildServiceProvider();

        await Assert.That(provider.GetRequiredService<IQueueUnitOfWorkFactory>()).IsTypeOf<SqlUnitOfWorkFactory>();
    }

    [Test]
    public async Task Storage_respects_a_custom_boundary_the_host_registered()
    {
        var services = new ServiceCollection();
        services.AddScoped<IQueueDispatchBoundary, CustomBoundary>();

        AddQueue(Configuration(), services);
        var descriptor = services.Single(d => d.ServiceType == typeof(IQueueDispatchBoundary));

        await Assert.That(descriptor.ImplementationType).IsEqualTo(typeof(CustomBoundary));
    }

    [Test]
    public async Task Storage_registers_the_table_backed_dead_letters()
    {
        var provider = AddQueue(Configuration()).BuildServiceProvider();

        await Assert.That(provider.GetRequiredService<IDeadLetterWriter>()).IsTypeOf<SqlDeadLetterWriter>();
        await Assert.That(provider.GetRequiredService<IDeadLetterStore>()).IsTypeOf<SqlDeadLetterStore>();
    }

    [Test]
    public async Task Storage_registers_the_ambient_transaction_provider()
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
    public async Task Storage_respects_host_registered_overrides()
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
                d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(SqlQueueWorker)))
            .IsTrue();
    }

    [Test]
    public async Task Storage_without_a_delivery_registers_no_polling_worker()
    {
        // A producer-only host: nothing consuming the storage.
        var services = AddQueue(Configuration());

        await Assert.That(services.Any(d => d.ImplementationType == typeof(SqlQueueWorker))).IsFalse();
    }

    [Test]
    public async Task UseInHostDelivery_without_a_storage_fails_at_registration_naming_the_sql_storages()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddQueue(q => q
            .AddRequestAssemblies(typeof(SqlQueueRegistrationConformance).Assembly)
            .UseInHostDelivery()));

        await Assert.That(ex.Message).Contains("nowhere to put messages");
        await Assert.That(ex.Message).Contains(dialect.UseStorageName);
    }

    [Test]
    public async Task A_host_data_source_is_used_without_a_connection_string()
    {
        // Disposal staying with the host is pinned by the storage's own unit tests.
        await using var hostOwned = dialect.CreateDataSourceFor(dialect.SampleConnectionString);
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddQueue(q => dialect.UseStorage(
            q.AddRequestAssemblies(typeof(SqlQueueRegistrationConformance).Assembly),
            _ => hostOwned,
            Configuration(withConnectionString: false)));

        await using var provider = services.BuildServiceProvider();

        await Assert.That(provider.GetRequiredService<SqlQueueStorage>().DataSource).IsSameReferenceAs(hostOwned);
    }

    [Test]
    public async Task Storage_options_defaults_hold_when_only_the_connection_string_is_configured()
    {
        var provider = AddQueue(Configuration()).BuildServiceProvider();

        var options = dialect.StorageOptions(provider);

        var (schema, queueTable, deadLetterTable) = dialect.DefaultStorageNames;
        await Assert.That(options.SchemaName).IsEqualTo(schema);
        await Assert.That(options.QueueTableName).IsEqualTo(queueTable);
        await Assert.That(options.DeadLetterTableName).IsEqualTo(deadLetterTable);
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
    public async Task Storage_options_bind_from_the_dialects_section()
    {
        var configuration = Configuration(new()
        {
            [$"{Section}:SchemaName"] = "queues",
            [$"{Section}:QueueTableName"] = "MyQueue",
            [$"{Section}:CreateSchema"] = "false",
        });

        var provider = AddQueue(configuration).BuildServiceProvider();
        var options = dialect.StorageOptions(provider);

        await Assert.That(options.SchemaName).IsEqualTo("queues");
        await Assert.That(options.QueueTableName).IsEqualTo("MyQueue");
        await Assert.That(options.CreateSchema).IsFalse();
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
        var configuration = Configuration(withConnectionString: false, overrides: new()
        {
            ["ConnectionStrings:QueueConnection"] = dialect.SampleConnectionString + ";Application Name=elsewhere",
            [$"{Section}:ConnectionStringName"] = "QueueConnection",
        });

        var provider = AddQueue(configuration).BuildServiceProvider();
        var options = dialect.StorageOptions(provider);

        await Assert.That(options.ConnectionString).Contains("elsewhere");
    }

    [Test]
    public async Task Missing_connection_string_fails_at_registration()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => AddQueue(Configuration(withConnectionString: false)));

        await Assert.That(ex.Message).Contains($"{Section}:ConnectionString");
    }

    [Test]
    public async Task Unresolvable_connection_string_name_fails_at_registration()
    {
        var configuration = Configuration(withConnectionString: false, overrides: new()
        {
            [$"{Section}:ConnectionStringName"] = "Nope",
        });

        var ex = Assert.Throws<InvalidOperationException>(() => AddQueue(configuration));

        await Assert.That(ex.Message).Contains($"{Section}:ConnectionString");
    }

    [Test]
    [Arguments("SchemaName", "bad schema!")]
    [Arguments("QueueTableName", "Robert'); DROP TABLE Students;--")]
    [Arguments("DeadLetterTableName", "[Sneaky]")]
    [Arguments("QueueTableName", "\"Quoted\"")]
    [Arguments("QueueTableName", "1StartsWithADigit")]
    public async Task Invalid_identifiers_fail_at_registration_naming_the_key(string setting, string value)
    {
        var key = $"{Section}:{setting}";
        var configuration = Configuration(new() { [key] = value });

        var ex = Assert.Throws<InvalidOperationException>(() => AddQueue(configuration));

        await Assert.That(ex.Message).Contains(key);
    }

    [Test]
    public async Task A_table_name_longer_than_the_database_allows_fails_at_registration()
    {
        var key = $"{Section}:QueueTableName";
        var configuration = Configuration(new() { [key] = new string('q', dialect.MaxIdentifierLength + 1) });

        var ex = Assert.Throws<InvalidOperationException>(() => AddQueue(configuration));

        await Assert.That(ex.Message).Contains(key);
    }

    [Test]
    public async Task A_table_name_that_fits_but_derives_an_index_name_that_does_not_fails_at_registration()
    {
        // The index names built from it are too long: SQL Server refuses the DDL, Postgres truncates them.
        var key = $"{Section}:QueueTableName";
        var configuration = Configuration(new() { [key] = new string('q', dialect.MaxIdentifierLength - 2) });

        var ex = Assert.Throws<InvalidOperationException>(() => AddQueue(configuration));

        await Assert.That(ex.Message).Contains(key);
        await Assert.That(ex.Message).Contains("Shorten the table name");
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
        await Task.CompletedTask;
    }

    private sealed class CustomBoundary : IQueueDispatchBoundary
    {
        public Task ExecuteAsync(QueueDeliveryContext context, Func<Task> dispatch, CancellationToken ct) => dispatch();
    }

    private sealed class StubSerializer : IQueuePayloadSerializer
    {
        public string Serialize(object request, Type requestType) => "{}";
        public object? Deserialize(string json, Type requestType) => null;
    }

    private sealed class StubDeadLetterWriter : IDeadLetterWriter
    {
        public Task WriteAsync(DeadLetterEntry entry, CancellationToken ct = default) => Task.CompletedTask;
    }
}
