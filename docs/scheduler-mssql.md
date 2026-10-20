# Spinneret.Scheduler.Mssql

Scheduled jobs in SQL Server, beside the queue's [SQL Server storage](queue-mssql.md). This page
covers what is specific to SQL Server — configuration and the table. The engine — the sweep, the
recurring and one-shot schedulers, transactional scheduling and its guarantees — is the same for
every SQL dialect and lives in [scheduler-sql.md](scheduler-sql.md).

Read [scheduler.md](scheduler.md) first for declaring jobs and schedules.

## Install

```sh
dotnet add package Spinneret.Scheduler.Mssql
```

```csharp
services.AddQueue(q => q
    .AddRequestAssemblies(typeof(SyncCustomer).Assembly)
    .UseMssqlStorage(configuration)
    .UseInHostDelivery(configuration));   // or .UseCloudTasksDelivery(configuration)
services.AddScheduler(s => s.UseMssqlStorage(configuration).UseInHostSweeper());
```

## Requires

| | |
|---|---|
| **`q.UseMssqlStorage(...)`** | The queue stored in this database. Connection string, schema name and schema creation are all inherited from `Queue:Mssql`. Any other storage fails the host at startup. |
| **A trigger and a delivery** | As for every SQL scheduler — see [scheduler-sql.md](scheduler-sql.md#requires). |

## Configuration — `Scheduler:Mssql`

| Key | Type | Default | Notes |
|---|---|---|---|
| `TableName` | `string` | `"SpinneretScheduledJobs"` | A plain SQL identifier; it and the index name derived from it must fit SQL Server's 128 characters. |

That is the whole surface. Three more settings are read from the queue storage's own section, bound
by `UseMssqlStorage`:

| Key | What |
|---|---|
| `Queue:Mssql:ConnectionString` | Which database. |
| `Queue:Mssql:SchemaName` | Which schema. |
| `Queue:Mssql:CreateSchema` | Whether the table is created at startup. |

## Infrastructure

One table, created idempotently at startup when the queue's `CreateSchema` is on, with the same
retry-while-the-database-warms-up behaviour as the queue's initializer, and the same
[concurrency safety](queue-mssql.md#owning-the-schema-yourself) — its own transaction and
application lock, so a fleet starting together serializes rather than racing:

```sql
[dbo].[SpinneretScheduledJobs]
    JobKey          NVARCHAR(200) PRIMARY KEY
    RequestTypeName NVARCHAR(500)  NOT NULL
    PayloadJson     NVARCHAR(MAX)  NOT NULL
    Schedule        NVARCHAR(500)  NULL       -- NULL marks a one-shot job
    NextExecuteAt   DATETIME2(3)   NOT NULL
    CreatedAt       DATETIME2(3)   NOT NULL
    LastRunAt       DATETIME2(3)   NULL       -- recurring only; a one-shot is deleted by that run
  + IX_SpinneretScheduledJobs_NextExecuteAt (NextExecuteAt)
```

Owning the schema yourself means setting `Queue:Mssql:CreateSchema = false` — which turns off the
queue's tables too — and emitting both scripts from your migrations:

```csharp
var queueDdl     = MssqlQueueSchema.CreateScript(storageOptions);
var schedulerDdl = MssqlSchedulerSchema.CreateScript(storageOptions, schedulerOptions);
```

Note the scheduler script takes the queue storage's options as well, since it inherits the schema
name.

Runtime permissions: `SELECT`, `INSERT`, `UPDATE`, `DELETE` on the table — plus DDL rights only if
`CreateSchema` is on.

## Gotchas

- **Job keys are compared case-insensitively.** Under the usual SQL Server collation `Sync` and `sync`
  are one row, so a pair that would coexist in Firestore or Postgres silently collapses here. The
  installer therefore rejects such pairs at startup on *every* provider — a case-sensitive collation
  does not re-enable them.
- **Registration is a single `MERGE … WITH (HOLDLOCK)`**, whose key-range lock is what makes
  concurrent registrations of a new key converge on one row.
- **The sweep skips rows other sweeps hold with `READPAST`**, under an `UPDLOCK` held until the claim
  transaction commits.
- **`Scheduler:Mssql:SweepInterval` no longer exists.** The cadence moved to `Scheduler:Sweeper:SweepInterval`
  along with the trigger. A leftover key binds to nothing and silently does nothing.
