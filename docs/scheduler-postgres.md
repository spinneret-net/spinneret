# Spinneret.Scheduler.Postgres

Scheduled jobs in PostgreSQL, beside the queue's [Postgres storage](queue-postgres.md). This page
covers what is specific to Postgres — configuration and the table. The engine — the sweep, the
recurring and one-shot schedulers, transactional scheduling and its guarantees — is the same for
every SQL dialect and lives in [scheduler-sql.md](scheduler-sql.md).

Read [scheduler.md](scheduler.md) first for declaring jobs and schedules.

## Install

```sh
dotnet add package Spinneret.Scheduler.Postgres
```

```csharp
services.AddQueue(q => q
    .AddRequestAssemblies(typeof(SyncCustomer).Assembly)
    .UsePostgresStorage(configuration)
    .UseInHostDelivery(configuration));   // or .UseCloudTasksDelivery(configuration)
services.AddScheduler(s => s.UsePostgres(configuration).UseSweeper());
```

## Requires

| | |
|---|---|
| **`q.UsePostgresStorage(...)`** | The queue stored in this database. Connection (string or data source), schema name and schema creation are all inherited from `Queue:Postgres`. Any other storage fails the host at startup. |
| **A trigger and a delivery** | As for every SQL scheduler — see [scheduler-sql.md](scheduler-sql.md#requires). |

## Configuration — `Scheduler:Postgres`

| Key | Type | Default | Notes |
|---|---|---|---|
| `TableName` | `string` | `"spinneret_scheduled_jobs"` | A plain SQL identifier; it and the index name derived from it must fit Postgres's 63 bytes, so the derived name is never silently truncated. |

That is the whole surface. Three more settings are read from the queue storage's own section, bound
by `UsePostgresStorage`:

| Key | What |
|---|---|
| `Queue:Postgres:ConnectionString` | Which database (unless the storage was given a data source). |
| `Queue:Postgres:SchemaName` | Which schema. |
| `Queue:Postgres:CreateSchema` | Whether the table is created at startup. |

## Infrastructure

One table, created idempotently at startup when the queue's `CreateSchema` is on, with the same
retry-while-the-database-warms-up behaviour as the queue's initializer, and the same
[concurrency safety](queue-postgres.md#owning-the-schema-yourself) — one `DO` block under a
transaction-scoped advisory lock, so a fleet starting together takes turns rather than racing:

```sql
public.spinneret_scheduled_jobs
    jobkey          varchar(200) PRIMARY KEY
    requesttypename varchar(500) NOT NULL
    payloadjson     text         NOT NULL
    schedule        varchar(500) NULL       -- NULL marks a one-shot job
    nextexecuteat   timestamptz  NOT NULL
    createdat       timestamptz  NOT NULL
    lastrunat       timestamptz  NULL       -- recurring only; a one-shot is deleted by that run
  + spinneret_scheduled_jobs_nextexecuteat_idx (nextexecuteat)
```

Owning the schema yourself means setting `Queue:Postgres:CreateSchema = false` — which turns off the
queue's tables too — and emitting both scripts from your migrations:

```csharp
var queueDdl     = PostgresQueueSchema.CreateScript(storageOptions);
var schedulerDdl = PostgresSchedulerSchema.CreateScript(storageOptions, schedulerOptions);
```

Note the scheduler script takes the queue storage's options as well, since it inherits the schema
name.

Runtime permissions: `SELECT`, `INSERT`, `UPDATE`, `DELETE` on the table — plus DDL rights only if
`CreateSchema` is on.

## Gotchas

- **Job keys are case-sensitive here**, unlike under SQL Server's usual collation. The installer
  rejects declared keys that differ only in case on every provider anyway, so a set of declarations
  behaves the same wherever it runs.
- **Registration is a single `INSERT … ON CONFLICT (jobkey) DO UPDATE`.** Concurrent registrations of
  a new key serialize on the primary key and converge on one row; an unchanged schedule (compared
  byte for byte, `COLLATE "C"`) keeps its cadence.
- **The sweep claims with `FOR UPDATE SKIP LOCKED`**: the row lock is held until the claim transaction
  commits, and competing sweeps pass over it to the next due job.
