using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Spinneret.Queue.Gcp.Tests;

public sealed class HostJsonPayloadSerializerTests
{
    private static IQueuePayloadSerializer CreateSerializer(Action<JsonOptions>? configureJson = null)
    {
        var provider = TestSetup.BuildProvider(configure: services =>
        {
            if (configureJson is not null)
                services.Configure(configureJson);
        });

        return provider.GetRequiredService<IQueuePayloadSerializer>();
    }

    [Test]
    public async Task Serialize_uses_host_web_defaults_with_camel_casing()
    {
        var serializer = CreateSerializer();
        var command = new PlainCommand("widget", 3);

        var json = serializer.Serialize(command, typeof(PlainCommand));

        await Assert.That(json).Contains("\"name\":\"widget\"");
        await Assert.That(json).Contains("\"count\":3");
    }

    [Test]
    public async Task Serialize_honors_host_configured_naming_policy()
    {
        var serializer = CreateSerializer(json => json.SerializerOptions.PropertyNamingPolicy = null);
        var command = new PlainCommand("widget", 3);

        var json = serializer.Serialize(command, typeof(PlainCommand));

        await Assert.That(json).Contains("\"Name\":\"widget\"");
        await Assert.That(json).Contains("\"Count\":3");
    }

    [Test]
    public async Task Round_trip_preserves_record_equality()
    {
        var serializer = CreateSerializer();
        var command = new PlainCommand("round-trip", 42);

        var json = serializer.Serialize(command, typeof(PlainCommand));
        var deserialized = serializer.Deserialize(json, typeof(PlainCommand));

        await Assert.That(deserialized).IsEqualTo(command);
    }

    [Test]
    public async Task Deserialize_is_case_insensitive_with_web_defaults()
    {
        var serializer = CreateSerializer();

        var deserialized = serializer.Deserialize("""{"NAME":"shouty","COUNT":7}""", typeof(PlainCommand));

        await Assert.That(deserialized).IsEqualTo(new PlainCommand("shouty", 7));
    }

    [Test]
    public async Task Deserialize_null_json_returns_null()
    {
        var serializer = CreateSerializer();

        var deserialized = serializer.Deserialize("null", typeof(PlainCommand));

        await Assert.That(deserialized).IsNull();
    }
}
