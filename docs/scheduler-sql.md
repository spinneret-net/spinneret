# Spinneret.Scheduler.Sql

Scheduled jobs stored as rows beside the queue's SQL storage, plus the sweep that dispatches them.

Unlike the Firestore scheduler, a SQL scheduler requires the queue's *storage* to be SQL, in the same
database: it dispatches onto the outbox inside one transaction with the job's booking, so a run can
neither be lost nor double-enqueued. The delivery is free — in-host workers or Cloud Tasks.

This package is the database-neutral engine — the sweep, the recurring and one-shot schedulers —
written once against ADO.NET. You install it through the dialect package that matches your queue
storage:

| Dialect | Queue storage | Database |
|---|---|---|
| [`Spinneret.Scheduler.Mssql`](scheduler-mssql.md) | [`Spinneret.Queue.Mssql`](queue-mssql.md) | SQL Server |
| [`Spinneret.Scheduler.Postgres`](scheduler-postgres.md) | [`Spinneret.Queue.Postgres`](queue-postgres.md) | PostgreSQL |

Read [scheduler.md](scheduler.md) first for declaring jobs and schedules; this page is the SQL half,
and the dialect pages cover configuration and the table.

## Install

```csharp
services.AddQueue(q => q
    .AddRequestAssemblies(typeof(SyncCustomer).Assembly)
    .UseMssqlStorage(configuration)                    // or .UsePostgresStorage(configuration)
    .UseInHostDelivery(configuration));                 // or .UseCloudTasksDelivery(configuration)
services.AddScheduler(s => s.UseMssql(configuration)   // or s.UsePostgres(configuration)
    .UseSweeper());
```

`AddQueue` and `AddScheduler` can be called in either order.

## Requires

| | |
|---|---|
| **The matching queue storage** | Not merely any queue — one stored in this database. Connection, schema name and schema creation are all inherited from the queue storage's section. Any other storage, or none, fails the host at startup with the reason. |
| **A trigger** | `s.UseSweeper()` for a timer, or [`Spinneret.Scheduler.AspNetCore`](scheduler-aspnetcore.md) for an endpoint. |
| **A delivery somewhere** | The sweep only enqueues; an in-host worker or Cloud Tasks delivers. |

The dead-letter writer comes from the queue's storage; nothing extra is needed. A dead-letter store
chosen explicitly — `q.UseFirestoreDeadLetters()` — also works, with one caveat under
[how the sweep behaves](#how-the-sweep-behaves).

The sweep cadence is **not** a scheduler-storage setting. `Scheduler:Sweeper:SweepInterval` belongs
to the trigger, and nothing binds it for you — putting it in `appsettings.json` alone changes nothing.
Bind it yourself, or set it in code:

```csharp
services.Configure<SchedulerOptions>(
    configuration.GetSection(SchedulerOptions.SectionName));
services.AddScheduler(s => s.UseMssql(configuration).UseSweeper());

// or, without configuration:
services.AddScheduler(s => s.UseMssql(configuration).UseSweeper(o => o.SweepInterval = TimeSpan.FromMinutes(1)));
```

See [scheduler.md](scheduler.md#driving-the-sweep).

## There is no status column

A row exists only while it is still work to do. A one-shot is deleted by the same transaction that
enqueues it, or — if the dispatch failed — by the one that writes its dead letter; cancelling deletes
it outright. So the table stays proportional to what is scheduled rather than to everything ever
scheduled, and needs no retention job. The record of a run lives in the logs and the queue, not here.

The table is created idempotently at startup when the queue storage's `CreateSchema` is on — one
switch for both packages. Owning the schema yourself means turning it off and emitting both scripts
from your migrations; each dialect page shows how.

## Transactional one-shot jobs

Schedule a job as part of a transaction you already own, so it commits with the change that caused it:

```csharp
await using var tx = await connection.BeginTransactionAsync();
await repository.SaveAsync(employee, tx);
var handle = await scheduler.ScheduleJobAsync(tx, new RemoveEmployee(id), employee.LeavingDate);
await tx.CommitAsync();
```

`ScheduleJobAsync` (on `ISqlTransactionalScheduler`) returns the handle for `CancelJobAsync`, prefixed
`oneshot-`. Cancelling deletes the row: a job that already dispatched is gone, so cancelling it — or
an unknown handle — is a silent no-op. Passing a *recurring* key throws `ArgumentException`; those
share this table, and cancelling deletes, so the prefix is what stops a mixed-up call from destroying
a live schedule. Retire those with `UnregisterAsync`.

The row is identical to the ones the recurring scheduler writes, so the same sweep picks it up.

Note the recurring API (`RegisterAsync` / `UnregisterAsync`) does **not** join an ambient transaction —
it always manages its own. Only one-shot scheduling is enlistable.

## How the sweep behaves

Each due job is claimed under a row lock and dispatched in its own transaction: the claim, the booking
and the outbox insert commit together. Over Cloud Tasks the relay forwards the message as that
transaction commits. Competing hosts skip locked rows and dispatch other due jobs in parallel, so
running the sweep on several instances is expected rather than merely tolerated.

A sweep **drains** — it keeps going until nothing is due — so a backlog clears at full speed rather
than one job per tick. (The Firestore sweep processes one snapshot instead.)

A dispatch failure compensates on a fresh transaction: a one-shot is deleted in the same transaction
that records its dead letter — so the row is only ever removed together with the record that replaces
it, and a failure to write that record rolls both back and leaves the job due. A recurring job
dead-letters that occurrence but keeps its schedule armed.

With dead letters in a store outside the database (`q.UseFirestoreDeadLetters()`), that pairing is
lost: the dead letter is written outside the compensating transaction. It is idempotent by key, so a
retried compensation does not duplicate it, but a compensation that fails after the write leaves a
dead letter for a job that is still due and will run again. Keep the storage's own table when exact
one-shot accounting matters.

A row whose schedule this host cannot parse (written by a newer version, or corrupted) is quarantined
rather than failing the sweep: its run is pushed out and the occurrence dead-lettered, and the row is
left in place for a host that understands it.

## Gotchas

- **Same database as the queue's storage.** The one-transaction guarantee is the entire reason this
  package exists; a separate scheduler database would lose it.
- **A slow enqueue holds a row lock** for the length of the claim transaction.
- **Compensation can be a no-op** if a competing sweep re-claimed the job; the dead letter is only
  written when the compensating statement actually applied.
- **Never hand-delete rows without a `Schedule IS NOT NULL` guard.** One-shot handles live in the same
  `JobKey` namespace and are exactly the rows with no schedule.
- **A missing row is not an error.** Since a finished job leaves nothing behind, "the job is gone" and
  "the job never existed" are the same observation. `RegisterAsync` treats both as "create it".
- **Registration is one statement** on every dialect, so every replica registering its declared jobs
  at startup converges on one row per key.
