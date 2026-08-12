using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Spinneret.Queue;
using Spinneret.Queue.Gcp;

namespace Spinneret.Scheduler.Gcp.Tests;

/// <summary>
/// Exercises endpoint registration metadata (against the shipped assembly via the public
/// <c>StartupExtensions.MapGcpSchedulerDispatch</c>) and the handler's failure path (against the
/// source-linked copy, whose dispatcher can be constructed with a null FirestoreDb so the sweep
/// throws). The success path requires a live Firestore query and is intentionally out of scope.
/// </summary>
public class SchedulerDispatchEndpointTests
{
    [Test]
    public async Task MapGcpSchedulerDispatch_registers_post_endpoint_on_internal_dispatch_route()
    {
        var builder = CreateRouteBuilder([]);

        StartupExtensions.MapGcpSchedulerDispatch(builder);

        var endpoint = (RouteEndpoint)builder.DataSources.Single().Endpoints.Single();
        await Assert.That(endpoint.RoutePattern.RawText).IsEqualTo("/internal/scheduler/dispatch");
        await Assert.That(endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods)
            .Contains("POST");
    }

    [Test]
    public async Task MapGcpSchedulerDispatch_requires_the_queue_oidc_policy()
    {
        var builder = CreateRouteBuilder([]);

        StartupExtensions.MapGcpSchedulerDispatch(builder);

        var endpoint = builder.DataSources.Single().Endpoints.Single();
        var authorize = endpoint.Metadata.GetMetadata<IAuthorizeData>();
        await Assert.That(authorize).IsNotNull();
        await Assert.That(authorize!.Policy).IsEqualTo(OidcAuthSetup.PolicyName);
    }

    [Test]
    public async Task MapGcpSchedulerDispatch_excludes_the_endpoint_from_api_description()
    {
        var builder = CreateRouteBuilder([]);

        StartupExtensions.MapGcpSchedulerDispatch(builder);

        var endpoint = builder.DataSources.Single().Endpoints.Single();
        await Assert.That(endpoint.Metadata.GetMetadata<IExcludeFromDescriptionMetadata>()).IsNotNull();
    }

    [Test]
    public async Task MapGcpSchedulerDispatch_returns_the_builder_for_chaining()
    {
        var builder = CreateRouteBuilder([]);

        var returned = StartupExtensions.MapGcpSchedulerDispatch(builder);

        await Assert.That(returned).IsSameReferenceAs(builder);
    }

    [Test]
    public async Task Dispatch_request_returns_500_when_the_sweep_throws()
    {
        // A dispatcher with a null FirestoreDb makes DispatchDueJobsAsync throw immediately,
        // driving the handler's catch path. Uses the source-linked endpoint + dispatcher.
        var services = new ServiceCollection();
        services.AddSingleton(new GcpSchedulerDispatcher(
            db: null!,
            Options.Create(new GcpSchedulerOptions()),
            new QueueTypeRegistry([]),
            new FakePayloadSerializer(),
            new FakeQueue(),
            new FakeDeadLetterWriter(),
            NullLogger<GcpSchedulerDispatcher>.Instance));
        var builder = CreateRouteBuilder(services);
        SchedulerDispatchEndpoint.MapGcpSchedulerDispatch(builder);
        var endpoint = builder.DataSources.Single().Endpoints.Single();
        var httpContext = new DefaultHttpContext { RequestServices = builder.ServiceProvider };

        await endpoint.RequestDelegate!(httpContext);

        await Assert.That(httpContext.Response.StatusCode)
            .IsEqualTo(StatusCodes.Status500InternalServerError);
    }

    private static TestEndpointRouteBuilder CreateRouteBuilder(ServiceCollection services)
    {
        services.AddLogging();
        services.AddRouting();
        return new TestEndpointRouteBuilder(services.BuildServiceProvider());
    }

    private sealed class TestEndpointRouteBuilder(IServiceProvider serviceProvider) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider => serviceProvider;
        public ICollection<EndpointDataSource> DataSources { get; } = [];
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(serviceProvider);
    }
}
