using Spinneret.Functional;

namespace Spinneret.ViewModel;

/// <summary>Two-way binding conversion: <see cref="ConvertTo"/> parses (failing with a display message), <see cref="ConvertFrom"/> formats.</summary>
public interface IConverter<T1, T2>
{
    Result<T2, string> ConvertTo(T1 value);
    T1 ConvertFrom(T2 value);
}
