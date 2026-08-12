using Microsoft.Extensions.DependencyInjection;

namespace Spinneret.Mediator.Tests;

public class StartupExtensionsTests
{
    [Test]
    public async Task AddMediator_with_assembly_registers_handlers_from_that_assembly()
    {
        var services = new ServiceCollection();
        services.AddMediator(typeof(StartupExtensionsTests).Assembly);
        await using var provider = services.BuildServiceProvider();

        var handler = provider.GetService<IRequestHandler<EchoQuery, int>>();

        await Assert.That(handler).IsNotNull();
        await Assert.That(handler).IsTypeOf<EchoHandler>();
    }

    [Test]
    public async Task AddMediator_registers_mediator_and_resolves_it()
    {
        var services = new ServiceCollection();
        services.AddMediator(typeof(StartupExtensionsTests).Assembly);
        await using var provider = services.BuildServiceProvider();

        var mediator = provider.GetService<ISpinneretMediator>();

        await Assert.That(mediator).IsNotNull();
    }

    [Test]
    public async Task AddMediator_does_not_register_abstract_handler_types()
    {
        var services = new ServiceCollection();
        services.AddMediator(typeof(StartupExtensionsTests).Assembly);
        await using var provider = services.BuildServiceProvider();

        var handlers = provider.GetServices<IRequestHandler<EchoQuery, int>>().ToList();

        await Assert.That(handlers.Count).IsEqualTo(1);
        await Assert.That(handlers[0]).IsTypeOf<EchoHandler>();
    }

    [Test]
    public async Task AddMediator_does_not_register_open_generic_handler_types()
    {
        // OpenGenericEchoHandler<T> lives in this assembly; scanning must skip it,
        // both to avoid a registration error and to keep it out of the container.
        var services = new ServiceCollection();
        services.AddMediator(typeof(StartupExtensionsTests).Assembly);
        await using var provider = services.BuildServiceProvider();

        var handlers = provider.GetServices<IRequestHandler<EchoQuery, int>>().ToList();

        await Assert.That(handlers.Count).IsEqualTo(1);
        await Assert.That(handlers[0]).IsTypeOf<EchoHandler>();
    }

    [Test]
    public async Task AddMediator_registers_every_handler_interface_of_a_multi_interface_handler()
    {
        var services = new ServiceCollection();
        services.AddMediator(typeof(StartupExtensionsTests).Assembly);
        await using var provider = services.BuildServiceProvider();

        var cachedQueryHandler = provider.GetService<IRequestHandler<CachedQuery, int>>();
        var multiTagQueryHandler = provider.GetService<IRequestHandler<MultiTagQuery, int>>();

        await Assert.That(cachedQueryHandler).IsTypeOf<CountingHandler>();
        await Assert.That(multiTagQueryHandler).IsTypeOf<CountingHandler>();
    }

    [Test]
    public async Task AddMediator_registers_handlers_as_transient()
    {
        var services = new ServiceCollection();
        services.AddMediator(typeof(StartupExtensionsTests).Assembly);
        await using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IRequestHandler<EchoQuery, int>>();
        var second = provider.GetRequiredService<IRequestHandler<EchoQuery, int>>();

        await Assert.That(ReferenceEquals(first, second)).IsFalse();
    }

    [Test]
    public async Task AddMediator_registers_mediator_as_scoped()
    {
        var services = new ServiceCollection();
        services.AddMediator(typeof(StartupExtensionsTests).Assembly);
        await using var provider = services.BuildServiceProvider();

        using var scope1 = provider.CreateScope();
        using var scope2 = provider.CreateScope();

        var a = scope1.ServiceProvider.GetRequiredService<ISpinneretMediator>();
        var b = scope1.ServiceProvider.GetRequiredService<ISpinneretMediator>();
        var c = scope2.ServiceProvider.GetRequiredService<ISpinneretMediator>();

        await Assert.That(ReferenceEquals(a, b)).IsTrue();
        await Assert.That(ReferenceEquals(a, c)).IsFalse();
    }

    [Test]
    public async Task AddMediator_cache_is_shared_across_scopes()
    {
        var handler = new CountingHandler();
        var services = new ServiceCollection();
        services.AddMediator(typeof(StartupExtensionsTests).Assembly);
        services.AddSingleton<IRequestHandler<CachedQuery, int>>(handler);
        await using var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var mediator = scope.ServiceProvider.GetRequiredService<ISpinneretMediator>();
            await mediator.Send(new CachedQuery(11));
        }

        using (var scope = provider.CreateScope())
        {
            var mediator = scope.ServiceProvider.GetRequiredService<ISpinneretMediator>();
            var result = await mediator.Send(new CachedQuery(11));
            await Assert.That(result).IsEqualTo(11);
        }

        await Assert.That(handler.CallCount).IsEqualTo(1);
    }

    [Test]
    public async Task AddMediator_returns_the_same_service_collection_instance()
    {
        var services = new ServiceCollection();

        var returned = services.AddMediator(typeof(StartupExtensionsTests).Assembly);

        await Assert.That(ReferenceEquals(services, returned)).IsTrue();
    }

    [Test]
    public async Task AddMediator_without_arguments_scans_the_entry_assembly()
    {
        // In this test host the entry assembly is the test assembly itself,
        // so the parameterless overload should find the handlers defined here.
        var services = new ServiceCollection();
        services.AddMediator();
        await using var provider = services.BuildServiceProvider();

        var handler = provider.GetService<IRequestHandler<EchoQuery, int>>();

        await Assert.That(handler).IsNotNull();
    }
}
