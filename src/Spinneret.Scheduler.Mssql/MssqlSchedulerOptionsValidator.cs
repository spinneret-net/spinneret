using Microsoft.Extensions.Options;
using Spinneret.Queue.Mssql;

namespace Spinneret.Scheduler.Mssql;

internal sealed class MssqlSchedulerOptionsValidator : IValidateOptions<MssqlSchedulerOptions>
{
    public ValidateOptionsResult Validate(string? name, MssqlSchedulerOptions options)
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

    public static void ValidateOrThrow(MssqlSchedulerOptions o)
    {
        if (!Identifier.IsValid(o.TableName))
            throw new InvalidOperationException(
                $"Scheduler:Mssql:TableName must be a plain SQL identifier (letters, digits, underscore); got '{o.TableName}'.");

        if (o.SweepInterval <= TimeSpan.Zero)
            throw new InvalidOperationException("Scheduler:Mssql:SweepInterval must be positive.");
    }
}
