using Microsoft.Extensions.Localization;
using Spinneret.Parsing;
using Spinneret.ViewModel;

// ReSharper disable once CheckNamespace — DI namespace for discoverability
namespace Microsoft.Extensions.DependencyInjection;

public static class ViewModelServiceCollectionExtensions
{
    /// <summary>Registers <see cref="IViewModelParser{TParseError}"/>.</summary>
    public static IServiceCollection AddViewModelParser<TParseError>(
        this IServiceCollection services,
        TParseError missingPropertyError,
        Func<IStringLocalizerFactory, IStringLocalizer> localizerFactoryFn)
        where TParseError : ILocalizable
    {
        return services.AddSingleton<IViewModelParser<TParseError>>(s =>
        {
            var localizerFactory = s.GetRequiredService<IStringLocalizerFactory>();
            var localizer = localizerFactoryFn(localizerFactory);
            return new ViewModelParser<TParseError>(localizer, missingPropertyError);
        });
    }
}
