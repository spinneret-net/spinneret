using Microsoft.Extensions.DependencyInjection;
using Spinneret.ViewModel;

namespace Spinneret.View.Tests;

public class StartupExtensionsTests
{
    private static readonly System.Reflection.Assembly TestAssembly = typeof(StartupExtensionsTests).Assembly;

    [Test]
    public async Task AddMvvm_returns_the_same_service_collection_for_chaining()
    {
        var services = new ServiceCollection();

        var result = services.AddMvvm<ClientRenderContext>(autoRegisterViewModels: false, TestAssembly);

        await Assert.That(ReferenceEquals(result, services)).IsTrue();
    }

    [Test]
    public async Task AddMvvm_registers_view_model_factory_as_scoped()
    {
        var services = new ServiceCollection();

        services.AddMvvm<ClientRenderContext>(autoRegisterViewModels: false, TestAssembly);

        var descriptor = services.Single(d => d.ServiceType == typeof(IViewModelFactory));
        await Assert.That(descriptor.Lifetime).IsEqualTo(ServiceLifetime.Scoped);
        await Assert.That(descriptor.ImplementationType).IsEqualTo(typeof(ViewModelFactory));
    }

    [Test]
    public async Task AddMvvm_registers_refresh_coordinator_as_scoped()
    {
        var services = new ServiceCollection();

        services.AddMvvm<ClientRenderContext>(autoRegisterViewModels: false, TestAssembly);

        var descriptor = services.Single(d => d.ServiceType == typeof(IViewRefreshCoordinator));
        await Assert.That(descriptor.Lifetime).IsEqualTo(ServiceLifetime.Scoped);
    }

    [Test]
    public async Task AddMvvm_registers_view_resolver_as_singleton()
    {
        var services = new ServiceCollection();

        services.AddMvvm<ClientRenderContext>(autoRegisterViewModels: false, TestAssembly);

        var descriptor = services.Single(d => d.ServiceType == typeof(IViewResolver));
        await Assert.That(descriptor.Lifetime).IsEqualTo(ServiceLifetime.Singleton);
    }

    [Test]
    public async Task AddMvvm_registers_render_context_as_singleton_of_the_specified_type()
    {
        var provider = new ServiceCollection()
            .AddMvvm<ClientRenderContext>(autoRegisterViewModels: false, TestAssembly)
            .BuildServiceProvider();

        var first = provider.GetRequiredService<IRenderContext>();
        var second = provider.GetRequiredService<IRenderContext>();

        await Assert.That(first).IsTypeOf<ClientRenderContext>();
        await Assert.That(ReferenceEquals(first, second)).IsTrue();
    }

    [Test]
    public async Task AddMvvm_refresh_coordinator_is_shared_within_a_scope_but_not_across_scopes()
    {
        var provider = new ServiceCollection()
            .AddMvvm<ClientRenderContext>(autoRegisterViewModels: false, TestAssembly)
            .BuildServiceProvider();

        using var scopeA = provider.CreateScope();
        using var scopeB = provider.CreateScope();
        var coordinatorA1 = scopeA.ServiceProvider.GetRequiredService<IViewRefreshCoordinator>();
        var coordinatorA2 = scopeA.ServiceProvider.GetRequiredService<IViewRefreshCoordinator>();
        var coordinatorB = scopeB.ServiceProvider.GetRequiredService<IViewRefreshCoordinator>();

        await Assert.That(ReferenceEquals(coordinatorA1, coordinatorA2)).IsTrue();
        await Assert.That(ReferenceEquals(coordinatorA1, coordinatorB)).IsFalse();
    }

    [Test]
    public async Task AddMvvm_view_resolver_maps_views_discovered_in_the_scanned_assembly()
    {
        var provider = new ServiceCollection()
            .AddMvvm<ClientRenderContext>(autoRegisterViewModels: false, TestAssembly)
            .BuildServiceProvider();

        var resolver = provider.GetRequiredService<IViewResolver>();

        await Assert.That(resolver.ResolveViewType(typeof(SingleViewModel))).IsEqualTo(typeof(SingleView));
    }

    [Test]
    public async Task AddMvvm_auto_register_true_registers_view_models_as_transient()
    {
        var provider = new ServiceCollection()
            .AddMvvm<ClientRenderContext>(autoRegisterViewModels: true, TestAssembly)
            .BuildServiceProvider();

        var first = provider.GetService<SingleViewModel>();
        var second = provider.GetService<SingleViewModel>();

        await Assert.That(first).IsNotNull();
        await Assert.That(second).IsNotNull();
        await Assert.That(ReferenceEquals(first, second)).IsFalse();
    }

    [Test]
    public async Task AddMvvm_auto_register_true_registers_view_models_without_views_too()
    {
        var provider = new ServiceCollection()
            .AddMvvm<ClientRenderContext>(autoRegisterViewModels: true, TestAssembly)
            .BuildServiceProvider();

        await Assert.That(provider.GetService<UnmappedViewModel>()).IsNotNull();
    }

    [Test]
    public async Task AddMvvm_auto_register_false_does_not_register_view_models()
    {
        var provider = new ServiceCollection()
            .AddMvvm<ClientRenderContext>(autoRegisterViewModels: false, TestAssembly)
            .BuildServiceProvider();

        await Assert.That(provider.GetService<SingleViewModel>()).IsNull();
    }
}
