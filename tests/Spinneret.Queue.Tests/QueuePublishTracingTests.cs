using System.Diagnostics;

namespace Spinneret.Queue.Tests;

// Tags are literals because docs/queue.md publishes them; unique request types let each test find its span.
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
        // The message id is the transport's to assign, which the producer doesn't know.
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
        // Nested and closed generic FullNames break a naive split on '.'.
        using var spans = new SpanCollector();

        using (QueueTracing.StartProducer("default", Envelope(requestTypeName), dedupeKey: null))
        {
        }

        await Assert.That(spans.TaggedWith("spinneret.request.type", requestTypeName).DisplayName)
            .IsEqualTo(expected);
    }
}
