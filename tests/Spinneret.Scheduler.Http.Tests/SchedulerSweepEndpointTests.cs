using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Spinneret.Scheduler.Http.Tests;

/// <summary>
/// The endpoint is a trigger and nothing else: it resolves whatever <see cref="ISchedulerSweep"/>
/// the host registered, so these tests use a fake and never touch a storage provider.
/// </summary>
public class SchedulerSweepEndpointTests
{
    private const string TestPolicy = "TestSweepPolicy";

    private sealed class FakeSweep : ISchedulerSweep
    {
        public int Calls { get; private set; }
        public Exception? ThrowOnSweep { get; init; }
        public int JobsDispatched { get; init; }

        public Task<SweepResult> SweepAsync(CancellationToken ct)
        {
            Calls++;
            return ThrowOnSweep is not null
                ? Task.FromException<SweepResult>(ThrowOnSweep)
                : Task.FromResult(SweepResult.Dispatched(JobsDispatched));
        }
    }

    private sealed class TestEndpointRouteBuilder(IServiceProvider services) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = services;
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }

    private static TestEndpointRouteBuilder RouteBuilder(ISchedulerSweep? sweep = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRouting();
        if (sweep is not null)
            services.AddSingleton(sweep);
        return new TestEndpointRouteBuilder(services.BuildServiceProvider());
    }

    [Test]
    public async Task Maps_a_post_endpoint_on_the_default_route()
    {
        var builder = RouteBuilder(new FakeSweep());

        builder.MapSchedulerSweep(TestPolicy);

        var endpoint = (RouteEndpoint)builder.DataSources.Single().Endpoints.Single();
        await Assert.That(endpoint.RoutePattern.RawText).IsEqualTo("/internal/scheduler/sweep");
        await Assert.That(endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods).Contains("POST");
    }

    [Test]
    public async Task Maps_a_custom_route()
    {
        var builder = RouteBuilder(new FakeSweep());

        builder.MapSchedulerSweep(TestPolicy, "/hooks/sweep");

        var endpoint = (RouteEndpoint)builder.DataSources.Single().Endpoints.Single();
        await Assert.That(endpoint.RoutePattern.RawText).IsEqualTo("/hooks/sweep");
    }

    [Test]
    public async Task Guards_the_endpoint_with_the_supplied_policy()
    {
        var builder = RouteBuilder(new FakeSweep());

        builder.MapSchedulerSweep(TestPolicy);

        var authorize = builder.DataSources.Single().Endpoints.Single().Metadata.GetMetadata<IAuthorizeData>();
        await Assert.That(authorize).IsNotNull();
        await Assert.That(authorize!.Policy).IsEqualTo(TestPolicy);
    }

    [Test]
    public async Task Excludes_the_endpoint_from_api_description()
    {
        var builder = RouteBuilder(new FakeSweep());

        builder.MapSchedulerSweep(TestPolicy);

        var endpoint = builder.DataSources.Single().Endpoints.Single();
        await Assert.That(endpoint.Metadata.GetMetadata<IExcludeFromDescriptionMetadata>()).IsNotNull();
    }

    [Test]
    public async Task Returns_the_builder_for_chaining()
    {
        var builder = RouteBuilder(new FakeSweep());

        var returned = builder.MapSchedulerSweep(TestPolicy);

        await Assert.That(ReferenceEquals(returned, builder)).IsTrue();
    }

    [Test]
    public async Task A_request_runs_one_sweep_and_returns_200()
    {
        var sweep = new FakeSweep();
        var builder = RouteBuilder(sweep);
        builder.MapSchedulerSweep(TestPolicy);
        var endpoint = builder.DataSources.Single().Endpoints.Single();
        var httpContext = new DefaultHttpContext { RequestServices = builder.ServiceProvider };

        await endpoint.RequestDelegate!(httpContext);

        await Assert.That(sweep.Calls).IsEqualTo(1);
        await Assert.That(httpContext.Response.StatusCode).IsEqualTo(StatusCodes.Status200OK);
    }

    [Test]
    public async Task The_response_reports_what_the_sweep_dispatched()
    {
        // The external scheduler is the only thing that knows a sweep happened, so the count has to
        // travel back to it — otherwise a cron reports success identically whether it did work or not.
        var builder = RouteBuilder(new FakeSweep { JobsDispatched = 3 });
        builder.MapSchedulerSweep(TestPolicy);
        var endpoint = builder.DataSources.Single().Endpoints.Single();
        var body = new MemoryStream();
        var httpContext = new DefaultHttpContext { RequestServices = builder.ServiceProvider };
        httpContext.Response.Body = body;

        await endpoint.RequestDelegate!(httpContext);

        await Assert.That(Encoding.UTF8.GetString(body.ToArray())).Contains("\"jobsDispatched\":3");
    }

    [Test]
    public async Task A_failing_sweep_returns_500_so_the_trigger_retries()
    {
        var builder = RouteBuilder(new FakeSweep { ThrowOnSweep = new InvalidOperationException("boom") });
        builder.MapSchedulerSweep(TestPolicy);
        var endpoint = builder.DataSources.Single().Endpoints.Single();
        var httpContext = new DefaultHttpContext { RequestServices = builder.ServiceProvider };

        await endpoint.RequestDelegate!(httpContext);

        await Assert.That(httpContext.Response.StatusCode).IsEqualTo(StatusCodes.Status500InternalServerError);
    }

    [Test]
    public async Task Without_a_scheduler_registered_it_fails_at_map_time()
    {
        var builder = RouteBuilder();

        var ex = Assert.Throws<InvalidOperationException>(() => builder.MapSchedulerSweep(TestPolicy));

        // The message names the fix, not the missing type — that is what the reader needs.
        await Assert.That(ex.Message).Contains("AddFirestoreScheduler");
        await Assert.That(ex.Message).Contains("AddMssqlScheduler");
    }

    [Test]
    public async Task Without_a_policy_it_fails_at_map_time()
    {
        // An unguarded sweep endpoint would let anyone dispatch every due job.
        var builder = RouteBuilder(new FakeSweep());

        var ex = Assert.Throws<ArgumentException>(() => builder.MapSchedulerSweep("  "));

        await Assert.That(ex.Message).Contains("authorization policy");
    }
}
