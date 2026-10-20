using Microsoft.Extensions.Options;

namespace Spinneret.Queue.Mssql;

/// <summary>Validates <see cref="MssqlStorageOptions"/>: a connection to use, and identifiers that are safe to splice into SQL.</summary>
internal sealed class MssqlStorageOptionsValidator : IValidateOptions<MssqlStorageOptions>
{
    public ValidateOptionsResult Validate(string? name, MssqlStorageOptions options)
    {
        try
        {
            ValidateOrThrow(options);
            return ValidateOptionsResult.Success;
        }
        catch (InvalidOperationException ex)
        {
            return ValidateOptionsResult.Fail(ex.Message);
        }
    }

    public static void ValidateOrThrow(MssqlStorageOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.ConnectionString))
            throw new InvalidOperationException(
                "Queue:Mssql:ConnectionString must be set — directly, or as a ConnectionStrings entry named by Queue:Mssql:ConnectionStringName.");

        ValidateIdentifier(o.SchemaName, "Queue:Mssql:SchemaName");
        ValidateIdentifier(o.QueueTableName, "Queue:Mssql:QueueTableName");
        ValidateIdentifier(o.DeadLetterTableName, "Queue:Mssql:DeadLetterTableName");
    }

    private static void ValidateIdentifier(string value, string configKey)
    {
        if (!Identifier.IsValid(value))
            throw new InvalidOperationException(
                $"{configKey} must be a plain SQL identifier (letters, digits, underscore); got '{value}'.");
    }
}
