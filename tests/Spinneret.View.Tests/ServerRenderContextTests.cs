using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Spinneret.View.Tests;

public class ServerRenderContextTests
{
    [Test]
    public async Task IsClient_always_returns_false()
    {
        var context = new ServerRenderContext(new FakeHttpContextAccessor());

        await Assert.That(context.IsClient).IsFalse();
    }

    [Test]
    public async Task IsServer_always_returns_true()
    {
        var context = new ServerRenderContext(new FakeHttpContextAccessor());

        await Assert.That(context.IsServer).IsTrue();
    }

    [Test]
    public async Task IsPrerendering_no_http_context_returns_false()
    {
        var accessor = new FakeHttpContextAccessor { HttpContext = null };
        var context = new ServerRenderContext(accessor);

        await Assert.That(context.IsPrerendering).IsFalse();
    }

    [Test]
    public async Task IsPrerendering_response_not_started_returns_true()
    {
        var accessor = new FakeHttpContextAccessor { HttpContext = new FakeHttpContext(responseHasStarted: false) };
        var context = new ServerRenderContext(accessor);

        await Assert.That(context.IsPrerendering).IsTrue();
    }

    [Test]
    public async Task IsPrerendering_response_started_returns_false()
    {
        var accessor = new FakeHttpContextAccessor { HttpContext = new FakeHttpContext(responseHasStarted: true) };
        var context = new ServerRenderContext(accessor);

        await Assert.That(context.IsPrerendering).IsFalse();
    }

    private sealed class FakeHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class FakeHttpContext : HttpContext
    {
        private readonly HttpResponse _response;

        public FakeHttpContext(bool responseHasStarted)
        {
            _response = new FakeHttpResponse(this, responseHasStarted);
        }

        public override HttpResponse Response => _response;

        public override IFeatureCollection Features => throw new NotSupportedException();
        public override HttpRequest Request => throw new NotSupportedException();
        public override ConnectionInfo Connection => throw new NotSupportedException();
        public override WebSocketManager WebSockets => throw new NotSupportedException();
#pragma warning disable CS0672, CS0618
        public override Microsoft.AspNetCore.Http.Authentication.AuthenticationManager Authentication =>
            throw new NotSupportedException();
#pragma warning restore CS0672, CS0618
        public override ClaimsPrincipal User { get; set; } = new();
        public override IDictionary<object, object> Items { get; set; } = new Dictionary<object, object>();
        public override IServiceProvider RequestServices { get; set; } = null!;
        public override CancellationToken RequestAborted { get; set; }
        public override string TraceIdentifier { get; set; } = "";
        public override ISession Session { get; set; } = null!;

        public override void Abort()
        {
        }
    }

    private sealed class FakeHttpResponse(HttpContext context, bool hasStarted) : HttpResponse
    {
        public override bool HasStarted => hasStarted;

        public override HttpContext HttpContext => context;
        public override int StatusCode { get; set; } = 200;
        public override IHeaderDictionary Headers => throw new NotSupportedException();
        public override Stream Body { get; set; } = Stream.Null;
        public override long? ContentLength { get; set; }
        public override string ContentType { get; set; } = "";
        public override IResponseCookies Cookies => throw new NotSupportedException();

        public override void OnStarting(Func<object, Task> callback, object state)
        {
        }

        public override void OnCompleted(Func<object, Task> callback, object state)
        {
        }

        public override void Redirect(string location, bool permanent)
        {
        }
    }
}
