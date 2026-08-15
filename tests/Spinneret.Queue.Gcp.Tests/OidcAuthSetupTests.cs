using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Spinneret.Queue.Gcp.Tests;

public sealed class OidcAuthSetupTests
{
    private static JwtBearerOptions ResolveJwtOptions(IServiceProvider provider)
        => provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(OidcAuthSetup.SchemeName);

    [Test]
    public async Task Scheme_and_policy_names_are_queue_oidc()
    {
        await Assert.That(OidcAuthSetup.SchemeName).IsEqualTo("QueueOIDC");
        await Assert.That(OidcAuthSetup.PolicyName).IsEqualTo("QueueOIDC");
    }

    [Test]
    public async Task AddGcpQueue_configures_jwt_bearer_with_google_issuer_by_default()
    {
        var provider = TestSetup.BuildProvider();

        var jwt = ResolveJwtOptions(provider);

        await Assert.That(jwt.Authority).IsEqualTo("https://accounts.google.com");
        await Assert.That(jwt.RequireHttpsMetadata).IsTrue();
        await Assert.That(jwt.TokenValidationParameters.ValidateIssuer).IsTrue();
        await Assert.That(jwt.TokenValidationParameters.ValidIssuer).IsEqualTo("https://accounts.google.com");
        await Assert.That(jwt.TokenValidationParameters.ValidateLifetime).IsTrue();
        await Assert.That(jwt.TokenValidationParameters.ClockSkew).IsEqualTo(TimeSpan.FromMinutes(1));
    }

    [Test]
    public async Task AddGcpQueue_defaults_valid_audience_to_dispatcher_url()
    {
        var provider = TestSetup.BuildProvider();

        var jwt = ResolveJwtOptions(provider);

        await Assert.That(jwt.TokenValidationParameters.ValidateAudience).IsTrue();
        await Assert.That(jwt.TokenValidationParameters.ValidAudience).IsEqualTo(TestSetup.DispatcherUrl);
    }

    [Test]
    public async Task AddGcpQueue_uses_configured_issuer_and_audience()
    {
        var config = TestSetup.Config(values =>
        {
            values["Queue:Gcp:OidcIssuer"] = "https://issuer.example.com";
            values["Queue:Gcp:OidcAudience"] = "custom-audience";
        });
        var provider = TestSetup.BuildProvider(config);

        var jwt = ResolveJwtOptions(provider);

        await Assert.That(jwt.Authority).IsEqualTo("https://issuer.example.com");
        await Assert.That(jwt.TokenValidationParameters.ValidIssuer).IsEqualTo("https://issuer.example.com");
        await Assert.That(jwt.TokenValidationParameters.ValidAudience).IsEqualTo("custom-audience");
    }

    [Test]
    public async Task AddGcpQueue_with_emulator_allows_http_metadata()
    {
        var provider = TestSetup.BuildProvider(TestSetup.EmulatorConfig());

        var jwt = ResolveJwtOptions(provider);

        await Assert.That(jwt.RequireHttpsMetadata).IsFalse();
    }

    [Test]
    public async Task AddGcpQueue_registers_jwt_bearer_scheme()
    {
        var provider = TestSetup.BuildProvider();

        var scheme = await provider.GetRequiredService<IAuthenticationSchemeProvider>()
            .GetSchemeAsync(OidcAuthSetup.SchemeName);

        await Assert.That(scheme).IsNotNull();
        await Assert.That(scheme!.HandlerType).IsEqualTo(typeof(JwtBearerHandler));
    }

    [Test]
    public async Task AddGcpQueue_policy_requires_authenticated_user_via_queue_scheme()
    {
        var provider = TestSetup.BuildProvider();

        var policy = provider.GetRequiredService<IOptions<AuthorizationOptions>>()
            .Value.GetPolicy(OidcAuthSetup.PolicyName);

        await Assert.That(policy).IsNotNull();
        await Assert.That(policy!.AuthenticationSchemes).Contains(OidcAuthSetup.SchemeName);
        await Assert.That(policy.Requirements.OfType<DenyAnonymousAuthorizationRequirement>().Any()).IsTrue();
    }
}
