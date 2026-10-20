# Spinneret.Scheduler

Recurring jobs declared in code, installed idempotently at startup, and dispatched onto the queue as
they fall due. Adding a scheduled job is implementing an interface and registering it — no
infrastructure change, because every job rides one sweep.

You do not install this directly: a storage package ([Firestore](scheduler-firestore.md),
[SQL Server](scheduler-mssql.md), [PostgreSQL](scheduler-postgres.md)) brings it in. This page covers what you write regardless of where
the jobs are stored, plus the timer that drives them.

## Three separate decisions

| Decision | Where |
|---|---|
| What the jobs are | this package — `IRecurringJob`, `AddRecurringJob` |
| Where they live | a storage, chosen in `AddScheduler(s => s.UseMssql(...))`, `s.UsePostgres(...)` or `s.UseFirestore(...)` |
| What decides when to sweep | `s.UseSweeper()` here, or [`Spinneret.Scheduler.AspNetCore`](scheduler-aspnetcore.md) |

```csharp
services.AddScheduler(s => s.UseFirestore(configuration).UseSweeper());
```

`AddScheduler` checks the composition when it returns — no storage, or two, fails there — and adds
the installer that asserts your declared jobs at startup. `AddQueue` and `AddScheduler` can be called
in either order.

## Declaring a job

Inline, when the job is just "enqueue this on this schedule":

```csharp
services.AddRecurringJob(
    "fortnox-sync-all",
    Schedule.Cron("0 3 * * *", "Europe/Stockholm"),
    () => new SyncAllTenantsToFortnox());
```

Or as a class, when the schedule comes from configuration or the request needs building:

```csharp
public sealed class MonthCloseJob(IConfiguration config) : IRecurringJob<Unit>
{
    public string Key => "month-close";
    public Schedule Schedule => Schedule.Parse(config["Jobs:MonthClose"]!);
    public IRequest<Unit> CreateRequest() => new CloseMonth();
}

services.AddSingleton<IRecurringJob, MonthCloseJob>();
```

The type parameter is the request's response type, and any of them may be scheduled — a run is
enqueued and executed on a worker, which discards whatever the handler returns. Use `Unit` when the
handler returns nothing. Register the job in DI as the non-generic `IRecurringJob`: that is what the
installer collects, so jobs whose requests return different types still install together.

`Key` is the stable identity and the storage key, so re-installing upserts rather than duplicating.
Registration is idempotent: every instance asserting the same job on every deploy converges on one
definition, and re-registering an unchanged job does **not** disturb its next run — only a changed
schedule re-arms it. Frequent restarts therefore cannot push a job's next run forever into the future.

## Retiring a job

Deleting the code is not enough. The stored definition outlives it and keeps dispatching, with
nothing in the codebase to explain it:

```csharp
services.RetireRecurringJob("old-nightly-export");
```

Leave the retirement in place for **at least one full deploy**. During a rolling deploy the old
instances still declare the job and re-install it, so the two fight until the last old instance is
gone — after which the retirement wins for good. Removing it in the same release that deletes the job
is what strands the definition.

Declaring and retiring the same key fails startup, as does declaring two jobs under one key. Both are
code bugs that would otherwise be silent — the loser of a duplicated key is simply overwritten and
never runs.

## Schedules

```csharp
Schedule.Cron("0 3 * * *", "Europe/Stockholm");     // 03:00 local, every day
Schedule.Cron("*/30 * * * * *", "Etc/UTC");         // every 30 seconds (6 fields)
Schedule.Parse("cron:Europe/Stockholm:0 3 * * *");  // from configuration or storage
```

- **Five fields** — minute, hour, day, month, day-of-week. **Six** when the first is seconds. Any
  other count is rejected.
- **The zone must be an IANA id** (`Europe/Stockholm`), not a Windows one. Only the id is persisted
  and it is rehydrated on whichever host sweeps, so a Windows id would be unreadable to a Linux
  sweep. `TimeZoneInfo.TryConvertWindowsIdToIanaId` converts.
- **Cron runs in that zone**, so a schedule keeps its wall-clock slot across DST transitions.
- **Canonical form is `cron:<zone>:<expression>`** — what `ToString()` produces, what storage holds,
  and what `Parse` accepts. Expressions are normalized to single spaces and upper case, so whitespace
  and `mon` vs `MON` do not read as a change.
- An expression that can never occur (`0 0 31 2 *`) is rejected where you write it, not at dispatch.

**Host requirement:** resolving IANA ids needs ICU. `InvariantGlobalization=true` breaks it.

A slot finer than the sweep interval is reached on the following sweep, not at the slot itself.

## Driving the sweep

Nothing dispatches until something drives it. A scheduler with no trigger is a silent no-op, and a
trigger with nothing to sweep is a wiring mistake, so `AddScheduler` refuses `UseSweeper()` without a
storage.

A pass returns a `SweepResult` reporting how many jobs it enqueued. The timer logs it; the HTTP
trigger returns it in the response body, so an external cron can see whether a tick did any work.

```csharp
services.AddScheduler(s => s.UseMssql(configuration).UseSweeper());                     // default 15s
services.AddScheduler(s => s.UseMssql(configuration)
    .UseSweeper(o => o.SweepInterval = TimeSpan.FromMinutes(1)));
```

| Option | Config key | Type | Default |
|---|---|---|---|
| `SweepInterval` | `Scheduler:Sweeper:SweepInterval` | `TimeSpan` | 15 seconds |

This package deliberately takes no dependency on the configuration binder, so binding from config is
the host's call:

```csharp
services.Configure<SchedulerOptions>(configuration.GetSection(SchedulerOptions.SectionName));
services.AddScheduler(s => s.UseMssql(configuration).UseSweeper());
```

Sweeps race safely across hosts, so every instance may run its own. This loop never overlaps its own
sweeps either — but do not build on that as a guarantee: if you also map the HTTP trigger, a request
can start a sweep while a tick is running.

> **The interval is also the failure backoff**, not just a cadence. A provider whose sweep cannot make
> progress returns rather than spinning, and this delay is the only thing between that and a hot loop.
> A very low value costs more than wasted queries. It is read once at startup.

For a host that scales to zero and has no thread to tick, use
[`Spinneret.Scheduler.AspNetCore`](scheduler-aspnetcore.md) and an external cron instead. Same sweep, different
clock.

## Tracing

Spans are emitted on the `Spinneret.Scheduler` source
(`SchedulerDiagnostics.ActivitySourceName`): one `scheduler sweep` per pass, tagged
`scheduler.jobs_dispatched`, and one `scheduler job` per *claimed* job — a pass that finds nothing
due emits only the sweep span. Each job span carries `scheduler.job_id`, `scheduler.job_kind`
(`recurring` or `oneshot`) and `scheduler.outcome` (`enqueued`, `skipped` or `quarantined`).

**A job runs in the sweep's trace, never the one that scheduled it.** For a recurring job the
"scheduling trace" is a host bootstrap that may have happened a year ago, and parenting every
occurrence onto it would build one endless trace. A one-shot can fire months after it was booked, and
a parent that far back describes an operation nothing performed — it is also long past any tracing
backend's retention.

The join back to the scheduling side is the job key, which the sweep logs. The job is then enqueued
through the queue, so from that point on it carries [the queue's trace context](queue.md#tracing)
normally.

If you ever do want the provenance, the right primitive is an `ActivityLink` to the scheduling
context, not a parent.

## What you must register

- **A storage** — `s.UseFirestore(...)`, `s.UseMssql(...)` or `s.UsePostgres(...)`, in `AddScheduler`.
- **A queue** (`AddQueue`), since the sweep dispatches onto it. A SQL scheduler needs the queue's
  storage in the same database — `s.UseMssql` beside `q.UseMssqlStorage`, `s.UsePostgres` beside
  `q.UsePostgresStorage` — and fails the host at startup otherwise.
- **A trigger** — the sweeper above, or the HTTP endpoint.
- **Your jobs**, and any retirements.
- **A host that consumes the queue.** The sweep only *enqueues*; without a consumer, jobs pile up
  having apparently run.

## Installation behaviour

Job installation happens in the background and retries with capped, jittered backoff — 5s doubling to
a 5-minute ceiling, escalating from warning to error on the fifth consecutive failure. A store that is
briefly unreachable at startup costs a short delay rather than a missing job.

It is a retry, not a reconciliation loop: each job stops being retried the moment it installs.
Re-asserting on a timer would make two revisions overwrite each other's definitions for the length of
every rolling deploy.

Validation is on the startup path — duplicate and contested keys stop the deploy — while retrying is
deliberately off it, so an unreachable store delays jobs instead of holding the application down.
