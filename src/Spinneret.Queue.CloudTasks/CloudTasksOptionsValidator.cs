using Microsoft.Extensions.Options;

namespace Spinneret.Queue.CloudTasks;

// Fails the host at boot on a missing value or unmapped channel, rather than at first enqueue.
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

    // Otherwise a malformed URL fails at first enqueue, and plain http at delivery (no OIDC token for http).
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

    // Without OidcIssuer the emulator's tokens are validated against Google's issuer and every delivery 401s.
    private static void ValidateOidc(CloudTasksOptions o)
    {
        if (o.UsesEmulator && string.IsNullOrWhiteSpace(o.OidcIssuer))
            throw new InvalidOperationException(
                "Queue:CloudTasks:OidcIssuer must be set when Queue:CloudTasks:EmulatorEndpoint is configured: " +
                "the dispatch endpoint would otherwise validate emulator-issued tokens against " +
                "https://accounts.google.com and reject every delivery. Set it to the emulator's " +
                "-openid-issuer value.");

        // OidcAudience is an opaque string; only the issuer must be a URL, since it is fetched.
        if (!string.IsNullOrWhiteSpace(o.OidcIssuer) && !IsHttpUrl(o.OidcIssuer, out _))
            throw new InvalidOperationException(
                $"Queue:CloudTasks:OidcIssuer must be an absolute http(s) URL; got '{o.OidcIssuer}'. " +
                "It is the authority the dispatch endpoint fetches OpenID metadata from.");
    }

    // The scheme check is required: on Unix, Uri.TryCreate reads "/path" as an absolute file:// URI.
    internal static bool IsHttpUrl(string value, out Uri uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri!)
        && uri.Scheme is var scheme
        && (scheme == Uri.UriSchemeHttp || scheme == Uri.UriSchemeHttps);
}
