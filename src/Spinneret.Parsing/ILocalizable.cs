using Microsoft.Extensions.Localization;

namespace Spinneret.Parsing;

/// <summary>
/// A parse error that can render itself as localized display text. Implemented by consumers
/// on their error types so the same error flows from an HTTP boundary to a form field.
/// </summary>
public interface ILocalizable
{
    string Localize(IStringLocalizer localizer);
}
