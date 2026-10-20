using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Spinneret.Queue.CloudTasks;

public static class CloudTasksAuth
{
    public const string SchemeName = "QueueOIDC";

    /// <summary>Authorization policy accepting Google-minted OIDC tokens, for use on the host's own internal endpoints.</summary>
    public const string PolicyName = "QueueOIDC";

    internal static IServiceCollection AddQueueOidcAuth(this IServiceCollection services, CloudTasksOptions options)
    {
        services
            .AddAuthentication()
            .AddJwtBearer(SchemeName, jwt =>
            {
                jwt.Authority = options.ResolvedOidcIssuer;
                jwt.RequireHttpsMetadata = !options.UsesEmulator;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = options.ResolvedOidcIssuer,
                    ValidateAudience = true,
                    ValidAudience = options.ResolvedOidcAudience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(1),
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(PolicyName, policy => policy
                .AddAuthenticationSchemes(SchemeName)
                .RequireAuthenticatedUser());

        return services;
    }
}
