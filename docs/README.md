# Queue and scheduler setup

Spinneret's queue and scheduler are assembled from small packages, one per decision. The queue is
composed in one call, `AddQueue`, from up to three choices; the scheduler in another, `AddScheduler`,
from two. The two calls can be written in either order.

| Decision | Choices |
|---|---|
| **Queue storage** — where messages wait | [`Spinneret.Queue.Mssql`](queue-mssql.md) (SQL Server) · [`Spinneret.Queue.Postgres`](queue-postgres.md) (PostgreSQL) — both via [`Spinneret.Queue.Sql`](queue-sql.md) · none |
| **Queue delivery** — how messages reach handlers | `UseInHostDelivery()` ([in-host workers](queue-sql.md#in-host-delivery)) · [`Spinneret.Queue.CloudTasks`](queue-cloudtasks.md) · none, on a producer-only host |
| **Dead letters** — where given-up work lands | the SQL storage's own table · [`Spinneret.Queue.Firestore`](queue-firestore.md) |
| **Schedule storage** — where job definitions live | [`Spinneret.Scheduler.Mssql`](scheduler-mssql.md) · [`Spinneret.Scheduler.Postgres`](scheduler-postgres.md) — both via [`Spinneret.Scheduler.Sql`](scheduler-sql.md) · [`Spinneret.Scheduler.Firestore`](scheduler-firestore.md) |
| **Sweep trigger** — what decides when to sweep | `UseInHostSweeper()` from [`Spinneret.Scheduler`](scheduler.md) (timer) · [`Spinneret.Scheduler.AspNetCore`](scheduler-aspnetcore.md) (endpoint + external cron) |

What a SQL storage buys is atomicity: with one, every enqueue commits with the business write that
caused it, and every delivery's writes, cascades and outcome commit together — whether in-host
workers or Cloud Tasks deliver it. Without one, Cloud Tasks is called directly and nothing spans the
two systems. See [the guarantees](queue-sql.md#guarantees).

The combinations that do not hold together fail at `AddQueue` or `AddScheduler` — or, where the two
calls must first both be in, as the host starts — with the reason:

- **In-host delivery needs a SQL storage** — the workers drain its table.
- **One storage, one delivery.** Choosing two of either fails, naming both.
- **A SQL scheduler needs the queue's storage in the same database** — `Scheduler.Mssql` beside
  `Queue.Mssql`, `Scheduler.Postgres` beside `Queue.Postgres` — because a due job's booking and its
  enqueue commit in one transaction. Since `AddQueue` and `AddScheduler` can come in either order,
  this one is checked as the host starts. `Spinneret.Scheduler.Firestore` composes with any queue.

## Typical combinations

**Cloud Run, no SQL.** Scales to zero, so the sweep trigger is an external cron rather than a timer.

```csharp
services.AddMediator([typeof(SomeCommand).Assembly]);           // handlers
services.AddSingleton(_ => new FirestoreDbBuilder { ProjectId = "…" }.Build());
services.AddQueue(q => q
    .AddRequestAssemblies(typeof(SomeCommand).Assembly)
    .UseCloudTasksDelivery(config)                              // Cloud Tasks, called directly
    .UseFirestoreDeadLetters(config));                          // dead letters
services.AddScheduler(s => s.UseFirestoreStorage(config));             // schedules

app.UseAuthentication();
app.UseAuthorization();
app.MapCloudTasksDispatch();                                    // receives tasks
app.MapSchedulerSweep(CloudTasksAuth.PolicyName);               // Cloud Scheduler calls this
```

Read: [queue.md](queue.md) · [queue-cloudtasks.md](queue-cloudtasks.md) ·
[queue-firestore.md](queue-firestore.md) · [scheduler.md](scheduler.md) ·
[scheduler-firestore.md](scheduler-firestore.md) · [scheduler-aspnetcore.md](scheduler-aspnetcore.md)

**Cloud Run over SQL Server.** Cloud Tasks delivers, but the outbox and the schedules live in the
application's database, so enqueues are transactional and deliveries atomic. The host is
CPU-throttled outside requests, so Cloud Scheduler drives both the relay and the sweep.

```csharp
services.AddMediator([typeof(SomeCommand).Assembly]);
services.AddQueue(q => q
    .AddRequestAssemblies(typeof(SomeCommand).Assembly)
    .UseMssqlStorage(config)                                    // the outbox
    .UseCloudTasksDelivery(config, o => o.Relay.RunInBackground = false));
services.AddScheduler(s => s.UseMssqlStorage(config));

app.UseAuthentication();
app.UseAuthorization();
app.MapCloudTasksDispatch();
app.MapQueueRelay(CloudTasksAuth.PolicyName);                   // Cloud Scheduler calls these two
app.MapSchedulerSweep(CloudTasksAuth.PolicyName);
```

Messages committed by a delivery, a sweep or an enqueue outside a transaction are forwarded to Cloud
Tasks as they commit; the relay endpoint picks up the ones committed in your own transactions.

Read: [queue.md](queue.md) · [queue-sql.md](queue-sql.md) · [queue-mssql.md](queue-mssql.md) ·
[queue-cloudtasks.md](queue-cloudtasks.md#over-a-sql-storage) · [scheduler-mssql.md](scheduler-mssql.md) ·
[scheduler-aspnetcore.md](scheduler-aspnetcore.md)

**On PostgreSQL.** Any of the SQL Server recipes above, with the Postgres packages instead — the
composition and the guarantees are the same. Sharing the application's own `NpgsqlDataSource` keeps
the queue on the same pool as the rest of your data access:

```csharp
services.AddNpgsqlDataSource(config.GetConnectionString("App")!);
services.AddMediator([typeof(SomeCommand).Assembly]);
services.AddQueue(q => q
    .AddRequestAssemblies(typeof(SomeCommand).Assembly)
    .UsePostgresStorage(sp => sp.GetRequiredService<NpgsqlDataSource>(), config)
    .UseInHostDelivery(config));                                // or .UseCloudTasksDelivery(config)
services.AddScheduler(s => s.UsePostgresStorage(config).UseInHostSweeper());
```

Read: [queue-sql.md](queue-sql.md) · [queue-postgres.md](queue-postgres.md) ·
[scheduler-sql.md](scheduler-sql.md) · [scheduler-postgres.md](scheduler-postgres.md)

**A single always-on service on SQL Server.** One database, one process, timers inside it.

```csharp
services.AddMediator([typeof(SomeCommand).Assembly]);
services.AddQueue(q => q
    .AddRequestAssemblies(typeof(SomeCommand).Assembly)
    .UseMssqlStorage(config)
    .UseInHostDelivery(config));                                // this host consumes
services.AddScheduler(s => s.UseMssqlStorage(config).UseInHostSweeper());    // timer
```

A producer-only host — a public API in front of this worker — composes the same storage without
`UseInHostDelivery`.

Read: [queue.md](queue.md) · [queue-sql.md](queue-sql.md) · [queue-mssql.md](queue-mssql.md) ·
[scheduler.md](scheduler.md) · [scheduler-mssql.md](scheduler-mssql.md)

**Mixed.** Nothing forces one vendor. Firestore schedules dispatched onto a SQL Server outbox is a
supported combination, because `Spinneret.Scheduler.Firestore` depends only on the core queue:

```csharp
services.AddSingleton(_ => new FirestoreDbBuilder { ProjectId = "…" }.Build());
services.AddQueue(q => q
    .AddRequestAssemblies(typeof(SomeCommand).Assembly)
    .UseMssqlStorage(config)
    .UseInHostDelivery(config));
services.AddScheduler(s => s.UseFirestoreStorage(config).UseInHostSweeper());
```

The `FirestoreDb` registration is easy to forget here — the SQL packages need nothing like it, and
without it the host fails at first resolve.

Read: [queue.md](queue.md) · [queue-sql.md](queue-sql.md) · [scheduler.md](scheduler.md) ·
[scheduler-firestore.md](scheduler-firestore.md)

## Reference

| Page | Package |
|---|---|
| [queue.md](queue.md) | `Spinneret.Queue` — the model every queue shares: `IQueue`, `[QueuePolicy]`, dead-lettering, `AddQueue` |
| [queue-sql.md](queue-sql.md) | `Spinneret.Queue.Sql` — the outbox, transactional enqueue, units of work, in-host delivery, guarantees |
| [queue-mssql.md](queue-mssql.md) | `Spinneret.Queue.Mssql` — SQL Server storage: configuration, schema, permissions |
| [queue-postgres.md](queue-postgres.md) | `Spinneret.Queue.Postgres` — PostgreSQL storage: configuration, schema, permissions, pooling |
| [queue-cloudtasks.md](queue-cloudtasks.md) | `Spinneret.Queue.CloudTasks` — Cloud Tasks delivery, OIDC dispatch, the relay, IAM, emulator |
| [queue-firestore.md](queue-firestore.md) | `Spinneret.Queue.Firestore` — Firestore dead-letter store |
| [scheduler.md](scheduler.md) | `Spinneret.Scheduler` — declaring jobs, cron schedules, `AddScheduler`, the timer trigger |
| [scheduler-firestore.md](scheduler-firestore.md) | `Spinneret.Scheduler.Firestore` — schedules in Firestore |
| [scheduler-sql.md](scheduler-sql.md) | `Spinneret.Scheduler.Sql` — the SQL scheduler engine: the sweep, transactional one-shots, guarantees |
| [scheduler-mssql.md](scheduler-mssql.md) | `Spinneret.Scheduler.Mssql` — schedules in SQL Server |
| [scheduler-postgres.md](scheduler-postgres.md) | `Spinneret.Scheduler.Postgres` — schedules in PostgreSQL |
| [scheduler-aspnetcore.md](scheduler-aspnetcore.md) | `Spinneret.Scheduler.AspNetCore` — sweep endpoint for externally-clocked hosts |

Every page follows the same shape: what it is, install, what it requires, configuration,
infrastructure, and gotchas. Read only the pages for the packages you picked.
