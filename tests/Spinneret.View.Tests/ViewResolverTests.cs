using Microsoft.Extensions.DependencyInjection;

namespace Spinneret.View.Tests;

public class ViewResolverTests
{
    // ViewResolver is internal; the public way to obtain a configured instance is through
    // AddMvvm, which scans the given assemblies for ViewBase<T> subclasses.
    private static IViewResolver CreateResolver() =>
        new ServiceCollection()
            .AddMvvm<ClientRenderContext>(o => { o.AutoRegisterViewModels = false; o.Assemblies.Add(typeof(ViewResolverTests).Assembly); })
            .BuildServiceProvider()
            .GetRequiredService<IViewResolver>();

    [Test]
    public async Task ResolveViewType_view_model_with_single_mapped_view_returns_that_view()
    {
        var resolver = CreateResolver();

        var viewType = resolver.ResolveViewType(typeof(SingleViewModel));

        await Assert.That(viewType).IsEqualTo(typeof(SingleView));
    }

    [Test]
    public async Task ResolveViewType_multiple_views_prefers_view_named_after_view_model_without_suffix()
    {
        var resolver = CreateResolver();

        // DuoViewModel maps to both Duo and DuoAlternate; "Duo" matches the naming convention.
        var viewType = resolver.ResolveViewType(typeof(DuoViewModel));

        await Assert.That(viewType).IsEqualTo(typeof(Duo));
    }

    [Test]
    public async Task ResolveViewType_multiple_views_without_name_match_returns_one_of_the_mapped_views()
    {
        var resolver = CreateResolver();

        // TrioViewModel maps to TrioFirstView and TrioSecondView; neither matches "Trio",
        // so the resolver falls back to the first mapped view (scan order is unspecified).
        var viewType = resolver.ResolveViewType(typeof(TrioViewModel));

        var isMappedView = viewType == typeof(TrioFirstView) || viewType == typeof(TrioSecondView);
        await Assert.That(isMappedView).IsTrue();
    }

    [Test]
    public async Task ResolveViewType_unmapped_view_model_throws_InvalidOperationException()
    {
        var resolver = CreateResolver();

        await Assert.That(() => resolver.ResolveViewType(typeof(UnmappedViewModel)))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ResolveViewType_resolution_is_stable_across_calls()
    {
        var resolver = CreateResolver();

        var first = resolver.ResolveViewType(typeof(DuoViewModel));
        var second = resolver.ResolveViewType(typeof(DuoViewModel));

        await Assert.That(first).IsEqualTo(second);
    }
}
