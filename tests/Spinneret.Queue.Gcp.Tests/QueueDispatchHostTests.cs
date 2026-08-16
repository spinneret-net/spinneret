using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Spinneret.Queue.Gcp.Tests;

/// <summary>
/// The dispatch endpoint over a real ASP.NET pipeline rather than an invoked handler: that Cloud
/// Tasks' POST reaches the delivery processor only when its OIDC token validates, and that the
/// status and headers it gets back are the ones its retry behaviour depends on.
/// </summary>
/// <remarks>
/// <para>
/// The companion suite asserts what <c>MapGcpQueueDispatch</c> registers and what the handler
/// returns when invoked directly. Neither can show that the endpoint is actually guarded:
/// authorization is middleware, so only a request through the pipeline distinguishes a route that
/// enforces the policy from one that merely carries its metadata.
/// </para>
/// <para>
/// Tokens are signed with a symmetric test key and the scheme's <c>Configuration</c> is pre-set, so
/// the JWT handler never reaches out to accounts.google.com for metadata. Everything else about the
/// scheme — issuer, audience and lifetime validation — is exactly what <c>AddGcpQueue</c> configured.
/// </para>
/// </remarks>
public sealed class QueueDispatchHostTests
{
    private const string TaskNameHeader = "X-CloudTasks-TaskName";
    private const string FullTaskName = "projects/p/locations/l/queues/q/tasks/task-123";

    /// <summary>The route the endpoint derives from <see cref="TestSetup.DispatcherUrl"/>.</summary>
    private const string DispatchRoute = "/internal/queue/dispatch";

    private static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes("a-test-signing-key-long-enough-for-hmac-sha256"));

    private sealed class FakeProcessor : IQueueDeliveryProcessor
    {
        public QueueDeliveryOutcome Outcome { get; set; } = QueueDeliveryOutcome.Acked;
        public QueueEnvelope? Envelope { get; private set; }
        public string? TaskId { get; private set; }
        public int Calls { get; private set; }

        public Task<QueueDeliveryOutcome> ProcessAsync(QueueDeliveryContext context, CancellationToken ct)
        {
            Calls++;
            Envelope = context.Envelope;
            TaskId = context.TaskId;
            return Task.FromResult(Outcome);
        }
    }

    private sealed class FakeDeadLetterWriter : IDeadLetterWriter
    {
        public List<DeadLetterEntry> Entries { get; } = [];
        public Exception? ThrowOnWrite { get; set; }

        public Task WriteAsync(DeadLetterEntry entry, CancellationToken ct = default)
        {
            if (ThrowOnWrite is not null)
                throw ThrowOnWrite;

            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private static async Task<IHost> StartHostAsync(FakeProcessor processor, FakeDeadLetterWriter deadLetters) =>
        await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddGcpQueue(
                        TestSetup.Config(),
                        o => o.RequestAssemblies = [typeof(QueueDispatchHostTests).Assembly]);
                    // Replace the pieces a worker would supply, so the endpoint's own behaviour is
                    // what is under test rather than a real handler or store.
                    services.AddSingleton<IQueueDeliveryProcessor>(processor);
                    services.AddSingleton<IDeadLetterWriter>(deadLetters);
                    services.AddSingleton<IConfigureOptions<JwtBearerOptions>>(
                        new ConfigureNamedOptions<JwtBearerOptions>(OidcAuthSetup.SchemeName, ValidateAgainstTestKey));
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapGcpQueueDispatch());
                }))
            .StartAsync();

    /// <summary>
    /// Points the scheme at a local signing key and a pre-set OIDC document, so no network fetch
    /// happens. Issuer, audience and lifetime validation stay exactly as AddGcpQueue set them.
    /// </summary>
    private static void ValidateAgainstTestKey(JwtBearerOptions options)
    {
        options.Configuration = new OpenIdConnectConfiguration();
        options.TokenValidationParameters.IssuerSigningKey = SigningKey;
        options.TokenValidationParameters.ValidateIssuerSigningKey = true;
    }

    private static string Token(
        string issuer = "https://accounts.google.com",
        string audience = TestSetup.DispatcherUrl,
        DateTime? expires = null)
    {
        // notBefore is derived from the expiry rather than from now, so an already-expired token is
        // still a well-formed one — the handler rejects a nbf-after-exp pair before it ever gets to
        // lifetime validation, which would test nothing.
        var expiresAt = expires ?? DateTime.UtcNow.AddMinutes(10);
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: [new Claim(ClaimTypes.NameIdentifier, TestSetup.ServiceAccountEmail)],
            notBefore: expiresAt.AddMinutes(-15),
            expires: expiresAt,
            signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static HttpRequestMessage Post(string body, string? token, string route = DispatchRoute)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(TaskNameHeader, FullTaskName);
        return request;
    }

    private static string EnvelopeJson(string typeName = "Some.Command", string payload = """{"name":"ada"}""") =>
        JsonSerializer.Serialize(new QueueEnvelope
        {
            RequestTypeName = typeName,
            PayloadJson = payload,
            EnqueuedAtUtc = DateTimeOffset.UtcNow,
        });

    // -------------------------------------------------------------------------- the guard ---

    [Test]
    public async Task An_unauthenticated_post_never_reaches_the_processor()
    {
        // The endpoint executes arbitrary registered commands, so an unguarded route is remote code
        // execution. Metadata alone cannot show the policy runs — this request can.
        var processor = new FakeProcessor();
        using var host = await StartHostAsync(processor, new FakeDeadLetterWriter());

        var response = await host.GetTestClient().SendAsync(Post(EnvelopeJson(), token: null));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(processor.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task A_token_for_another_audience_is_refused()
    {
        // The audience is what stops a token minted for a different service being replayed here.
        var processor = new FakeProcessor();
        using var host = await StartHostAsync(processor, new FakeDeadLetterWriter());

        var response = await host.GetTestClient()
            .SendAsync(Post(EnvelopeJson(), Token(audience: "https://some-other-service.example.com")));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(processor.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task A_token_from_another_issuer_is_refused()
    {
        var processor = new FakeProcessor();
        using var host = await StartHostAsync(processor, new FakeDeadLetterWriter());

        var response = await host.GetTestClient()
            .SendAsync(Post(EnvelopeJson(), Token(issuer: "https://not-google.example.com")));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(processor.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task An_expired_token_is_refused()
    {
        // Lifetime validation runs with a minute of clock skew, so the token is aged past that.
        var processor = new FakeProcessor();
        using var host = await StartHostAsync(processor, new FakeDeadLetterWriter());

        var response = await host.GetTestClient()
            .SendAsync(Post(EnvelopeJson(), Token(expires: DateTime.UtcNow.AddMinutes(-10))));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(processor.Calls).IsEqualTo(0);
    }

    // ------------------------------------------------------------------------- the delivery ---

    [Test]
    public async Task An_authorized_delivery_reaches_the_processor_and_acks()
    {
        var processor = new FakeProcessor();
        using var host = await StartHostAsync(processor, new FakeDeadLetterWriter());

        var response = await host.GetTestClient().SendAsync(Post(EnvelopeJson("Orders.Ship"), Token()));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(processor.Envelope!.RequestTypeName).IsEqualTo("Orders.Ship");
        // Only the final segment of the Cloud Tasks task name: slashes are not valid document ids.
        await Assert.That(processor.TaskId).IsEqualTo("task-123");
    }

    [Test]
    public async Task A_retry_outcome_returns_429_with_the_backoff_Cloud_Tasks_honors()
    {
        // The queue's own retry config is an unlimited backstop, so a task ends only on a 200 —
        // which makes the 429 and its Retry-After the entire flow-control mechanism.
        var processor = new FakeProcessor { Outcome = QueueDeliveryOutcome.RetryIn(TimeSpan.FromSeconds(42)) };
        using var host = await StartHostAsync(processor, new FakeDeadLetterWriter());

        var response = await host.GetTestClient().SendAsync(Post(EnvelopeJson(), Token()));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(response.Headers.RetryAfter!.Delta).IsEqualTo(TimeSpan.FromSeconds(42));
    }

    [Test]
    public async Task A_sub_second_backoff_still_asks_for_at_least_one_second()
    {
        var processor = new FakeProcessor { Outcome = QueueDeliveryOutcome.RetryIn(TimeSpan.FromMilliseconds(1)) };
        using var host = await StartHostAsync(processor, new FakeDeadLetterWriter());

        var response = await host.GetTestClient().SendAsync(Post(EnvelopeJson(), Token()));

        await Assert.That(response.Headers.RetryAfter!.Delta).IsEqualTo(TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task An_unreadable_body_is_dead_lettered_and_acked()
    {
        // No retry can repair the bytes, so the raw body is stored for inspection and the task ends.
        var deadLetters = new FakeDeadLetterWriter();
        var processor = new FakeProcessor();
        using var host = await StartHostAsync(processor, deadLetters);

        var response = await host.GetTestClient().SendAsync(Post("{ this is not an envelope", Token()));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(processor.Calls).IsEqualTo(0);
        var entry = deadLetters.Entries.Single();
        await Assert.That(entry.IdempotencyKey).IsEqualTo("task-123");
        await Assert.That(entry.CommandTypeName).IsEqualTo("<unreadable envelope>");
        await Assert.That(entry.PayloadJson).IsEqualTo("{ this is not an envelope");
    }

    [Test]
    public async Task An_unreadable_body_whose_dead_letter_write_fails_is_retried_instead_of_dropped()
    {
        var deadLetters = new FakeDeadLetterWriter { ThrowOnWrite = new InvalidOperationException("store is down") };
        using var host = await StartHostAsync(new FakeProcessor(), deadLetters);

        var response = await host.GetTestClient().SendAsync(Post("{ this is not an envelope", Token()));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(response.Headers.RetryAfter!.Delta).IsEqualTo(TimeSpan.FromMinutes(1));
    }

    // ------------------------------------------------------------------------------ routing ---

    [Test]
    public async Task The_endpoint_answers_on_the_path_of_the_configured_dispatcher_url()
    {
        // A mapped route that does not match DispatcherUrl is close to undetectable in production:
        // every task 404s and retries until it expires.
        using var host = await StartHostAsync(new FakeProcessor(), new FakeDeadLetterWriter());

        var configured = new Uri(TestSetup.DispatcherUrl).AbsolutePath;
        var response = await host.GetTestClient().SendAsync(Post(EnvelopeJson(), Token(), configured));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task A_get_is_not_a_delivery()
    {
        var processor = new FakeProcessor();
        using var host = await StartHostAsync(processor, new FakeDeadLetterWriter());

        var client = host.GetTestClient();
        var request = new HttpRequestMessage(HttpMethod.Get, DispatchRoute);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token());
        var response = await client.SendAsync(request);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.MethodNotAllowed);
        await Assert.That(processor.Calls).IsEqualTo(0);
    }
}
