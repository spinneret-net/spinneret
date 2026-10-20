using Spinneret.Parsing;

// ReSharper disable once CheckNamespace — discoverable without a using
namespace Microsoft.Extensions.DependencyInjection;

public static class ParsingServiceCollectionExtensions
{
    /// <summary>Registers <see cref="IModelParser{T}"/>; <paramref name="missingPropertyError"/> is recorded for absent required properties.</summary>
    public static IServiceCollection AddModelParser<T>(this IServiceCollection services, T missingPropertyError)
    {
        return services.AddSingleton<IModelParser<T>>(new ModelParser<T>(missingPropertyError));
    }
}
