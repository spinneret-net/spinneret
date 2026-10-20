using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Spinneret.Queue.CloudTasks;

// ASP.NET's JSON options, so payloads round-trip with the same converters as the host's APIs.
internal sealed class HostJsonPayloadSerializer(IOptions<JsonOptions> jsonOptions) : IQueuePayloadSerializer
{
    public string Serialize(object request, Type requestType)
        => JsonSerializer.Serialize(request, requestType, jsonOptions.Value.SerializerOptions);

    public object? Deserialize(string json, Type requestType)
        => JsonSerializer.Deserialize(json, requestType, jsonOptions.Value.SerializerOptions);
}
