using Microsoft.Extensions.Localization;

namespace Spinneret.Parsing;

public interface ILocalizable
{
    string Localize(IStringLocalizer localizer);
}