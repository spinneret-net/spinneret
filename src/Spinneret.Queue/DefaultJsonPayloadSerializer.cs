using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Spinneret.Queue;

/// <summary>
/// Serializes payloads with the host's <see cref="JsonSerializerOptions"/> when one is registered
/// as a singleton (the convention for non-web hosts to share converter setup — NodaTime and the
/// like), falling back to the defaults. The queue's default when neither the host
/// (<c>UsePayloadSerializer</c>) nor the delivery chose one.
/// </summary>
internal sealed class DefaultJsonPayloadSerializer(IServiceProvider services) : IQueuePayloadSerializer
{
    private readonly JsonSerializerOptions _options =
        services.GetService<JsonSerializerOptions>() ?? JsonSerializerOptions.Default;

    public string Serialize(object request, Type requestType) =>
        JsonSerializer.Serialize(request, requestType, _options);

    public object? Deserialize(string json, Type requestType) =>
        JsonSerializer.Deserialize(json, requestType, _options);
}
