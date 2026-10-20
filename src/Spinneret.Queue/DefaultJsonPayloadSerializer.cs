using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Spinneret.Queue;

// Uses a host-registered JsonSerializerOptions singleton when present, so converters are shared.
internal sealed class DefaultJsonPayloadSerializer(IServiceProvider services) : IQueuePayloadSerializer
{
    private readonly JsonSerializerOptions _options =
        services.GetService<JsonSerializerOptions>() ?? JsonSerializerOptions.Default;

    public string Serialize(object request, Type requestType) =>
        JsonSerializer.Serialize(request, requestType, _options);

    public object? Deserialize(string json, Type requestType) =>
        JsonSerializer.Deserialize(json, requestType, _options);
}
