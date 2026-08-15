using Spinneret.Functional;
using Spinneret.Mediator;
using Spinneret.Queue;

namespace Spinneret.Scheduler.Firestore.Tests;

// ---------------------------------------------------------------------------------------------
// Request types scanned by QueueTypeRegistry from this assembly, plus hand-rolled fakes for the
// payload serializer and clock. No mocking library is used.
// ---------------------------------------------------------------------------------------------

public sealed record TestRequest(string Name) : IRequest<Unit>;

public sealed record OtherTestRequest(int Number) : IRequest<Unit>;

public sealed class FakePayloadSerializer : IQueuePayloadSerializer
{
    public string SerializeResult { get; set; } = "{}";
    public object? DeserializeResult { get; set; }
    public List<(object Request, Type RequestType)> SerializeCalls { get; } = [];
    public List<(string Json, Type RequestType)> DeserializeCalls { get; } = [];

    public string Serialize(object request, Type requestType)
    {
        SerializeCalls.Add((request, requestType));
        return SerializeResult;
    }

    public object? Deserialize(string json, Type requestType)
    {
        DeserializeCalls.Add((json, requestType));
        return DeserializeResult;
    }
}

/// <summary>A clock frozen at a known instant, so timestamps written into documents are assertable.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
