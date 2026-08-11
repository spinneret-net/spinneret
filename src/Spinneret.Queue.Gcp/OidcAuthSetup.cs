using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Spinneret.Queue.Gcp;

public static class OidcAuthSetup
{
    public const string SchemeName = "QueueOIDC";

    /// <summary>
    /// Authorization policy guarding endpoints that accept Google-minted OIDC tokens (Cloud Tasks
    /// dispatch and Cloud Scheduler ticks). Exposed so the host can apply it to its own internal
    /// endpoints with <c>RequireAuthorization(OidcAuthSetup.PolicyName)</c>.
    /// </summary>
    public const string PolicyName = "QueueOIDC";

    internal static IServiceCollection AddQueueOidcAuth(this IServiceCollection services, GcpQueueOptions options)
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
