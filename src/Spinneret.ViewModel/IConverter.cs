using Spinneret.Functional;

namespace Spinneret.ViewModel;

public interface IConverter<T1, T2>
{
    Result<T2, string> ConvertTo(T1 value);
    T1 ConvertFrom(T2 value);
}