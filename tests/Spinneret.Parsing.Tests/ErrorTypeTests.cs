using Microsoft.Extensions.Localization;

namespace Spinneret.Parsing.Tests;

public class ErrorTypeTests
{
    private sealed record LocalizableError(string Key) : ILocalizable
    {
        public string Localize(IStringLocalizer localizer)
        {
            return localizer[Key].Value;
        }
    }

    private sealed class FakeStringLocalizer(Dictionary<string, string> translations) : IStringLocalizer
    {
        public LocalizedString this[string name] =>
            translations.TryGetValue(name, out var value)
                ? new LocalizedString(name, value)
                : new LocalizedString(name, name, resourceNotFound: true);

        public LocalizedString this[string name, params object[] arguments] =>
            new(name, string.Format(this[name].Value, arguments));

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
            translations.Select(x => new LocalizedString(x.Key, x.Value));
    }

    [Test]
    public async Task InvalidProperty_stores_property_name_and_error()
    {
        var sut = new InvalidProperty<string>
        {
            PropertyName = "Name",
            Error = "some_error"
        };

        await Assert.That(sut.PropertyName).IsEqualTo("Name");
        await Assert.That(sut.Error).IsEqualTo("some_error");
    }

    [Test]
    public async Task Localizable_error_reported_by_parser_can_be_localized()
    {
        var modelParser = new ModelParser<LocalizableError>(new LocalizableError("errors.required"));
        var localizer = new FakeStringLocalizer(new Dictionary<string, string>
        {
            ["errors.required"] = "Fältet är obligatoriskt"
        });
        var sut = new TestObject
        {
            StringProperty = null!
        };

        var parseRes = modelParser.Parse(sut, parser => parser.Require(x => x.StringProperty));

        var error = Expect.SingleError(parseRes);

        await Assert.That(error.PropertyName).IsEqualTo("StringProperty");
        await Assert.That(error.Error.Localize(localizer)).IsEqualTo("Fältet är obligatoriskt");
    }

    [Test]
    public async Task Localizable_error_with_missing_translation_falls_back_to_key()
    {
        var localizer = new FakeStringLocalizer([]);
        var sut = new LocalizableError("errors.unknown");

        var localized = sut.Localize(localizer);

        await Assert.That(localized).IsEqualTo("errors.unknown");
    }
}
