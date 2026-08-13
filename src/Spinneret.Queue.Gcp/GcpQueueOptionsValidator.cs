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
    }
}
