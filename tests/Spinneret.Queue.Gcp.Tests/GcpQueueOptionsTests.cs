namespace Spinneret.Queue.Gcp.Tests;

public sealed class GcpQueueOptionsTests
{
    [Test]
    public async Task Defaults_are_empty_strings_and_null_optionals()
    {
        var options = new GcpQueueOptions();

        await Assert.That(options.ProjectId).IsEqualTo(string.Empty);
        await Assert.That(options.LocationId).IsEqualTo(string.Empty);
        await Assert.That(options.DispatcherUrl).IsEqualTo(string.Empty);
        await Assert.That(options.ServiceAccountEmail).IsEqualTo(string.Empty);
        await Assert.That(options.Channels).IsEmpty();
        await Assert.That(options.OidcAudience).IsNull();
        await Assert.That(options.OidcIssuer).IsNull();
        await Assert.That(options.EmulatorEndpoint).IsNull();
    }

    [Test]
    public async Task SectionName_is_queue_gcp()
    {
        await Assert.That(GcpQueueOptions.SectionName).IsEqualTo("Queue:Gcp");
    }

    [Test]
    [Arguments(null, false)]
    [Arguments("", false)]
    [Arguments("   ", false)]
    [Arguments("localhost:8123", true)]
    public async Task UsesEmulator_reflects_whether_endpoint_is_set(string? endpoint, bool expected)
    {
        var options = new GcpQueueOptions { EmulatorEndpoint = endpoint };

        await Assert.That(options.UsesEmulator).IsEqualTo(expected);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task ResolvedOidcAudience_falls_back_to_dispatcher_url_when_audience_blank(string? audience)
    {
        var options = new GcpQueueOptions
        {
            DispatcherUrl = "https://worker.example.com/dispatch",
            OidcAudience = audience,
        };

        await Assert.That(options.ResolvedOidcAudience).IsEqualTo("https://worker.example.com/dispatch");
    }

    [Test]
    public async Task ResolvedOidcAudience_uses_explicit_audience_when_set()
    {
        var options = new GcpQueueOptions
        {
            DispatcherUrl = "https://worker.example.com/dispatch",
            OidcAudience = "custom-audience",
        };

        await Assert.That(options.ResolvedOidcAudience).IsEqualTo("custom-audience");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task ResolvedOidcIssuer_defaults_to_google_accounts_when_issuer_blank(string? issuer)
    {
        var options = new GcpQueueOptions { OidcIssuer = issuer };

        await Assert.That(options.ResolvedOidcIssuer).IsEqualTo("https://accounts.google.com");
    }

    [Test]
    public async Task ResolvedOidcIssuer_uses_explicit_issuer_when_set()
    {
        var options = new GcpQueueOptions { OidcIssuer = "http://localhost:9099" };

        await Assert.That(options.ResolvedOidcIssuer).IsEqualTo("http://localhost:9099");
    }

    [Test]
    public async Task QueueIdFor_returns_mapped_queue_id()
    {
        var options = new GcpQueueOptions
        {
            Channels = { ["default"] = "default-queue", ["bulk"] = "bulk-queue" },
        };

        await Assert.That(options.QueueIdFor("bulk")).IsEqualTo("bulk-queue");
    }

    [Test]
    public async Task QueueIdFor_unmapped_channel_throws_with_channel_name()
    {
        var options = new GcpQueueOptions
        {
            Channels = { ["default"] = "default-queue" },
        };

        var ex = Assert.Throws<InvalidOperationException>(() => options.QueueIdFor("missing"));

        await Assert.That(ex.Message).Contains("missing");
        await Assert.That(ex.Message).Contains("Queue:Gcp:Channels:missing");
    }
}
