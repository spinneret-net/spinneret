using Microsoft.Extensions.DependencyInjection;

namespace Spinneret.Parsing
{
    public static class StartupExtensions
    {
        public static IServiceCollection AddModelParser<T>(this IServiceCollection services, T missingPropertyError)
        {
            return services.AddSingleton<IModelParser<T>>(new ModelParser<T>(missingPropertyError));
        }
    }
}
