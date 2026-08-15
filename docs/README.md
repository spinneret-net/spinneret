# Queue and scheduler setup

Spinneret's queue and scheduler are assembled from small packages, one per decision. Pick along four
axes; each choice is one registration call, and the calls can be written in any order.

| Axis | Choices |
|---|---|
| **Transport** — how queued work is delivered | [`Spinneret.Queue.Gcp`](queue-gcp.md) (Cloud Tasks) · [`Spinneret.Queue.Mssql`](queue-mssql.md) (SQL Server) |
| **Dead letters** — where given-up work lands | built into `Spinneret.Queue.Mssql` · [`Spinneret.Queue.Firestore`](queue-firestore.md) |
| **Schedule storage** — where job definitions live | [`Spinneret.Scheduler.Firestore`](scheduler-firestore.md) · [`Spinneret.Scheduler.Mssql`](scheduler-mssql.md) |
| **Trigger** — what decides when to sweep | `AddSchedulerSweeper()` from [`Spinneret.Scheduler`](scheduler.md) (timer) · [`Spinneret.Scheduler.Http`](scheduler-http.md) (endpoint + external cron) |

The axes are mostly independent. SQL Server happens to be transport *and* storage — the queue table is
the store — but Cloud Tasks stores nothing you can query, which is why its dead letters and schedules
need a store of their own.

One exception worth knowing before you plan a combination: **`Spinneret.Scheduler.Mssql` requires the
SQL Server queue specifically**, because it stores jobs in that queue's database and dispatches onto
it inside a single transaction. Everything else composes freely — `Spinneret.Scheduler.Firestore` and
`Spinneret.Queue.Firestore` depend only on the provider-agnostic core, so they sit behind any
transport.

## Typical combinations

**Cloud Run, all GCP.** Scales to zero, so the trigger is an external cron rather than a timer.

```csharp
services.AddMediator(typeof(SomeCommand).Assembly);           // handlers
services.AddSingleton(_ => new FirestoreDbBuilder { ProjectId = "…" }.Build());
services.AddGcpQueue(config, typeof(SomeCommand).Assembly);   // Cloud Tasks
services.AddFirestoreDeadLetters(config);                     // dead letters
services.AddFirestoreScheduler(config);                       // schedules

app.UseAuthentication();
app.UseAuthorization();
app.MapGcpQueueDispatch();                                    // receives tasks
app.MapSchedulerSweep(OidcAuthSetup.PolicyName);              // Cloud Scheduler calls this
```

Read: [queue.md](queue.md) · [queue-gcp.md](queue-gcp.md) · [queue-firestore.md](queue-firestore.md) ·
[scheduler.md](scheduler.md) · [scheduler-firestore.md](scheduler-firestore.md) ·
[scheduler-http.md](scheduler-http.md)

**A single always-on service, all SQL Server.** One database, one process, a timer inside it.

```csharp
services.AddMediator(typeof(SomeCommand).Assembly);   // handlers
services.AddMssqlQueue(config, typeof(SomeCommand).Assembly);
services.AddMssqlQueueWorker();                       // this host consumes
services.AddMssqlScheduler(config);
services.AddSchedulerSweeper();                       // timer
```

Read: [queue.md](queue.md) · [queue-mssql.md](queue-mssql.md) · [scheduler.md](scheduler.md) ·
[scheduler-mssql.md](scheduler-mssql.md)

**Mixed.** Nothing forces one vendor. Firestore schedules dispatched onto a SQL Server queue is a
supported combination, because `Spinneret.Scheduler.Firestore` depends on the provider-agnostic core
queue rather than on any transport:

```csharp
services.AddSingleton(_ => new FirestoreDbBuilder { ProjectId = "…" }.Build());
services.AddMssqlQueue(config, typeof(SomeCommand).Assembly);
services.AddMssqlQueueWorker();
services.AddFirestoreScheduler(config);
services.AddSchedulerSweeper();
```

The `FirestoreDb` registration is easy to forget here — the SQL packages need nothing like it, and
without it the host fails at first resolve.

Read: [queue.md](queue.md) · [queue-mssql.md](queue-mssql.md) · [scheduler.md](scheduler.md) ·
[scheduler-firestore.md](scheduler-firestore.md)

## Reference

| Page | Package |
|---|---|
| [queue.md](queue.md) | `Spinneret.Queue` — the model every transport shares: `IQueue`, `[QueuePolicy]`, dead-lettering |
| [queue-gcp.md](queue-gcp.md) | `Spinneret.Queue.Gcp` — Cloud Tasks, OIDC dispatch, IAM, emulator |
| [queue-mssql.md](queue-mssql.md) | `Spinneret.Queue.Mssql` — transactional enqueue, polling workers, schema |
| [queue-firestore.md](queue-firestore.md) | `Spinneret.Queue.Firestore` — Firestore dead-letter store |
| [scheduler.md](scheduler.md) | `Spinneret.Scheduler` — declaring jobs, cron schedules, the timer trigger |
| [scheduler-firestore.md](scheduler-firestore.md) | `Spinneret.Scheduler.Firestore` — schedules in Firestore |
| [scheduler-mssql.md](scheduler-mssql.md) | `Spinneret.Scheduler.Mssql` — schedules in SQL Server |
| [scheduler-http.md](scheduler-http.md) | `Spinneret.Scheduler.Http` — sweep endpoint for externally-clocked hosts |

Every page follows the same shape: what it is, install, what it requires, configuration,
infrastructure, and gotchas. Read only the pages for the packages you picked.
