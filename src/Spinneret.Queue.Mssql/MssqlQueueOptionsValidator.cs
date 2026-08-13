using Microsoft.Extensions.Options;

namespace Spinneret.Queue.Mssql;

/// <summary>
/// Validates <see cref="MssqlQueueOptions"/> against the command registry: identifiers are safe,
/// intervals positive, and every configured channel-parallelism key matches a declared channel,
/// so a typo fails the host at boot instead of silently spinning workers for a channel no
/// command rides on.
/// </summary>
internal sealed class MssqlQueueOptionsValidator(QueueTypeRegistry registry) : IValidateOptions<MssqlQueueOptions>
{
    public ValidateOptionsResult Validate(string? name, MssqlQueueOptions options)
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

    public static void ValidateOrThrow(MssqlQueueOptions o, QueueTypeRegistry registry)
    {
        if (string.IsNullOrWhiteSpace(o.ConnectionString))
            throw new InvalidOperationException(
                "Queue:Mssql:ConnectionString must be set — directly, or as a ConnectionStrings entry named by Queue:Mssql:ConnectionStringName.");

        ValidateIdentifier(o.SchemaName, "Queue:Mssql:SchemaName");
        ValidateIdentifier(o.QueueTableName, "Queue:Mssql:QueueTableName");
        ValidateIdentifier(o.DeadLetterTableName, "Queue:Mssql:DeadLetterTableName");

        if (o.PollInterval <= TimeSpan.Zero)
            throw new InvalidOperationException("Queue:Mssql:PollInterval must be positive.");

        foreach (var (channel, parallelism) in o.ChannelParallelism)
        {
            if (channel != QueuePolicy.DefaultChannel && !registry.DeclaredChannels.Contains(channel, StringComparer.Ordinal))
                throw new InvalidOperationException(
                    $"Queue:Mssql:ChannelParallelism:{channel} does not match any channel declared by a [QueuePolicy].");
            if (parallelism < 1)
                throw new InvalidOperationException(
                    $"Queue:Mssql:ChannelParallelism:{channel} must be at least 1.");
        }
    }

    private static void ValidateIdentifier(string value, string configKey)
    {
        if (!Identifier.IsValid(value))
            throw new InvalidOperationException(
                $"{configKey} must be a plain SQL identifier (letters, digits, underscore); got '{value}'.");
    }
}
