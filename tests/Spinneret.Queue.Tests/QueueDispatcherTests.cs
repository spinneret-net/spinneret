using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Spinneret.Functional;
using Spinneret.Mediator;

namespace Spinneret.Queue.Tests;

/// <summary>
/// Covers QueueDispatcher — internal, so it is resolved the way a host gets it (AddQueueCore +
/// IQueueDispatcher) and the result-to-exception mapping is observed via which handler responses
/// raise QueueHandlerFailedException.
/// </summary>
public class QueueDispatcherTests
{
    private readonly FakeMediator _mediator = new();
    private readonly FakeSerializer _serializer = new();

    private IQueueDispatcher CreateDispatcher()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISpinneretMediator>(_mediator);
        services.AddSingleton<IQueuePayloadSerializer>(_serializer);
        services.AddNullLogging();
        services.AddQueueCore(new QueueTypeRegistry([typeof(QueueDispatcherTests).Assembly]));

        return services.BuildServiceProvider().GetRequiredService<IQueueDispatcher>();
    }

    private static QueueEnvelope Envelope<TCommand>() => new()
    {
        RequestTypeName = typeof(TCommand).FullName!,
        PayloadJson = """{"id":42}""",
        EnqueuedAtUtc = DateTimeOffset.UtcNow,
    };

    // -----------------------------------------------------------------------------------------
    // Happy path
    // -----------------------------------------------------------------------------------------

    [Test]
    public async Task Dispatch_unit_command_deserializes_payload_and_sends_it_to_the_mediator()
    {
        await CreateDispatcher().Dispatch(Envelope<UnannotatedCommand>(), CancellationToken.None);

        await Assert.That(_mediator.Calls).IsEqualTo(1);
        await Assert.That(_mediator.LastRequest).IsNotNull();
        await Assert.That(_mediator.LastRequest!.GetType()).IsEqualTo(typeof(UnannotatedCommand));
    }

    [Test]
    public async Task Dispatch_non_result_response_completes_without_throwing()
    {
        _mediator.Response = "any plain response";

        await CreateDispatcher().Dispatch(Envelope<PlainResponseCommand>(), CancellationToken.None);

        await Assert.That(_mediator.Calls).IsEqualTo(1);
    }

    // -----------------------------------------------------------------------------------------
    // Payload problems are permanent
    // -----------------------------------------------------------------------------------------

    [Test]
    public async Task Dispatch_undeserializable_payload_throws_permanent_exception()
    {
        _serializer.OnDeserialize = (_, _) => throw new JsonException("bad json");
        var dispatcher = CreateDispatcher();

        var ex = await Assert.ThrowsAsync<QueueHandlerPermanentException>(
            () => dispatcher.Dispatch(Envelope<UnannotatedCommand>(), CancellationToken.None));

        await Assert.That(ex!.Message).Contains("cannot be deserialized");
        await Assert.That(ex.Message).Contains(typeof(UnannotatedCommand).FullName!);
        await Assert.That(ex.InnerException).IsNotNull();
    }

    [Test]
    public async Task Dispatch_payload_deserializing_to_null_throws_permanent_exception()
    {
        _serializer.OnDeserialize = (_, _) => null;
        var dispatcher = CreateDispatcher();

        var ex = await Assert.ThrowsAsync<QueueHandlerPermanentException>(
            () => dispatcher.Dispatch(Envelope<UnannotatedCommand>(), CancellationToken.None));

        await Assert.That(ex!.Message).Contains("deserialized to null");
        await Assert.That(_mediator.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Dispatch_serializer_throwing_non_json_exception_propagates_unwrapped()
    {
        _serializer.OnDeserialize = (_, _) => throw new InvalidOperationException("infra hiccup");
        var dispatcher = CreateDispatcher();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => dispatcher.Dispatch(Envelope<UnannotatedCommand>(), CancellationToken.None));

        await Assert.That(ex!.Message).IsEqualTo("infra hiccup");
    }

    [Test]
    public async Task Dispatch_unknown_request_type_throws_unknown_request_type_before_deserializing()
    {
        var dispatcher = CreateDispatcher();
        var envelope = Envelope<UnannotatedCommand>() with { RequestTypeName = "No.Such.Type" };

        await Assert.ThrowsAsync<UnknownRequestTypeException>(
            () => dispatcher.Dispatch(envelope, CancellationToken.None));

        await Assert.That(_mediator.Calls).IsEqualTo(0);
    }

    // -----------------------------------------------------------------------------------------
    // Result introspection of handler responses
    // -----------------------------------------------------------------------------------------

    [Test]
    public async Task Dispatch_single_generic_result_in_error_throws_failed_exception_with_the_error()
    {
        _mediator.Response = Result<string>.Error("business rule violated");
        var dispatcher = CreateDispatcher();

        var ex = await Assert.ThrowsAsync<QueueHandlerFailedException>(
            () => dispatcher.Dispatch(Envelope<SingleResultCommand>(), CancellationToken.None));

        await Assert.That(ex!.Error).IsEqualTo("business rule violated");
    }

    [Test]
    public async Task Dispatch_single_generic_result_in_ok_state_completes()
    {
        _mediator.Response = Result<string>.Ok();

        await CreateDispatcher().Dispatch(Envelope<SingleResultCommand>(), CancellationToken.None);

        await Assert.That(_mediator.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Dispatch_two_generic_result_in_error_throws_failed_exception_with_the_error()
    {
        _mediator.Response = Result<int, string>.Error("nope");
        var dispatcher = CreateDispatcher();

        var ex = await Assert.ThrowsAsync<QueueHandlerFailedException>(
            () => dispatcher.Dispatch(Envelope<OkErrorResultCommand>(), CancellationToken.None));

        await Assert.That(ex!.Error).IsEqualTo("nope");
    }

    [Test]
    public async Task Dispatch_two_generic_result_in_ok_state_completes()
    {
        _mediator.Response = Result<int, string>.Ok(42);

        await CreateDispatcher().Dispatch(Envelope<OkErrorResultCommand>(), CancellationToken.None);

        await Assert.That(_mediator.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Dispatch_nested_result_with_inner_error_surfaces_the_inner_error()
    {
        _mediator.Response = Result<Result<string>, string>.Ok(Result<string>.Error("inner failure"));
        var dispatcher = CreateDispatcher();

        var ex = await Assert.ThrowsAsync<QueueHandlerFailedException>(
            () => dispatcher.Dispatch(Envelope<NestedResultCommand>(), CancellationToken.None));

        await Assert.That(ex!.Error).IsEqualTo("inner failure");
    }

    [Test]
    public async Task Dispatch_nested_result_with_outer_error_surfaces_the_outer_error()
    {
        _mediator.Response = Result<Result<string>, string>.Error("outer failure");
        var dispatcher = CreateDispatcher();

        var ex = await Assert.ThrowsAsync<QueueHandlerFailedException>(
            () => dispatcher.Dispatch(Envelope<NestedResultCommand>(), CancellationToken.None));

        await Assert.That(ex!.Error).IsEqualTo("outer failure");
    }

    [Test]
    public async Task Dispatch_nested_result_all_ok_completes()
    {
        _mediator.Response = Result<Result<string>, string>.Ok(Result<string>.Ok());

        await CreateDispatcher().Dispatch(Envelope<NestedResultCommand>(), CancellationToken.None);

        await Assert.That(_mediator.Calls).IsEqualTo(1);
    }

    // -----------------------------------------------------------------------------------------
    // Exception surface
    // -----------------------------------------------------------------------------------------

    [Test]
    public async Task QueueHandlerRetryAfterException_carries_delay_and_default_message()
    {
        var ex = new QueueHandlerRetryAfterException(TimeSpan.FromSeconds(90));

        await Assert.That(ex.RetryAfter).IsEqualTo(TimeSpan.FromSeconds(90));
        await Assert.That(ex.Message).Contains("90");
    }

    [Test]
    public async Task QueueHandlerFailedException_carries_the_boxed_error()
    {
        var error = new { Code = "X" };

        var ex = new QueueHandlerFailedException(error);

        await Assert.That(ex.Error).IsEqualTo((object)error);
        await Assert.That(ex.Message).Contains("error result");
    }
}
