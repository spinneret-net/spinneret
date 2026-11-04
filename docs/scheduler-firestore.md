# Spinneret.Scheduler.Firestore

Scheduled jobs stored as Firestore documents, plus the sweep that dispatches them onto the queue.

It depends only on the provider-agnostic core queue, so it composes with **any** queue — Firestore
schedules dispatched onto a SQL Server or PostgreSQL outbox is a supported combination. It is a plain library with no
web dependency; the HTTP trigger lives in [`Spinneret.Scheduler.AspNetCore`](scheduler-aspnetcore.md).

Read [scheduler.md](scheduler.md) first for declaring jobs and schedules; this page is the Firestore
half.

## Install

```sh
dotnet add package Spinneret.Scheduler.Firestore
```

```csharp
services.AddQueue(q => q
    .AddRequestAssemblies(typeof(SyncCustomer).Assembly)
    .UseCloudTasksDelivery(configuration)
    .UseFirestoreDeadLetters(configuration));

// then pick a trigger — one of:
services.AddScheduler(s => s.UseFirestoreStorage(configuration).UseInHostSweeper());   // timer, for an always-on host
services.AddScheduler(s => s.UseFirestoreStorage(configuration));                      // plus, for a host that scales to zero:
app.MapSchedulerSweep(CloudTasksAuth.PolicyName);                                      // an endpoint Cloud Scheduler calls
```

`AddQueue` and `AddScheduler` can be called in either order.

## Requires

| | |
|---|---|
| **A queue** | Any composition. The sweep enqueues through `IQueue` and needs the type registry and payload serializer. |
| **A host-registered `FirestoreDb`** | See [queue-firestore.md](queue-firestore.md#requires) for the builder snippet. |
| **An `IDeadLetterWriter`** | A job whose request can no longer be rebuilt is dead-lettered. `q.UseFirestoreDeadLetters()` is the natural pairing; a SQL storage's table works too. |
| **A trigger** | Nothing dispatches without one. |

## Configuration — `Scheduler:Firestore`

| Key | Type | Default | Notes |
|---|---|---|---|
| `Collection` | `string` | `"scheduled_jobs"` | Collection holding job documents. |
| `OneShotLeaseWindow` | `TimeSpan` | `00:05:00` | How long a one-shot job is hidden once a sweep leases it. |
| `EnqueueRetryWindow` | `TimeSpan` | `01:00:00` | How long a job whose enqueue keeps failing is retried before it is dead-lettered. |

`Collection` must be set and both windows positive; validated at startup.

Keep `EnqueueRetryWindow` within the transport's dedupe window — about an hour on Cloud Tasks. Past it,
a retry of an enqueue that landed unseen is delivered a second time.

`OneShotLeaseWindow` is a crash-recovery window, not a timeout. If a dispatcher dies mid-dispatch the
lease lapses and a later sweep retries the job, so a one-shot can never get permanently stuck. It must
comfortably exceed the time to enqueue a single job.

## Infrastructure

**None.** The sweep queries on `nextExecuteAt` alone, which Firestore serves from an automatic
single-field index — there is no composite index to provision and nothing to create by hand.
Documents are created by the installer at startup.

## Document shape

One document per job, keyed by the job's `Key` (or an `oneshot-`-prefixed id for one-shots).

| Field | Notes |
|---|---|
| `requestTypeName`, `payloadJson` | The request to enqueue. |
| `schedule` | Canonical `cron:<zone>:<expression>`. **Absent on one-shot jobs** — that absence is what distinguishes them. |
| `nextExecuteAt` | When the job is next due. Doubles as the lease. |
| `createdAt`, `lastRunAt` | `lastRunAt` is only meaningful on a recurring job; a one-shot is deleted by the run that would set it. |
| `dispatchFailures`, `firstFailedAt` | Consecutive failed enqueues, and when the first failed. Present only while the job waits to retry one. |
| `pendingDedupeKey` | Recurring only, beside `dispatchFailures`: the dedupe key the retry must reuse. |

### There is no status field

A document exists only while it is still work to do. A one-shot is deleted the moment it is enqueued,
or — if it is dead-lettered — in the step after its payload reaches the dead-letter store; cancelling deletes it
outright. So the collection stays proportional to what is scheduled rather than to everything ever
scheduled, and needs no TTL policy or retention job.

Two consequences worth knowing:

- **The record of a run lives in the logs and the queue, not here.** If you want to answer "did this
  job run", look at the `Scheduled job {JobId} enqueued` log line or the dead-letter collection.
- **A dead-lettered one-shot whose dead-letter write fails keeps its document**, deliberately: it is then
  the only copy of the payload. The lease lapses and a later sweep retries, which also recovers the
  job outright when the original failure was transient.

## Transactional one-shot jobs

Schedule a job as part of a transaction you already own, so it commits with the change that caused it
— scheduling an employee's removal in the same transaction that records the termination:

```csharp
await db.RunTransactionAsync(async tx =>
{
    tx.Set(employeeRef, employee);
    var handle = scheduler.ScheduleJob(tx, new RemoveEmployee(id), employee.LeavingDate);
    tx.Update(employeeRef, new Dictionary<string, object> { ["removalHandle"] = handle });
});
```

`ScheduleJob` returns the handle for `CancelJob`. The document is identical to the ones the recurring
scheduler writes, so the same sweep picks it up.

The API is synchronous because Firestore buffers writes client-side and flushes them at commit — the
write has not happened yet when the method returns.

## Gotchas

- **`CancelJob` deletes, and only accepts a handle it issued.** Cancelling a job that already ran, or
  an unknown handle, is a silent no-op — a delete needs no read and does not mind an absent document.
  Passing a *recurring* key throws `ArgumentException`: those share this collection, and cancelling
  now deletes, so the guard is what stops a mixed-up call from destroying a live schedule. Retire
  those with `UnregisterAsync`. The check is on the handle's `oneshot-` prefix rather than on the
  stored document because Firestore requires every read in a transaction to precede every write,
  which would be impossible in a transaction the caller may already have written to.
- **A sweep covers one query snapshot.** Anything falling due mid-pass waits for the next sweep, so the
  trigger interval bounds how late a job can run. (The SQL sweep drains instead.)
- **A crash mid-dispatch is not free, and it cuts differently per job kind.** Firestore and the queue
  are separate systems, so the lease always commits before the enqueue.
  - **One-shot jobs are at-least-once.** The lease hides the job for `OneShotLeaseWindow`; a crash
    after the enqueue but before the document is deleted leaves it in place, and a later sweep runs
    it again. Make these commands idempotent.
  - **Recurring occurrences can be lost.** The lease advances `nextExecuteAt` to the *next cron slot*,
    so a crash between lease and enqueue drops that occurrence silently — the job simply runs next
    time. Do not use a recurring job where every single occurrence must happen; use it for work that
    is periodic and self-correcting.

  Every enqueue carries a `DedupeKey` naming its run — the one-shot's handle, or the recurring job's
  key and slot — so an enqueue that landed but was not seen to (a crash, a timeout) is a duplicate
  when it is sent again, within the transport's dedupe window.

  The SQL schedulers differ here: its claim and enqueue share one transaction, so an
  occurrence can be neither lost nor double-enqueued. That is the trade for requiring the queue to
  live in the same database.
- **A failed enqueue is retried for `EnqueueRetryWindow`, then dead-lettered.** The failure is usually
  the transport's — a timeout, a queue briefly not found — so the job stays due: retried from 30
  seconds, doubling to a 5-minute ceiling (and never faster than the sweep runs), logged as a warning
  and as an error from the fifth consecutive failure on. A recurring job's slots missed meanwhile
  coalesce into the one run that lands — every occurrence carries the same request. The retry reuses
  the failed attempt's dedupe key, because a timed-out enqueue may have landed. A failure that outlasts
  the window (a deleted queue, a payload the transport refuses) is dead-lettered: a one-shot's document
  is then removed, and a recurring job resumes at its next slot and opens a new window.
- **A request that cannot be rebuilt is dead-lettered at once** — its type no longer registered, or its
  payload no longer deserializing. Retrying would never help. A recurring job is dead-lettered per
  occurrence and keeps its schedule: recurrence belongs to the job, not to any single run.
- **An unreadable schedule is quarantined, not dropped.** Pushed out five minutes and dead-lettered per
  occurrence, and kept so a host version that understands it can still pick it up. This is what a
  rollback past a schedule-format change looks like.
- **One-shot handles share the job-key namespace**, which is why unregistering a recurring job will not
  touch a one-shot, and why cancelling rejects anything without the `oneshot-` prefix.
