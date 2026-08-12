using Microsoft.Extensions.Localization;
using Spinneret.Functional;
using Spinneret.Parsing;

namespace Spinneret.ViewModel.Tests;

public class ViewModelParserTests
{
    [Test]
    public async Task Parse_valid_view_model_returns_the_parsed_value_and_is_valid()
    {
        var viewModel = new FormViewModel { Name = "Ada", Age = "36" };
        var sut = CreateParser();

        var parsed = sut.Parse(viewModel, null, ParseForm, out var isValid);

        await Assert.That(isValid).IsTrue();
        await Assert.That(parsed).IsEqualTo(new ParsedForm("Ada", 36));
        await Assert.That(viewModel.ValidationState.HasErrors).IsFalse();
    }

    [Test]
    public async Task Parse_valid_view_model_removes_stale_errors_on_parsed_properties()
    {
        var viewModel = new FormViewModel { Name = "Ada", Age = "36" };
        viewModel.ValidationState.AddError("Age", "stale");
        var sut = CreateParser();

        sut.Parse(viewModel, null, ParseForm, out _);

        await Assert.That(viewModel.ValidationState.GetError("Age")).IsNull();
    }

    [Test]
    public async Task Parse_invalid_property_adds_the_localized_error_and_returns_default()
    {
        var viewModel = new FormViewModel { Name = "Ada", Age = "not-a-number" };
        var sut = CreateParser();

        var parsed = sut.Parse(viewModel, null, ParseForm, out var isValid);

        await Assert.That(isValid).IsFalse();
        await Assert.That(parsed).IsNull();
        await Assert.That(viewModel.ValidationState.GetError("Age")).IsEqualTo("loc:bad-age");
    }

    [Test]
    public async Task Parse_missing_required_property_reports_the_missing_property_error()
    {
        var viewModel = new FormViewModel { Name = "  ", Age = "36" };
        var sut = CreateParser();

        var parsed = sut.Parse(viewModel, null, ParseForm, out var isValid);

        await Assert.That(isValid).IsFalse();
        await Assert.That(parsed).IsNull();
        await Assert.That(viewModel.ValidationState.GetError("Name")).IsEqualTo("loc:missing");
    }

    [Test]
    public async Task Parse_error_on_a_property_outside_the_changed_set_is_not_surfaced()
    {
        var viewModel = new FormViewModel { Name = "Ada", Age = "not-a-number" };
        var sut = CreateParser();

        var parsed = sut.Parse(viewModel, ["Name"], ParseForm, out var isValid);

        await Assert.That(isValid).IsFalse();
        await Assert.That(parsed).IsNull();
        await Assert.That(viewModel.ValidationState.GetError("Age")).IsNull();
    }

    [Test]
    public async Task Parse_with_changed_set_removes_stale_errors_on_valid_changed_properties()
    {
        var viewModel = new FormViewModel { Name = "Ada", Age = "not-a-number" };
        viewModel.ValidationState.AddError("Name", "stale");
        var sut = CreateParser();

        sut.Parse(viewModel, ["Name"], ParseForm, out _);

        await Assert.That(viewModel.ValidationState.GetError("Name")).IsNull();
    }

    [Test]
    public async Task Parse_changed_descendant_revalidates_the_parsed_ancestor()
    {
        var viewModel = new FormViewModel { Name = "Ada", Age = "36", Text = { ValueEn = null } };
        var sut = CreateParser();

        sut.Parse(
            viewModel,
            ["Text.ValueEn"],
            p => p.Parse(
                x => x.Text,
                text => text.ValueEn == null
                    ? Result<string, TestError>.Error(new TestError("no-text"))
                    : Result<string, TestError>.Ok(text.ValueEn)),
            out var isValid);

        await Assert.That(isValid).IsFalse();
        await Assert.That(viewModel.ValidationState.GetError("Text")).IsEqualTo("loc:no-text");
    }

    [Test]
    public async Task Parse_unrelated_changed_property_does_not_surface_the_ancestors_error()
    {
        var viewModel = new FormViewModel { Name = "Ada", Age = "36", Text = { ValueEn = null } };
        var sut = CreateParser();

        sut.Parse(
            viewModel,
            ["Name"],
            p => p.Parse(
                x => x.Text,
                text => text.ValueEn == null
                    ? Result<string, TestError>.Error(new TestError("no-text"))
                    : Result<string, TestError>.Ok(text.ValueEn)),
            out _);

        await Assert.That(viewModel.ValidationState.GetError("Text")).IsNull();
    }

    [Test]
    public async Task Parse_default_interface_overload_returns_the_parsed_value()
    {
        var viewModel = new FormViewModel { Name = "Ada", Age = "36" };
        IViewModelParser<TestError> sut = CreateParser();

        var parsed = sut.Parse(viewModel, null, ParseForm);

        await Assert.That(parsed).IsEqualTo(new ParsedForm("Ada", 36));
    }

    private static ViewModelParser<TestError> CreateParser() =>
        new(new FakeLocalizer(), new TestError("missing"));

    private static ParsedForm ParseForm(PropertyParser<FormViewModel, TestError> parser)
    {
        var name = parser.Require(x => x.Name);
        var age = parser.Require(
            x => x.Age,
            text => int.TryParse(text, out var value)
                ? Result<int, TestError>.Ok(value)
                : Result<int, TestError>.Error(new TestError("bad-age")));

        return new ParsedForm(name, age);
    }

    private sealed record ParsedForm(string Name, int Age);

    private sealed record TestError(string Key) : ILocalizable
    {
        public string Localize(IStringLocalizer localizer) => localizer[Key].Value;
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
        public string? Age { get; set; }
        public TextModel Text { get; set; } = new();
    }

    private sealed class TextModel
    {
        public string? ValueEn { get; set; }
    }
}
