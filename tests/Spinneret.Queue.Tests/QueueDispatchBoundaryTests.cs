using Microsoft.Extensions.DependencyInjection;

namespace Spinneret.Queue.Tests;

/// <summary>
/// The dispatch boundary brackets exactly the handler invocation — transports rely on it to scope
/// transactional state (e.g. a savepoint) to the handler alone, with the processor's own booking
/// happening outside the bracket.
/// </summary>
public class QueueDispatchBoundaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeDispatcher _dispatcher = new();
    private readonly FakeEnvelopeQueue _envelopeQueue = new();
    private readonly FakeDeadLetterWriter _deadLetters = new();
    private readonly RecordingBoundary _boundary = new();

    private IQueueDeliveryProcessor CreateProcessor()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IQueueDispatcher>(_dispatcher);
        services.AddSingleton<IQueueDispatchBoundary>(_boundary);
        services.AddSingleton<IEnvelopeQueue>(_envelopeQueue);
        services.AddSingleton<IDeadLetterWriter>(_deadLetters);
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
        services.AddNullLogging();
        services.AddQueueCore(new QueueTypeRegistry([typeof(QueueDispatchBoundaryTests).Assembly]));

        return services.BuildServiceProvider().GetRequiredService<IQueueDeliveryProcessor>();
    }

    private static QueueEnvelope Envelope<TCommand>(int priorFailures = 0) => new()
    {
        RequestTypeName = typeof(TCommand).FullName!,
        PayloadJson = """{"id":42}""",
        EnqueuedAtUtc = Now,
        PriorFailures = priorFailures,
    };

    [Test]
    public async Task ProcessAsync_invokes_the_handler_through_the_boundary()
    {
        var outcome = await CreateProcessor().ProcessAsync(
            Envelope<UnannotatedCommand>(), "task-1", CancellationToken.None);

        await Assert.That(outcome.Ack).IsTrue();
        await Assert.That(_boundary.Calls).IsEqualTo(1);
        await Assert.That(_dispatcher.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task ProcessAsync_handler_failure_propagates_through_the_boundary_and_still_books_the_retry()
    {
        _dispatcher.Throw = new InvalidOperationException("boom");

        var outcome = await CreateProcessor().ProcessAsync(
            Envelope<UnannotatedCommand>(), "task-1", CancellationToken.None);

        // The boundary observed the failure (its cue to roll back to a savepoint), and the
        // processor still booked the retry — outside the bracket.
        await Assert.That(outcome.Ack).IsTrue();
        await Assert.That(Expect.Single(_boundary.Observed)).IsSameReferenceAs(_dispatcher.Throw);
        await Assert.That(Expect.Single(_envelopeQueue.Enqueued).Envelope.PriorFailures).IsEqualTo(1);
    }

    [Test]
    public async Task ProcessAsync_boundary_receives_the_delivered_envelope()
    {
        var envelope = Envelope<UnannotatedCommand>();

        await CreateProcessor().ProcessAsync(envelope, "task-1", CancellationToken.None);

        await Assert.That(Expect.Single(_boundary.Envelopes)).IsEqualTo(envelope);
    }

    [Test]
    public async Task AddQueueCore_registers_a_scoped_pass_through_boundary()
    {
        var services = new ServiceCollection();

        services.AddQueueCore([typeof(QueueDispatchBoundaryTests).Assembly]);
        var descriptor = services.Single(d => d.ServiceType == typeof(IQueueDispatchBoundary));

        await Assert.That(descriptor.Lifetime).IsEqualTo(ServiceLifetime.Scoped);
        await Assert.That(descriptor.ImplementationType!.Name).IsEqualTo("DirectDispatchBoundary");
    }

    [Test]
    public async Task AddQueueCore_respects_a_host_registered_boundary()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IQueueDispatchBoundary>(_boundary);

        services.AddQueueCore([typeof(QueueDispatchBoundaryTests).Assembly]);
        var provider = services.BuildServiceProvider();

        await Assert.That(provider.GetRequiredService<IQueueDispatchBoundary>())
            .IsSameReferenceAs(_boundary);
    }
}

internal sealed class RecordingBoundary : IQueueDispatchBoundary
{
    public int Calls { get; private set; }
    public List<QueueEnvelope> Envelopes { get; } = [];
    public List<Exception> Observed { get; } = [];

    public async Task ExecuteAsync(QueueDeliveryContext context, Func<Task> dispatch, CancellationToken ct)
    {
        Calls++;
        Envelopes.Add(context.Envelope);
        try
        {
            await dispatch();
        }
        catch (Exception ex)
        {
            Observed.Add(ex);
            throw;
        }
    }
}
