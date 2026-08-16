# Spinneret.Scheduler.Mssql

Scheduled jobs stored as rows beside the SQL Server queue, plus the sweep that dispatches them.

Unlike the Firestore scheduler, this one requires *its own* transport: it stores jobs in the queue's
database and dispatches onto the queue inside one transaction, so a run can neither be lost nor
double-enqueued.

Read [scheduler.md](scheduler.md) first for declaring jobs and schedules; this page is the SQL half.

## Install

```sh
dotnet add package Spinneret.Scheduler.Mssql
```

```csharp
services.AddMssqlQueue(configuration, o => o.RequestAssemblies = [typeof(SyncCustomer).Assembly]);
services.AddMssqlQueueWorker();
services.AddMssqlScheduler(configuration);
services.AddSchedulerSweeper();
```

Registration order does not matter.

## Requires

| | |
|---|---|
| **`AddMssqlQueue`** | Not merely any transport — this one. Connection string, schema name and schema creation are all inherited from `Queue:Mssql`. |
| **A trigger** | `AddSchedulerSweeper()` for a timer, or [`Spinneret.Scheduler.Http`](scheduler-http.md) for an endpoint. |
| **A host running `AddMssqlQueueWorker()`** | The sweep only enqueues. |

The dead-letter writer comes from the queue package; nothing extra is needed.

## Configuration — `Scheduler:Mssql`

| Key | Type | Default | Notes |
|---|---|---|---|
| `TableName` | `string` | `"SpinneretScheduledJobs"` | Must be a plain SQL identifier. |

That is the whole surface. Three more settings are read from the queue's own section, bound by
`AddMssqlQueue`:

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
services.AddSchedulerSweeper();

// or, without configuration:
services.AddSchedulerSweeper(o => o.SweepInterval = TimeSpan.FromMinutes(1));
```

See [scheduler.md](scheduler.md#driving-the-sweep).

## Infrastructure

One table, created idempotently at startup when the queue's `CreateSchema` is on, with the same
retry-while-the-database-warms-up behaviour as the queue's initializer:

```sql
[dbo].[SpinneretScheduledJobs]
    JobKey          NVARCHAR(200) PRIMARY KEY
    RequestTypeName NVARCHAR(500)  NOT NULL
    PayloadJson     NVARCHAR(MAX)  NOT NULL
    Status          NVARCHAR(20)   NOT NULL   -- pending | enqueued | failed | cancelled
    Schedule        NVARCHAR(500)  NULL       -- NULL marks a one-shot job
    NextExecuteAt   DATETIME2(3)   NOT NULL
    CreatedAt       DATETIME2(3)   NOT NULL
    LastRunAt       DATETIME2(3)   NULL
  + IX_SpinneretScheduledJobs_Status_NextExecuteAt (Status, NextExecuteAt)
```

Owning the schema yourself means setting `Queue:Mssql:CreateSchema = false` — which turns off the
queue's tables too — and emitting both scripts from your migrations:

```csharp
var queueDdl     = MssqlQueueSchema.CreateScript(queueOptions);
var schedulerDdl = MssqlSchedulerSchema.CreateScript(queueOptions, schedulerOptions);
```

Note the scheduler script takes the queue's options as well, since it inherits the schema name.

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

`ScheduleJobAsync` returns the handle for `CancelJobAsync`. Cancelling is guarded — a job that already
dispatched is left alone rather than resurrected, and cancelling an unknown handle is a silent no-op.

The row is identical to the ones the recurring scheduler writes, so the same sweep picks it up.

Note the recurring API (`RegisterAsync` / `UnregisterAsync`) does **not** join an ambient transaction —
it always manages its own. Only one-shot scheduling is enlistable.

## How the sweep behaves

Each due job is claimed under a row lock and dispatched in its own transaction: the claim, the booking
and the queue insert commit together. Competing hosts skip locked rows (`READPAST`) and dispatch other
due jobs in parallel, so running the sweep on several instances is expected rather than merely
tolerated.

A sweep **drains** — it keeps going until nothing is due — so a backlog clears at full speed rather
than one job per tick. (The Firestore sweep processes one snapshot instead.)

A dispatch failure compensates on a fresh transaction: a one-shot goes terminal-failed with a dead
letter, a recurring job dead-letters that occurrence but keeps its schedule armed.

## Gotchas

- **Same database as the queue.** The one-transaction guarantee is the entire reason this package
  exists; a separate scheduler database would need MSDTC and would lose it.
- **Job keys are compared case-insensitively.** Under the usual SQL Server collation `Sync` and `sync`
  are one row, so a pair that would coexist in Firestore silently collapses here. The installer
  therefore rejects such pairs at startup on *every* provider — a case-sensitive collation does not
  re-enable them.
- **A slow enqueue holds a row lock** for the length of the claim transaction.
- **Compensation can be a no-op** if a competing sweep re-claimed the job; the dead letter is only
  written when the compensating update actually applied.
- **Never hand-delete rows without a `Schedule IS NOT NULL` guard.** One-shot handles live in the same
  `JobKey` namespace and are exactly the rows with no schedule.
- **`Scheduler:Mssql:SweepInterval` no longer exists.** The cadence moved to `Scheduler:Sweeper:SweepInterval`
  along with the trigger. A leftover key binds to nothing and silently does nothing.
