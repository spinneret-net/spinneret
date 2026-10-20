using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Spinneret.Scheduler.AspNetCore.Tests;

// Through a real pipeline: only a request proves the authorization middleware runs.
public class SchedulerSweepHostTests
{
    private const string TestPolicy = "TestSweepPolicy";
    private const string TestScheme = "TestScheme";

    private sealed class FakeSweep : ISchedulerSweep
    {
        public int Calls;
        public Exception? ThrowOnSweep { get; init; }
        public int JobsDispatched { get; init; }

        public Task<SweepResult> SweepAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return ThrowOnSweep is not null
                ? Task.FromException<SweepResult>(ThrowOnSweep)
                : Task.FromResult(SweepResult.Dispatched(JobsDispatched));
        }
    }

    /// <summary>Authenticates when the agreed header is sent, so no token issuer is needed.</summary>
    private sealed class HeaderSchemeHandler(
        Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        internal const string HeaderName = "X-Test-Caller";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey(HeaderName))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "cron")], TestScheme);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }

    private static async Task<IHost> StartHostAsync(ISchedulerSweep sweep, string? pattern = null)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton(sweep);
                    services.AddRouting();
                    services.AddAuthentication(TestScheme)
                        .AddScheme<AuthenticationSchemeOptions, HeaderSchemeHandler>(TestScheme, _ => { });
                    services.AddAuthorizationBuilder()
                        .AddPolicy(TestPolicy, policy => policy
                            .AddAuthenticationSchemes(TestScheme)
                            .RequireAuthenticatedUser());
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapSchedulerSweep(TestPolicy, pattern));
                }))
            .StartAsync();

        return host;
    }

    private static HttpClient Authenticated(IHost host)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(HeaderSchemeHandler.HeaderName, "cron");
        return client;
    }

    [Test]
    public async Task An_authorized_post_runs_one_sweep_and_returns_200()
    {
        var sweep = new FakeSweep { JobsDispatched = 3 };
        using var host = await StartHostAsync(sweep);

        var response = await Authenticated(host).PostAsync("/internal/scheduler/sweep", content: null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(sweep.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task The_response_body_reports_what_the_sweep_dispatched()
    {
        using var host = await StartHostAsync(new FakeSweep { JobsDispatched = 7 });

        var result = await Authenticated(host)
            .PostAsync("/internal/scheduler/sweep", content: null)
            .ContinueWith(t => t.Result.Content.ReadFromJsonAsync<SweepResult>()).Unwrap();

        await Assert.That(result!.JobsDispatched).IsEqualTo(7);
    }

    [Test]
    public async Task An_unauthenticated_post_is_rejected_without_sweeping()
    {
        var sweep = new FakeSweep();
        using var host = await StartHostAsync(sweep);

        var response = await host.GetTestClient().PostAsync("/internal/scheduler/sweep", content: null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(sweep.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task A_failing_sweep_returns_500_so_the_trigger_retries()
    {
        using var host = await StartHostAsync(
            new FakeSweep { ThrowOnSweep = new InvalidOperationException("the store is unreachable") });

        var response = await Authenticated(host).PostAsync("/internal/scheduler/sweep", content: null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
    }

    [Test]
    public async Task A_get_is_not_a_sweep()
    {
        var sweep = new FakeSweep();
        using var host = await StartHostAsync(sweep);

        var response = await Authenticated(host).GetAsync("/internal/scheduler/sweep");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.MethodNotAllowed);
        await Assert.That(sweep.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task A_custom_route_is_the_one_that_answers()
    {
        var sweep = new FakeSweep();
        using var host = await StartHostAsync(sweep, "/ops/tick");

        var moved = await Authenticated(host).PostAsync("/internal/scheduler/sweep", content: null);
        var custom = await Authenticated(host).PostAsync("/ops/tick", content: null);

        await Assert.That(moved.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(custom.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(sweep.Calls).IsEqualTo(1);
    }
}
