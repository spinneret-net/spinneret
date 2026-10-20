using Microsoft.Extensions.Options;

namespace Spinneret.Queue.CloudTasks;

/// <summary>
/// Validates <see cref="CloudTasksOptions"/> against the command registry: required values are
/// present and every channel a registered command declares is mapped to a queue, so a missing
/// mapping fails the host at boot instead of throwing at first enqueue in some handler.
/// </summary>
internal sealed class CloudTasksOptionsValidator(QueueTypeRegistry registry) : IValidateOptions<CloudTasksOptions>
{
    public ValidateOptionsResult Validate(string? name, CloudTasksOptions options)
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

    public static void ValidateOrThrow(CloudTasksOptions o, QueueTypeRegistry registry)
    {
        if (string.IsNullOrWhiteSpace(o.ProjectId))
            throw new InvalidOperationException("Queue:CloudTasks:ProjectId must be set.");
        if (string.IsNullOrWhiteSpace(o.LocationId))
            throw new InvalidOperationException("Queue:CloudTasks:LocationId must be set.");
        if (!o.Channels.TryGetValue(QueuePolicy.DefaultChannel, out var defaultQueue) || string.IsNullOrWhiteSpace(defaultQueue))
            throw new InvalidOperationException($"Queue:CloudTasks:Channels:{QueuePolicy.DefaultChannel} must be set.");

        foreach (var channel in registry.DeclaredChannels)
        {
            if (!o.Channels.TryGetValue(channel, out var queueId) || string.IsNullOrWhiteSpace(queueId))
                throw new InvalidOperationException(
                    $"Queue channel '{channel}' is declared by a [QueuePolicy] but not mapped. " +
                    $"Add Queue:CloudTasks:Channels:{channel}.");
        }
        if (string.IsNullOrWhiteSpace(o.DispatcherUrl))
            throw new InvalidOperationException("Queue:CloudTasks:DispatcherUrl must be set.");
        if (string.IsNullOrWhiteSpace(o.ServiceAccountEmail))
            throw new InvalidOperationException("Queue:CloudTasks:ServiceAccountEmail must be set.");

        ValidateDispatcherUrl(o);
        ValidateOidc(o);
        ValidateRelay(o.Relay);
    }

    private static void ValidateRelay(CloudTasksRelayOptions relay)
    {
        if (relay.PollInterval <= TimeSpan.Zero)
            throw new InvalidOperationException("Queue:CloudTasks:Relay:PollInterval must be positive.");
        if (relay.BatchSize < 1)
            throw new InvalidOperationException("Queue:CloudTasks:Relay:BatchSize must be at least 1.");
        if (relay.LeaseDuration <= TimeSpan.Zero)
            throw new InvalidOperationException("Queue:CloudTasks:Relay:LeaseDuration must be positive.");
        if (relay.ForwardAhead < TimeSpan.Zero || relay.ForwardAhead > CloudTasksRelayOptions.MaxForwardAhead)
            throw new InvalidOperationException(
                $"Queue:CloudTasks:Relay:ForwardAhead must be between zero and {CloudTasksRelayOptions.MaxForwardAhead.TotalDays} days: "
                + "Cloud Tasks does not accept a task scheduled further ahead than that.");
    }

    /// <summary>
    /// Cloud Tasks resolves this URL itself, so a malformed one fails at the first enqueue rather
    /// than here — and a plain-http one fails at delivery, since Cloud Tasks will not mint an OIDC
    /// token for an insecure target. Both are worth catching at boot instead.
    /// </summary>
    private static void ValidateDispatcherUrl(CloudTasksOptions o)
    {
        if (!IsHttpUrl(o.DispatcherUrl, out var uri))
            throw new InvalidOperationException(
                $"Queue:CloudTasks:DispatcherUrl must be an absolute http(s) URL; got '{o.DispatcherUrl}'.");

        if (!o.UsesEmulator && uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException(
                $"Queue:CloudTasks:DispatcherUrl must use https; got '{o.DispatcherUrl}'. " +
                "Cloud Tasks only sends OIDC tokens to https targets. Plain http is accepted only " +
                "when Queue:CloudTasks:EmulatorEndpoint is set.");
    }

    /// <summary>
    /// The emulator mints tokens from its own issuer, but issuer validation stays on regardless of
    /// the emulator (only HTTPS metadata is relaxed). Leaving OidcIssuer unset there means the
    /// dispatch endpoint keeps validating against accounts.google.com and rejects every emulator
    /// token — a configuration that binds cleanly, starts cleanly, and then 401s forever.
    /// </summary>
    private static void ValidateOidc(CloudTasksOptions o)
    {
        if (o.UsesEmulator && string.IsNullOrWhiteSpace(o.OidcIssuer))
            throw new InvalidOperationException(
                "Queue:CloudTasks:OidcIssuer must be set when Queue:CloudTasks:EmulatorEndpoint is configured: " +
                "the dispatch endpoint would otherwise validate emulator-issued tokens against " +
                "https://accounts.google.com and reject every delivery. Set it to the emulator's " +
                "-openid-issuer value.");

        // Deliberately not validating OidcAudience: a JWT 'aud' is an opaque string, and Cloud Tasks
        // sends whatever is configured verbatim. Only the issuer must be a URL — it is fetched.
        if (!string.IsNullOrWhiteSpace(o.OidcIssuer) && !IsHttpUrl(o.OidcIssuer, out _))
            throw new InvalidOperationException(
                $"Queue:CloudTasks:OidcIssuer must be an absolute http(s) URL; got '{o.OidcIssuer}'. " +
                "It is the authority the dispatch endpoint fetches OpenID metadata from.");
    }

    /// <summary>
    /// An absolute URL that is actually addressable over HTTP.
    /// </summary>
    /// <remarks>
    /// The scheme check is what makes this platform-independent, and it is not optional: on Unix,
    /// <see cref="Uri.TryCreate(string, UriKind, out Uri)"/> reads a leading slash as a file path and
    /// happily produces an absolute <c>file://</c> URI, so a relative value like
    /// <c>/internal/queue/dispatch</c> passes an absoluteness check on Linux while failing it on
    /// Windows. Requiring http or https rejects it the same way everywhere.
    /// </remarks>
    internal static bool IsHttpUrl(string value, out Uri uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri!)
        && uri.Scheme is var scheme
        && (scheme == Uri.UriSchemeHttp || scheme == Uri.UriSchemeHttps);
}
