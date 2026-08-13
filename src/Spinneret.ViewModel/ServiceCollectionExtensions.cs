using Microsoft.Extensions.Localization;
using Spinneret.Parsing;
using Spinneret.ViewModel;

// ReSharper disable once CheckNamespace — deliberate: registration extensions live in the
// DI namespace so every Add* call is discoverable without a using directive.
namespace Microsoft.Extensions.DependencyInjection;

public static class ViewModelServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IViewModelParser{TParseError}"/>, which validates view models with
    /// the same parse functions used at the HTTP boundary and binds each error to the field
    /// that caused it.
    /// </summary>
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
