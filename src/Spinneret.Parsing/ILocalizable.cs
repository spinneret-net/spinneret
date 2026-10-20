using Microsoft.Extensions.Localization;

namespace Spinneret.Parsing;

/// <summary>A parse error that can render itself as localized display text.</summary>
public interface ILocalizable
{
    string Localize(IStringLocalizer localizer);
}
