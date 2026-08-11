using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Spinneret.Parsing;

namespace Spinneret.ViewModel
{
    public static class StartupExtensions
    {
        public static IServiceCollection AddViewModelParser<TParseError>(
            this IServiceCollection services,
            TParseError missingPropertyError,
            Func<IStringLocalizerFactory, IStringLocalizer> localizerFactoryFn) 
            where TParseError: ILocalizable
        {
            return services.AddSingleton<IViewModelParser<TParseError>>(s =>
            {
                var localizerFactory = s.GetRequiredService<IStringLocalizerFactory>();
                var localizer = localizerFactoryFn(localizerFactory);
                return new ViewModelParser<TParseError>(localizer, missingPropertyError);
            });
        }
    }
}
