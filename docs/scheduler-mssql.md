# Spinneret.Scheduler.Mssql

Scheduled jobs stored as rows beside the queue's SQL Server storage, plus the sweep that dispatches
them.

Unlike the Firestore scheduler, this one requires the queue's *storage* to be SQL Server: it stores
jobs in the same database and dispatches onto the outbox inside one transaction, so a run can neither
be lost nor double-enqueued. The delivery is free — in-host workers or Cloud Tasks.

The engine — the sweep, the recurring and one-shot schedulers — is written once against ADO.NET in
`Spinneret.Scheduler.Sql`; this package is its SQL Server dialect and brings it in.

Read [scheduler.md](scheduler.md) first for declaring jobs and schedules; this page is the SQL half.

## Install

```sh
dotnet add package Spinneret.Scheduler.Mssql
```

```csharp
services.AddQueue(q => q
    .AddRequestAssemblies(typeof(SyncCustomer).Assembly)
    .UseMssqlStorage(configuration)
    .UseInHostDelivery(configuration));   // or .UseCloudTasksDelivery(configuration)
services.AddScheduler(s => s.UseMssql(configuration).UseSweeper());
```

`AddQueue` and `AddScheduler` can be called in either order.

## Requires

| | |
|---|---|
| **`q.UseMssqlStorage(...)`** | Not merely any queue — one stored in this database. Connection string, schema name and schema creation are all inherited from `Queue:Mssql`. A different storage fails the host at startup. |
| **A trigger** | `s.UseSweeper()` for a timer, or [`Spinneret.Scheduler.Http`](scheduler-http.md) for an endpoint. |
| **A delivery somewhere** | The sweep only enqueues; an in-host worker or Cloud Tasks delivers. |

The dead-letter writer comes from the queue's storage; nothing extra is needed.

## Configuration — `Scheduler:Mssql`

| Key | Type | Default | Notes |
|---|---|---|---|
| `TableName` | `string` | `"SpinneretScheduledJobs"` | Must be a plain SQL identifier. |

That is the whole surface. Three more settings are read from the queue storage's own section, bound
by `UseMssqlStorage`:

| Key | What |
|---|---|
| `Queue:Mssql:ConnectionString` | Which database. |
| `Queue:Mssql:SchemaName` | Which schema. |
| `Queue:Mssql:CreateSchema` | Whether the table is created at startup. |

The sweep cadence is **not** one of them. `Scheduler:Sweeper:SweepInterval` belongs to the trigger,
and nothing binds it for you — putting it in `appsettings.json` alone changes nothing. Bind it
yourself, or set it in code:

```csharp
services.Configure<SchedulerOptions>(
    configuration.GetSection(SchedulerOptions.SectionName));
services.AddScheduler(s => s.UseMssql(configuration).UseSweeper());

// or, without configuration:
services.AddScheduler(s => s.UseMssql(configuration).UseSweeper(o => o.SweepInterval = TimeSpan.FromMinutes(1)));
```

See [scheduler.md](scheduler.md#driving-the-sweep).

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

### There is no status column

A row exists only while it is still work to do. A one-shot is deleted by the same transaction that
enqueues it, or — if the dispatch failed — by the one that writes its dead letter; cancelling deletes
it outright. So the table stays proportional to what is scheduled rather than to everything ever
scheduled, and needs no retention job. The record of a run lives in the logs and the queue, not here.

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

## Transactional one-shot jobs

Schedule a job as part of a transaction you already own, so it commits with the change that caused it:

```csharp
await using var tx = await connection.BeginTransactionAsync();
await repository.SaveAsync(employee, tx);
var handle = await scheduler.ScheduleJobAsync(tx, new RemoveEmployee(id), employee.LeavingDate);
await tx.CommitAsync();
```

`ScheduleJobAsync` returns the handle for `CancelJobAsync`, prefixed `oneshot-`. Cancelling deletes
the row: a job that already dispatched is gone, so cancelling it — or an unknown handle — is a silent
no-op. Passing a *recurring* key throws `ArgumentException`; those share this table, and cancelling
deletes, so the prefix is what stops a mixed-up call from destroying a live schedule. Retire those
with `UnregisterAsync`.

The row is identical to the ones the recurring scheduler writes, so the same sweep picks it up.

Note the recurring API (`RegisterAsync` / `UnregisterAsync`) does **not** join an ambient transaction —
it always manages its own. Only one-shot scheduling is enlistable.

## How the sweep behaves

Each due job is claimed under a row lock and dispatched in its own transaction: the claim, the booking
and the outbox insert commit together. Over Cloud Tasks the relay forwards the message as that
transaction commits. Competing hosts skip locked rows (`READPAST`) and dispatch other
due jobs in parallel, so running the sweep on several instances is expected rather than merely
tolerated.

A sweep **drains** — it keeps going until nothing is due — so a backlog clears at full speed rather
than one job per tick. (The Firestore sweep processes one snapshot instead.)

A dispatch failure compensates on a fresh transaction: a one-shot is deleted in the same transaction
that records its dead letter — so the row is only ever removed together with the record that replaces
it, and a failure to write that record rolls both back and leaves the job due. A recurring job
dead-letters that occurrence but keeps its schedule armed.

## Gotchas

- **Same database as the queue's storage.** The one-transaction guarantee is the entire reason this
  package exists; a separate scheduler database would need MSDTC and would lose it.
- **Job keys are compared case-insensitively.** Under the usual SQL Server collation `Sync` and `sync`
  are one row, so a pair that would coexist in Firestore silently collapses here. The installer
  therefore rejects such pairs at startup on *every* provider — a case-sensitive collation does not
  re-enable them.
- **A slow enqueue holds a row lock** for the length of the claim transaction.
- **Compensation can be a no-op** if a competing sweep re-claimed the job; the dead letter is only
  written when the compensating statement actually applied.
- **Never hand-delete rows without a `Schedule IS NOT NULL` guard.** One-shot handles live in the same
  `JobKey` namespace and are exactly the rows with no schedule.
- **A missing row is not an error.** Since a finished job leaves nothing behind, "the job is gone" and
  "the job never existed" are the same observation. `RegisterAsync` treats both as "create it".
- **Registration is one statement.** `RegisterAsync` is a single `MERGE … WITH (HOLDLOCK)`, so every
  replica registering its declared jobs at startup converges on one row per key.
- **`Scheduler:Mssql:SweepInterval` no longer exists.** The cadence moved to `Scheduler:Sweeper:SweepInterval`
  along with the trigger. A leftover key binds to nothing and silently does nothing.
