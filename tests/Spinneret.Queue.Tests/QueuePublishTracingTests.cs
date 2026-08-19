using System.Diagnostics;

namespace Spinneret.Queue.Tests;

/// <summary>
/// The producer span, asserted on <see cref="QueueTracing"/> itself: every transport publishes
/// through it, so the shape is pinned once rather than per transport.
/// </summary>
/// <remarks>
/// Tags are spelled as literals, not as the <c>QueueTags</c> consts the production code uses,
/// because docs/queue.md publishes these strings. Each test uses a request type name no other test
/// uses, which is what lets the <see cref="SpanCollector"/> find its own span among the ones TUnit's
/// parallel tests are producing.
/// </remarks>
public class QueuePublishTracingTests
{
    private static QueueEnvelope Envelope(string requestTypeName) => new()
    {
        RequestTypeName = requestTypeName,
        PayloadJson = """{"id":42}""",
        EnqueuedAtUtc = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero),
    };

    [Test]
    public async Task The_span_is_named_for_the_request_and_carries_the_channel()
    {
        const string requestType = "Publish.Tests.Named.SendWelcomeEmail";
        using var spans = new SpanCollector();

        using (QueueTracing.StartProducer("bulk", Envelope(requestType), dedupeKey: null))
        {
        }

        var span = spans.TaggedWith("spinneret.request.type", requestType);
        await Assert.That(span.DisplayName).IsEqualTo("SendWelcomeEmail publish");
        await Assert.That(span.Kind).IsEqualTo(ActivityKind.Producer);
        await Assert.That(span.GetTagItem("messaging.system")).IsEqualTo("spinneret");
        await Assert.That(span.GetTagItem("messaging.operation")).IsEqualTo("publish");
        await Assert.That(span.GetTagItem("messaging.destination.name")).IsEqualTo("bulk");
    }

    [Test]
    public async Task A_dedupe_key_is_its_own_tag_and_never_the_message_id()
    {
        // messaging.message.id means the id the transport assigned, which the producer does not know:
        // Cloud Tasks builds its task name from the dedupe key, so the two coincide there, while the
        // MSSQL transport hands back an identity column value that has nothing to do with it.
        const string requestType = "Publish.Tests.Deduped.SendWelcomeEmail";
        using var spans = new SpanCollector();

        using (QueueTracing.StartProducer("default", Envelope(requestType), "welcome-email-4417"))
        {
        }

        var span = spans.TaggedWith("spinneret.request.type", requestType);
        await Assert.That(span.GetTagItem("spinneret.queue.dedupe_key")).IsEqualTo("welcome-email-4417");
        await Assert.That(span.GetTagItem("messaging.message.id")).IsNull();
    }

    [Test]
    public async Task An_enqueue_without_a_dedupe_key_leaves_the_tag_off()
    {
        const string requestType = "Publish.Tests.Undeduped.SendWelcomeEmail";
        using var spans = new SpanCollector();

        using (QueueTracing.StartProducer("default", Envelope(requestType), dedupeKey: null))
        {
        }

        await Assert.That(spans.TaggedWith("spinneret.request.type", requestType)
            .GetTagItem("spinneret.queue.dedupe_key")).IsNull();
    }

    [Test]
    [Arguments("MyApp.Commands.SendWelcomeEmail", "SendWelcomeEmail publish")]
    [Arguments("MyApp.Commands.Outer+Nested", "Nested publish")]
    [Arguments("NoNamespaceCommand", "NoNamespaceCommand publish")]
    [Arguments("MyApp.Commands.Generic`1[[System.Int32, System.Private.CoreLib]]", "Generic`1 publish")]
    public async Task The_name_survives_whatever_shape_the_registered_type_name_has(
        string requestTypeName, string expected)
    {
        // The registry hands over Type.FullName, and a nested or closed generic type spells that in
        // ways a naive split on '.' turns into gibberish.
        using var spans = new SpanCollector();

        using (QueueTracing.StartProducer("default", Envelope(requestTypeName), dedupeKey: null))
        {
        }

        await Assert.That(spans.TaggedWith("spinneret.request.type", requestTypeName).DisplayName)
            .IsEqualTo(expected);
    }
}
