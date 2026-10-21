using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Spinneret.Queue.CloudTasks;

public static class CloudTasksAuth
{
    public const string SchemeName = "QueueOIDC";

    /// <summary>Authorization policy accepting OIDC tokens minted for the configured service account, for use on the host's own internal endpoints.</summary>
    public const string PolicyName = "QueueOIDC";

    internal static IServiceCollection AddQueueOidcAuth(this IServiceCollection services, CloudTasksOptions options)
    {
        services
            .AddAuthentication()
            .AddJwtBearer(SchemeName, jwt =>
            {
                jwt.Authority = options.ResolvedOidcIssuer;
                jwt.RequireHttpsMetadata = !options.UsesEmulator;
                // Keep claim names as minted, so the caller check reads "email" rather than a mapped claim type.
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = options.ResolvedOidcIssuer,
                    ValidateAudience = true,
                    ValidAudience = options.ResolvedOidcAudience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(1),
                };
                // Google mints a token with any audience for any service account in any project, so issuer and
                // audience alone admit anyone; only the caller's identity ties the token to this deployment.
                jwt.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        if (!IsConfiguredCaller(context.Principal, options.ServiceAccountEmail))
                            context.Fail("The OIDC token was not minted for the configured service account.");
                        return Task.CompletedTask;
                    }
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(PolicyName, policy => policy
                .AddAuthenticationSchemes(SchemeName)
                .RequireAuthenticatedUser());

        return services;
    }

    private static bool IsConfiguredCaller(ClaimsPrincipal? principal, string serviceAccountEmail) =>
        principal is not null
        && string.Equals(principal.FindFirst("email")?.Value, serviceAccountEmail, StringComparison.OrdinalIgnoreCase)
        && string.Equals(principal.FindFirst("email_verified")?.Value, "true", StringComparison.OrdinalIgnoreCase);
}
