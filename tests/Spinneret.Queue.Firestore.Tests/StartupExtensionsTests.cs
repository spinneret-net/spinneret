using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Spinneret.Queue.Firestore.Tests;

public class StartupExtensionsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();

    [Test]
    public async Task Registers_the_writer_as_the_dead_letter_writer()
    {
        var services = new ServiceCollection();

        services.AddFirestoreDeadLetters();

        var descriptor = services.Single(d => d.ServiceType == typeof(IDeadLetterWriter));
        await Assert.That(descriptor.ImplementationType?.Name).IsEqualTo("FirestoreDeadLetterWriter");
        await Assert.That(descriptor.Lifetime).IsEqualTo(ServiceLifetime.Singleton);
    }

    [Test]
    public async Task Defaults_the_collection_to_dead_letters()
    {
        var services = new ServiceCollection();
        services.AddFirestoreDeadLetters();

        var options = services.BuildServiceProvider()
            .GetRequiredService<IOptions<FirestoreDeadLetterOptions>>().Value;

        await Assert.That(options.Collection).IsEqualTo("dead_letters");
    }

    [Test]
    public async Task Binds_the_collection_from_configuration()
    {
        var services = new ServiceCollection();
        services.AddFirestoreDeadLetters(Config(("Queue:Firestore:Collection", "failed_tasks")));

        var options = services.BuildServiceProvider()
            .GetRequiredService<IOptions<FirestoreDeadLetterOptions>>().Value;

        await Assert.That(options.Collection).IsEqualTo("failed_tasks");
    }

    [Test]
    public async Task A_host_registered_writer_wins()
    {
        // TryAdd, so a host that registered its own writer first keeps it — the documented contract
        // for every Spinneret default.
        var services = new ServiceCollection();
        services.AddSingleton<IDeadLetterWriter, FakeDeadLetterWriter>();

        services.AddFirestoreDeadLetters();

        var descriptor = services.Single(d => d.ServiceType == typeof(IDeadLetterWriter));
        await Assert.That(descriptor.ImplementationType).IsEqualTo(typeof(FakeDeadLetterWriter));
    }

    [Test]
    public async Task A_blank_collection_fails_validation()
    {
        var services = new ServiceCollection();
        services.AddFirestoreDeadLetters(o => o.Collection = "  ");

        var options = services.BuildServiceProvider()
            .GetRequiredService<IOptions<FirestoreDeadLetterOptions>>();

        var ex = Assert.Throws<OptionsValidationException>(() => _ = options.Value);
        await Assert.That(ex.Message).Contains("Queue:Firestore:Collection");
    }

    [Test]
    public async Task Section_name_is_queue_firestore()
    {
        await Assert.That(FirestoreDeadLetterOptions.SectionName).IsEqualTo("Queue:Firestore");
    }

    private sealed class FakeDeadLetterWriter : IDeadLetterWriter
    {
        public Task WriteAsync(DeadLetterEntry entry, CancellationToken ct = default) => Task.CompletedTask;
    }
}
