using Microsoft.Extensions.DependencyInjection;
using Spinneret.Functional;

namespace Spinneret.Mediator.Tests;

public class SpinneretMediatorTests
{
    [Test]
    [Arguments(0)]
    [Arguments(7)]
    [Arguments(-13)]
    public async Task Send_request_with_registered_handler_returns_handler_response(int value)
    {
        var mediator = TestServices.BuildMediator();

        var result = await mediator.Send(new EchoQuery(value));

        await Assert.That(result).IsEqualTo(value);
    }

    [Test]
    public async Task Send_dispatches_each_request_type_to_its_own_handler()
    {
        var mediator = TestServices.BuildMediator();

        var echoed = await mediator.Send(new EchoQuery(5));
        var uppercased = await mediator.Send(new UppercaseQuery("abc"));

        await Assert.That(echoed).IsEqualTo(5);
        await Assert.That(uppercased).IsEqualTo("ABC");
    }

    [Test]
    public async Task Send_unit_request_invokes_registered_handler()
    {
        var handler = new VoidCommandHandler();
        var mediator = TestServices.BuildMediator(services =>
            services.AddSingleton<IRequestHandler<VoidCommand, Unit>>(handler));

        await mediator.Send(new VoidCommand());

        await Assert.That(handler.CallCount).IsEqualTo(1);
    }

    [Test]
    public async Task Send_uncached_request_invokes_handler_on_every_call()
    {
        var handler = new VoidCommandHandler();
        var mediator = TestServices.BuildMediator(services =>
            services.AddSingleton<IRequestHandler<VoidCommand, Unit>>(handler));

        await mediator.Send(new VoidCommand());
        await mediator.Send(new VoidCommand());
        await mediator.Send(new VoidCommand());

        await Assert.That(handler.CallCount).IsEqualTo(3);
    }

    [Test]
    public async Task Send_request_without_registered_handler_throws_InvalidOperationException()
    {
        var mediator = TestServices.BuildMediator();

        await Assert.That(async () => await mediator.Send(new NoHandlerQuery()))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Send_handler_exception_propagates_to_caller()
    {
        var mediator = TestServices.BuildMediator();

        await Assert.That(async () => await mediator.Send(new ThrowingQuery()))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Send_synchronously_throwing_handler_propagates_original_exception()
    {
        var mediator = TestServices.BuildMediator();

        var exception = await Assert.That(async () => await mediator.Send(new SyncThrowingQuery()))
            .Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).IsEqualTo("sync handler failure");
    }

    [Test]
    public async Task Send_forwards_cancellation_token_to_handler_for_uncached_request()
    {
        var mediator = TestServices.BuildMediator();
        using var cts = new CancellationTokenSource();

        var receivedToken = await mediator.Send(new TokenEchoQuery(), cts.Token);

        await Assert.That(receivedToken).IsEqualTo(cts.Token);
    }

    [Test]
    public async Task Unit_Value_equals_default_instance()
    {
        await Assert.That(Unit.Value).IsEqualTo(default(Unit));
    }
}
