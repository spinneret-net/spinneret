namespace Spinneret.Queue.Gcp;

public sealed class GcpQueueOptions
{
    public const string SectionName = "Queue:Gcp";

    public string ProjectId { get; set; } = string.Empty;
    public string LocationId { get; set; } = string.Empty;

    /// <summary>
    /// Channel → Cloud Tasks queue id. A command declares its channel via
    /// <c>[QueuePolicy(Channel = ...)]</c> and rides <see cref="QueuePolicy.DefaultChannel"/> when it
    /// declares none, so that entry is mandatory; every other declared channel must be mapped here too.
    /// </summary>
    public Dictionary<string, string> Channels { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Absolute URL Cloud Tasks should POST to with each dispatched task.
    /// </summary>
    public string DispatcherUrl { get; set; } = string.Empty;

    /// <summary>
    /// Service account Cloud Tasks impersonates when minting the OIDC bearer token
    /// for each dispatch. The dispatch endpoint validates the resulting JWT.
    /// </summary>
    public string ServiceAccountEmail { get; set; } = string.Empty;

    /// <summary>
    /// JWT audience claim required by the dispatch endpoint. Defaults to
    /// <see cref="DispatcherUrl"/> when null.
    /// </summary>
    public string? OidcAudience { get; set; }

    /// <summary>
    /// When set, the Cloud Tasks client targets the emulator at this gRPC endpoint
    /// (e.g. <c>localhost:8123</c>) using insecure credentials. Production runs leave
    /// this null.
    /// </summary>
    public string? EmulatorEndpoint { get; set; }

    /// <summary>
    /// OIDC issuer URL used by the dispatch endpoint's JwtBearer middleware to fetch
    /// JWKS. In production this is <c>https://accounts.google.com</c> (the default
    /// when null); in dev it matches the emulator's <c>-openid-issuer</c> flag.
    /// </summary>
    public string? OidcIssuer { get; set; }

    public bool UsesEmulator => !string.IsNullOrWhiteSpace(EmulatorEndpoint);

    public string ResolvedOidcAudience =>
        string.IsNullOrWhiteSpace(OidcAudience) ? DispatcherUrl : OidcAudience;

    public string ResolvedOidcIssuer =>
        string.IsNullOrWhiteSpace(OidcIssuer) ? "https://accounts.google.com" : OidcIssuer;

    public string QueueIdFor(string channel) =>
        Channels.TryGetValue(channel, out var queueId)
            ? queueId
            : throw new InvalidOperationException(
                $"Queue channel '{channel}' is not configured. Add Queue:Gcp:Channels:{channel}.");
}
