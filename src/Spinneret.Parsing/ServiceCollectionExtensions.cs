using Spinneret.Parsing;

// ReSharper disable once CheckNamespace — deliberate: registration extensions live in the
// DI namespace so every Add* call is discoverable without a using directive.
namespace Microsoft.Extensions.DependencyInjection;

public static class ParsingServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IModelParser{T}"/> using <paramref name="missingPropertyError"/>
    /// as the error recorded for absent required properties.
    /// </summary>
    public static IServiceCollection AddModelParser<T>(this IServiceCollection services, T missingPropertyError)
    {
        return services.AddSingleton<IModelParser<T>>(new ModelParser<T>(missingPropertyError));
    }
}
