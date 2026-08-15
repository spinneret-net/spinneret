using Microsoft.Extensions.Options;

namespace Spinneret.Queue.Gcp;

/// <summary>
/// Validates <see cref="GcpQueueOptions"/> against the command registry: required values are
/// present and every channel a registered command declares is mapped to a queue, so a missing
/// mapping fails the host at boot instead of throwing at first enqueue in some handler.
/// </summary>
internal sealed class GcpQueueOptionsValidator(QueueTypeRegistry registry) : IValidateOptions<GcpQueueOptions>
{
    public ValidateOptionsResult Validate(string? name, GcpQueueOptions options)
    {
        try
        {
            ValidateOrThrow(options, registry);
            return ValidateOptionsResult.Success;
        }
        catch (InvalidOperationException ex)
        {
            return ValidateOptionsResult.Fail(ex.Message);
        }
    }

    public static void ValidateOrThrow(GcpQueueOptions o, QueueTypeRegistry registry)
    {
        if (string.IsNullOrWhiteSpace(o.ProjectId))
            throw new InvalidOperationException("Queue:Gcp:ProjectId must be set.");
        if (string.IsNullOrWhiteSpace(o.LocationId))
            throw new InvalidOperationException("Queue:Gcp:LocationId must be set.");
        if (!o.Channels.TryGetValue(QueuePolicy.DefaultChannel, out var defaultQueue) || string.IsNullOrWhiteSpace(defaultQueue))
            throw new InvalidOperationException($"Queue:Gcp:Channels:{QueuePolicy.DefaultChannel} must be set.");

        foreach (var channel in registry.DeclaredChannels)
        {
            if (!o.Channels.TryGetValue(channel, out var queueId) || string.IsNullOrWhiteSpace(queueId))
                throw new InvalidOperationException(
                    $"Queue channel '{channel}' is declared by a [QueuePolicy] but not mapped. " +
                    $"Add Queue:Gcp:Channels:{channel}.");
        }
        if (string.IsNullOrWhiteSpace(o.DispatcherUrl))
            throw new InvalidOperationException("Queue:Gcp:DispatcherUrl must be set.");
        if (string.IsNullOrWhiteSpace(o.ServiceAccountEmail))
            throw new InvalidOperationException("Queue:Gcp:ServiceAccountEmail must be set.");

        ValidateDispatcherUrl(o);
        ValidateOidc(o);
    }

    /// <summary>
    /// Cloud Tasks resolves this URL itself, so a malformed one fails at the first enqueue rather
    /// than here — and a plain-http one fails at delivery, since Cloud Tasks will not mint an OIDC
    /// token for an insecure target. Both are worth catching at boot instead.
    /// </summary>
    private static void ValidateDispatcherUrl(GcpQueueOptions o)
    {
        if (!Uri.TryCreate(o.DispatcherUrl, UriKind.Absolute, out var uri))
            throw new InvalidOperationException(
                $"Queue:Gcp:DispatcherUrl must be an absolute URL; got '{o.DispatcherUrl}'.");

        if (!o.UsesEmulator && uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException(
                $"Queue:Gcp:DispatcherUrl must use https; got '{o.DispatcherUrl}'. " +
                "Cloud Tasks only sends OIDC tokens to https targets. Plain http is accepted only " +
                "when Queue:Gcp:EmulatorEndpoint is set.");
    }

    /// <summary>
    /// The emulator mints tokens from its own issuer, but issuer validation stays on regardless of
    /// the emulator (only HTTPS metadata is relaxed). Leaving OidcIssuer unset there means the
    /// dispatch endpoint keeps validating against accounts.google.com and rejects every emulator
    /// token — a configuration that binds cleanly, starts cleanly, and then 401s forever.
    /// </summary>
    private static void ValidateOidc(GcpQueueOptions o)
    {
        if (o.UsesEmulator && string.IsNullOrWhiteSpace(o.OidcIssuer))
            throw new InvalidOperationException(
                "Queue:Gcp:OidcIssuer must be set when Queue:Gcp:EmulatorEndpoint is configured: " +
                "the dispatch endpoint would otherwise validate emulator-issued tokens against " +
                "https://accounts.google.com and reject every delivery. Set it to the emulator's " +
                "-openid-issuer value.");

        // Deliberately not validating OidcAudience: a JWT 'aud' is an opaque string, and Cloud Tasks
        // sends whatever is configured verbatim. Only the issuer must be a URL — it is fetched.
        if (!string.IsNullOrWhiteSpace(o.OidcIssuer)
            && !Uri.TryCreate(o.OidcIssuer, UriKind.Absolute, out _))
            throw new InvalidOperationException(
                $"Queue:Gcp:OidcIssuer must be an absolute URL; got '{o.OidcIssuer}'. " +
                "It is the authority the dispatch endpoint fetches OpenID metadata from.");
    }
}
