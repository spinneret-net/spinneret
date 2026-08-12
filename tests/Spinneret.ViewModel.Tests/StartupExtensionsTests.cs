using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Spinneret.Functional;
using Spinneret.Parsing;

namespace Spinneret.ViewModel.Tests;

public class StartupExtensionsTests
{
    [Test]
    public async Task AddViewModelParser_registers_a_resolvable_parser()
    {
        var provider = BuildProvider();

        var parser = provider.GetService<IViewModelParser<TestError>>();

        await Assert.That(parser).IsNotNull();
    }

    [Test]
    public async Task AddViewModelParser_registers_the_parser_as_a_singleton()
    {
        var provider = BuildProvider();

        var first = provider.GetRequiredService<IViewModelParser<TestError>>();
        var second = provider.GetRequiredService<IViewModelParser<TestError>>();

        await Assert.That(ReferenceEquals(first, second)).IsTrue();
    }

    [Test]
    public async Task AddViewModelParser_uses_the_localizer_from_the_given_factory_function()
    {
        var provider = BuildProvider();
        var parser = provider.GetRequiredService<IViewModelParser<TestError>>();
        var viewModel = new FormViewModel();

        parser.Parse(
            viewModel,
            null,
            p => p.Parse(
                x => x.Name,
                _ => Result<string, TestError>.Error(new TestError("bad-name"))),
            out var isValid);

        await Assert.That(isValid).IsFalse();
        await Assert.That(viewModel.ValidationState.GetError("Name")).IsEqualTo("loc:bad-name");
    }

    [Test]
    public async Task AddViewModelParser_uses_the_given_missing_property_error()
    {
        var provider = BuildProvider();
        var parser = provider.GetRequiredService<IViewModelParser<TestError>>();
        var viewModel = new FormViewModel { Name = null };

        parser.Parse(viewModel, null, p => p.Require(x => x.Name), out var isValid);

        await Assert.That(isValid).IsFalse();
        await Assert.That(viewModel.ValidationState.GetError("Name")).IsEqualTo("loc:missing");
    }

    [Test]
    public async Task AddViewModelParser_resolving_without_a_localizer_factory_throws()
    {
        var provider = new ServiceCollection()
            .AddViewModelParser(new TestError("missing"), factory => factory.Create("res", "loc"))
            .BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IViewModelParser<TestError>>());

        await Assert.That(exception.Message).Contains("IStringLocalizerFactory");
    }

    private static ServiceProvider BuildProvider()
    {
        return new ServiceCollection()
            .AddSingleton<IStringLocalizerFactory, FakeLocalizerFactory>()
            .AddViewModelParser(new TestError("missing"), factory => factory.Create("resource", "location"))
            .BuildServiceProvider();
    }

    private sealed record TestError(string Key) : ILocalizable
    {
        public string Localize(IStringLocalizer localizer) => localizer[Key].Value;
    }

    private sealed class FakeLocalizerFactory : IStringLocalizerFactory
    {
        public IStringLocalizer Create(Type resourceSource) => new FakeLocalizer();
        public IStringLocalizer Create(string baseName, string location) => new FakeLocalizer();
    }

    private sealed class FakeLocalizer : IStringLocalizer
    {
        public LocalizedString this[string name] => new(name, $"loc:{name}");
        public LocalizedString this[string name, params object[] arguments] => new(name, $"loc:{name}");
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }

    private sealed class FormViewModel : IValidationStateProvider
    {
        public IValidationState ValidationState { get; } = new ValidationState();
        public string? Name { get; set; }
    }
}
