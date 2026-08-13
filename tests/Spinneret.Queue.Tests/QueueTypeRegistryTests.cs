namespace Spinneret.Queue.Tests;

public class QueueTypeRegistryTests
{
    private static QueueTypeRegistry CreateRegistry() =>
        new([typeof(QueueTypeRegistryTests).Assembly]);

    [Test]
    public async Task GetPolicy_unannotated_command_returns_default_policy()
    {
        var registry = CreateRegistry();

        var policy = registry.GetPolicy(typeof(UnannotatedCommand));

        await Assert.That(policy).IsEqualTo(QueuePolicy.Default);
    }

    [Test]
    public async Task GetPolicy_annotated_command_parses_every_attribute_property()
    {
        var registry = CreateRegistry();

        var policy = registry.GetPolicy(typeof(AnnotatedCommand));

        await Assert.That(policy.Channel).IsEqualTo("test-channel");
        await Assert.That(policy.ResolvedChannel).IsEqualTo("test-channel");
        await Assert.That(policy.MaxAttempts).IsEqualTo(2);
        await Assert.That(policy.MaxAge).IsEqualTo(TimeSpan.FromHours(1));
        await Assert.That(policy.MinBackoff).IsEqualTo(TimeSpan.FromSeconds(5));
        await Assert.That(policy.MaxBackoff).IsEqualTo(TimeSpan.FromMinutes(1));
        await Assert.That(policy.OnErrorResult).IsEqualTo(ErrorResultAction.Discard);
        await Assert.That(policy.OnExhausted).IsEqualTo(ExhaustedAction.DeadLetter);
    }

    [Test]
    public async Task Resolve_registered_type_name_returns_request_response_and_policy()
    {
        var registry = CreateRegistry();

        var entry = registry.Resolve(typeof(SingleResultCommand).FullName!);

        await Assert.That(entry.RequestType).IsEqualTo(typeof(SingleResultCommand));
        await Assert.That(entry.ResponseType).IsEqualTo(typeof(Spinneret.Functional.Result<string>));
        await Assert.That(entry.Policy).IsEqualTo(QueuePolicy.Default);
    }

    [Test]
    public async Task Resolve_annotated_type_name_returns_declared_channel()
    {
        var registry = CreateRegistry();

        var entry = registry.Resolve(typeof(AnnotatedCommand).FullName!);

        await Assert.That(entry.Policy.Channel).IsEqualTo("test-channel");
    }

    [Test]
    public async Task Resolve_unknown_type_name_throws_unknown_request_type()
    {
        var registry = CreateRegistry();

        var ex = Assert.Throws<UnknownRequestTypeException>(() => registry.Resolve("No.Such.Type"));

        await Assert.That(ex.Message).Contains("No.Such.Type");
        await Assert.That(ex.Message).Contains("out of sync");
    }

    [Test]
    public async Task GetName_registered_type_returns_full_name()
    {
        var registry = CreateRegistry();

        var name = registry.GetName(typeof(UnannotatedCommand));

        await Assert.That(name).IsEqualTo(typeof(UnannotatedCommand).FullName);
    }

    [Test]
    public async Task GetName_unregistered_type_throws_invalid_operation()
    {
        var registry = CreateRegistry();

        var ex = Assert.Throws<InvalidOperationException>(() => registry.GetName(typeof(string)));

        await Assert.That(ex.Message).Contains("not registered");
    }

    [Test]
    public async Task GetName_abstract_request_type_is_not_registered()
    {
        var registry = CreateRegistry();

        var ex = Assert.Throws<InvalidOperationException>(() => registry.GetName(typeof(AbstractCommand)));

        await Assert.That(ex.Message).Contains("not registered");
    }

    [Test]
    public async Task DeclaredChannels_contains_each_declared_channel_without_default()
    {
        var registry = CreateRegistry();

        var channels = registry.DeclaredChannels;

        await Assert.That(channels).Contains("test-channel");
        await Assert.That(channels).Contains("bulk");
        await Assert.That(channels.Contains("default")).IsFalse();
    }

    [Test]
    public async Task Constructor_attribute_with_default_values_yields_default_policy()
    {
        var assembly = DynamicRequestAssembly.WithRequest(
            "Dyn.DefaultAttributeCommand", ("MaxAttempts", QueuePolicy.DefaultMaxAttempts));

        var registry = new QueueTypeRegistry([assembly]);

        await Assert.That(registry.Resolve("Dyn.DefaultAttributeCommand").Policy).IsEqualTo(QueuePolicy.Default);
    }

    [Test]
    [Arguments("not-a-timespan")]
    [Arguments("-00:01:00")]
    [Arguments("00:00:00")]
    public async Task Constructor_invalid_max_age_fails_at_startup(string maxAge)
    {
        var assembly = DynamicRequestAssembly.WithRequest("Dyn.BadMaxAgeCommand", ("MaxAge", maxAge));

        var ex = Assert.Throws<InvalidOperationException>(() => new QueueTypeRegistry([assembly]));

        await Assert.That(ex.Message).Contains("Dyn.BadMaxAgeCommand");
        await Assert.That(ex.Message).Contains("MaxAge");
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task Constructor_non_positive_max_attempts_fails_at_startup(int maxAttempts)
    {
        var assembly = DynamicRequestAssembly.WithRequest("Dyn.BadAttemptsCommand", ("MaxAttempts", maxAttempts));

        var ex = Assert.Throws<InvalidOperationException>(() => new QueueTypeRegistry([assembly]));

        await Assert.That(ex.Message).Contains("MaxAttempts");
    }

    [Test]
    public async Task Constructor_invalid_min_backoff_fails_at_startup()
    {
        var assembly = DynamicRequestAssembly.WithRequest("Dyn.BadMinBackoffCommand", ("MinBackoff", "nope"));

        var ex = Assert.Throws<InvalidOperationException>(() => new QueueTypeRegistry([assembly]));

        await Assert.That(ex.Message).Contains("MinBackoff");
    }

    [Test]
    public async Task Constructor_min_backoff_exceeding_max_backoff_fails_at_startup()
    {
        // MinBackoff raised above the (default) MaxBackoff would silently cap every retry at the
        // smaller value — the registry fails the host at boot instead.
        var assembly = DynamicRequestAssembly.WithRequest("Dyn.InvertedBackoffCommand", ("MinBackoff", "00:30:00"));

        var ex = Assert.Throws<InvalidOperationException>(() => new QueueTypeRegistry([assembly]));

        await Assert.That(ex.Message).Contains("MinBackoff");
        await Assert.That(ex.Message).Contains("MaxBackoff");
    }

    [Test]
    public async Task Constructor_duplicate_type_name_across_assemblies_fails_at_startup()
    {
        var first = DynamicRequestAssembly.WithRequest("Dup.Command");
        var second = DynamicRequestAssembly.WithRequest("Dup.Command");

        var ex = Assert.Throws<InvalidOperationException>(() => new QueueTypeRegistry([first, second]));

        await Assert.That(ex.Message).Contains("Duplicate");
        await Assert.That(ex.Message).Contains("Dup.Command");
    }

    [Test]
    public async Task Constructor_command_with_two_request_interfaces_fails_at_startup()
    {
        // A command implementing both IRequest<Unit> and IRequest<string> has an ambiguous
        // response type; the registry rejects it at boot instead of registering whichever
        // interface enumeration happens to yield last.
        var assembly = DynamicRequestAssembly.WithRequest(
            "Dyn.DualInterfaceCommand", [typeof(Spinneret.Functional.Unit), typeof(string)]);

        var ex = Assert.Throws<InvalidOperationException>(() => new QueueTypeRegistry([assembly]));

        await Assert.That(ex.Message).Contains("Dyn.DualInterfaceCommand");
        await Assert.That(ex.Message).Contains("multiple response types");
    }

    [Test]
    public async Task Constructor_same_assembly_registered_twice_is_not_a_duplicate()
    {
        var assembly = typeof(QueueTypeRegistryTests).Assembly;

        var registry = new QueueTypeRegistry([assembly, assembly]);

        await Assert.That(registry.GetPolicy(typeof(UnannotatedCommand))).IsEqualTo(QueuePolicy.Default);
    }
}
