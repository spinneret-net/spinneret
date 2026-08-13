using Spinneret.Functional;

namespace Spinneret.ViewModel;

/// <summary>
/// A two-way conversion for bindings: <see cref="ConvertTo"/> parses the input representation
/// (failing with a display message), <see cref="ConvertFrom"/> formats the value back.
/// Implemented by consumers, typically once per value type and reused across bindings.
/// </summary>
public interface IConverter<T1, T2>
{
    Result<T2, string> ConvertTo(T1 value);
    T1 ConvertFrom(T2 value);
}
