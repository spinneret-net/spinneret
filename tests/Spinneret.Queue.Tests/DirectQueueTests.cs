using Microsoft.Extensions.DependencyInjection;

namespace Spinneret.Queue.Tests;

public class DirectQueueTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly RecordingTransport _transport = new();

    private (IQueue Queue, IEnvelopeQueue Envelopes) Create()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IQueueTransport>(_transport);
        services.AddSingleton<IQueuePayloadSerializer>(new FakeSerializer());
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
        services.AddNullLogging();
        services.AddQueueCore([typeof(DirectQueueTests).Assembly]);
        services.AddSingleton<DirectQueue>();

        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<DirectQueue>(), provider.GetRequiredService<DirectQueue>());
    }

    [Test]
    public async Task Enqueue_sends_the_envelope_on_the_request_types_channel()
    {
        var (queue, _) = Create();

        await queue.Enqueue(new SecondChannelCommand(), new QueueOptions { Description = "why" });

        var message = Expect.Single(_transport.Sent);
        await Assert.That(message.Channel).IsEqualTo("bulk");
        await Assert.That(message.Envelope.RequestTypeName).IsEqualTo(typeof(SecondChannelCommand).FullName!);
        await Assert.That(message.Envelope.EnqueuedAtUtc).IsEqualTo(Now);
        await Assert.That(message.Envelope.Description).IsEqualTo("why");
        await Assert.That(message.DeliverAt).IsNull();
        await Assert.That(message.MessageName).IsNull();
    }

    [Test]
    public async Task Delay_becomes_an_absolute_delivery_time_from_the_clock()
    {
        var (queue, _) = Create();

        await queue.Enqueue(new UnannotatedCommand(), new QueueOptions { Delay = TimeSpan.FromMinutes(10) });

        await Assert.That(Expect.Single(_transport.Sent).DeliverAt).IsEqualTo(Now.AddMinutes(10));
    }

    [Test]
    public async Task Dedupe_key_becomes_the_message_name()
    {
        var (queue, _) = Create();

        await queue.Enqueue(new UnannotatedCommand(), new QueueOptions { DedupeKey = "order-7" });

        await Assert.That(Expect.Single(_transport.Sent).MessageName).IsEqualTo("order-7");
    }

    [Test]
    public async Task A_duplicate_the_transport_already_holds_is_success()
    {
        var (queue, _) = Create();
        _transport.Created = false;

        await queue.Enqueue(new UnannotatedCommand(), new QueueOptions { DedupeKey = "order-7" });

        await Assert.That(_transport.Sent).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Re_enqueued_envelope_keeps_its_history_and_gets_no_name()
    {
        // Anonymous so it can't collide with the task it replaces.
        var (_, envelopes) = Create();
        var envelope = new QueueEnvelope
        {
            RequestTypeName = typeof(UnannotatedCommand).FullName!,
            PayloadJson = "{}",
            EnqueuedAtUtc = Now.AddHours(-1),
            PriorFailures = 2,
        };

        await envelopes.Enqueue(envelope, TimeSpan.FromSeconds(30));

        var message = Expect.Single(_transport.Sent);
        await Assert.That(message.MessageName).IsNull();
        await Assert.That(message.DeliverAt).IsEqualTo(Now.AddSeconds(30));
        await Assert.That(message.Envelope.PriorFailures).IsEqualTo(2);
        await Assert.That(message.Envelope.EnqueuedAtUtc).IsEqualTo(Now.AddHours(-1));
    }

    [Test]
    public async Task Transport_failure_propagates_to_the_producer()
    {
        var (queue, _) = Create();
        _transport.Throw = new InvalidOperationException("transport down");

        await Assert.That(async () => await queue.Enqueue(new UnannotatedCommand()))
            .Throws<InvalidOperationException>();
    }

    private sealed class RecordingTransport : IQueueTransport
    {
        public List<OutboundMessage> Sent { get; } = [];
        public bool Created { get; set; } = true;
        public Exception? Throw { get; set; }

        public Task<bool> SendAsync(OutboundMessage message, CancellationToken ct)
        {
            if (Throw is not null)
                return Task.FromException<bool>(Throw);

            Sent.Add(message);
            return Task.FromResult(Created);
        }
    }
}
