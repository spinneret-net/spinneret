namespace Spinneret.Queue.CloudTasks;

public sealed class CloudTasksOptions
{
    public static readonly string SectionName = "Queue:CloudTasks";

    public string ProjectId { get; set; } = string.Empty;

    /// <summary>How a SQL storage's outbox is forwarded to Cloud Tasks, when the queue has one.</summary>
    public CloudTasksRelayOptions Relay { get; set; } = new();
    public string LocationId { get; set; } = string.Empty;

    /// <summary>
    /// Channel to Cloud Tasks queue id; <see cref="QueuePolicy.DefaultChannel"/> and every declared channel must be mapped.
    /// </summary>
    public Dictionary<string, string> Channels { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Absolute URL Cloud Tasks POSTs each task to.</summary>
    public string DispatcherUrl { get; set; } = string.Empty;

    /// <summary>Service account Cloud Tasks uses to mint each dispatch's OIDC token.</summary>
    public string ServiceAccountEmail { get; set; } = string.Empty;

    /// <summary>Required JWT audience; defaults to <see cref="DispatcherUrl"/>.</summary>
    public string? OidcAudience { get; set; }

    /// <summary>Emulator gRPC endpoint (e.g. <c>localhost:8123</c>); null in production.</summary>
    public string? EmulatorEndpoint { get; set; }

    /// <summary>OIDC issuer URL; defaults to <c>https://accounts.google.com</c>, or the emulator's <c>-openid-issuer</c>.</summary>
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
                $"Queue channel '{channel}' is not configured. Add Queue:CloudTasks:Channels:{channel}.");
}
