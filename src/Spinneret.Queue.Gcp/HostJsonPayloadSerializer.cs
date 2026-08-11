using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Spinneret.Queue.Gcp;

/// <summary>
/// Serializes mediator request payloads using the host's configured
/// <see cref="JsonSerializerOptions"/> so converters such as the NodaTime, Input, and
/// ValueArray ones round-trip identically to how the wire APIs handle them.
/// </summary>
internal sealed class HostJsonPayloadSerializer(IOptions<JsonOptions> jsonOptions) : IQueuePayloadSerializer
{
    public string Serialize(object request, Type requestType)
        => JsonSerializer.Serialize(request, requestType, jsonOptions.Value.SerializerOptions);

    public object? Deserialize(string json, Type requestType)
        => JsonSerializer.Deserialize(json, requestType, jsonOptions.Value.SerializerOptions);
}
