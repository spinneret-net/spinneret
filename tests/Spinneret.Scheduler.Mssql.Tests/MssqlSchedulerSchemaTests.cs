using Spinneret.Queue.Mssql;

namespace Spinneret.Scheduler.Mssql.Tests;

public sealed class MssqlSchedulerSchemaTests
{
    [Test]
    public async Task Schema_script_uses_the_configured_names()
    {
        var script = MssqlSchedulerSchema.CreateScript(
            new MssqlStorageOptions { SchemaName = "jobs" },
            new MssqlSchedulerOptions { TableName = "MyJobs" });

        await Assert.That(script).Contains("[jobs].[MyJobs]");
        await Assert.That(script).Contains("IF OBJECT_ID(N'[jobs].[MyJobs]', N'U') IS NULL");
        await Assert.That(script).Contains("[IX_MyJobs_NextExecuteAt]");
        // Existence is the state, so there is no status column to index on.
        await Assert.That(script).DoesNotContain("Status");
    }

    [Test]
    public async Task Schema_script_serializes_concurrent_hosts_with_an_application_lock()
    {
        var script = MssqlSchedulerSchema.CreateScript(new MssqlStorageOptions(), new MssqlSchedulerOptions());

        await Assert.That(script).Contains("sp_getapplock");
        await Assert.That(script).Contains("@LockOwner = 'Transaction'");
    }
}
