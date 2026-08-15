# Spinneret.Queue

The model every transport shares: enqueue a mediator request, have it executed later on a worker,
and decide per command type how hard to try before giving up. You do not install this package
directly — a transport package ([Cloud Tasks](queue-gcp.md), [SQL Server](queue-mssql.md)) brings it
in and registers its half.

Read this page for the parts you write regardless of transport: the retry policy, what a handler
throws, and where failures end up.

## The application owns termination

This is the design decision everything else follows from. The transport does not decide when a task
is finished — the app does. Every delivery ends in an explicit acknowledge, a retry re-enqueued with
a computed backoff, or a dead-letter.

The attempt budget lives on the message (`QueueEnvelope.PriorFailures`), never in the transport's own
retry counter. A transport redelivers only when the app never acknowledged at all — an outage, a
crash, an auth failure before your code ran — and that infrastructure noise must not spend the
handler's attempts.

**The practical consequence:** the transport's retry configuration must be an effectively unlimited
backstop. Tightening it silently overrides every `[QueuePolicy]` you wrote. Each transport page says
what that means concretely.

## Enqueuing

```csharp
public sealed record SyncCustomer(int Id) : IRequest<Unit>;

await queue.Enqueue(new SyncCustomer(42));
```

`IQueue.Enqueue` is fire-and-forget — the handler's response is discarded worker-side. Optional
per-call settings:

| `QueueOptions` | Type | Effect |
|---|---|---|
| `Delay` | `TimeSpan?` | Hold the first dispatch for this long. |
| `DedupeKey` | `string?` | Enqueuing the same key twice yields one delivery; a duplicate is silently skipped, not an error. Constraints and the deduplication window are transport-specific — see below. |
| `Description` | `string?` | Human-readable label carried for observability and shown on dead letters. Never affects dispatch. |

`DedupeKey` is passed straight to the transport, so its rules are the transport's:

| | Cloud Tasks | SQL Server |
|---|---|---|
| Allowed characters | `A-Z a-z 0-9 _ -` only — it becomes the task name | any, stored as `NVARCHAR(200)` |
| Max length | 500 | 200 |
| Window | roughly an hour after the task completes | only while the message is still pending, since the row is deleted on dequeue |

A key outside those rules fails the enqueue rather than silently skipping deduplication.

## Retry policy

Declared on the command type, parsed once at startup — a typo fails the host at boot rather than a
delivery at 2 a.m.

```csharp
[QueuePolicy(Channel = "fortnox", MaxAttempts = 10, MaxBackoff = "00:30:00")]
public sealed record SyncToFortnox(int TenantId) : IRequest<Unit>;
```

| Property | Type | Default | Meaning |
|---|---|---|---|
| `Channel` | `string?` | `null` → `"default"` | Logical name the transport maps to a physical queue — e.g. a rate-limited one. |
| `MaxAttempts` | `int` | `7` | Failed executions before dead-lettering. Deferrals do not count. |
| `MaxAge` | `string` | `"1.00:00:00"` (1 day) | Total lifetime from first enqueue, spanning retries *and* deferrals. |
| `MinBackoff` | `string` | `"00:00:10"` | Delay after the first failure; doubles thereafter. |
| `MaxBackoff` | `string` | `"00:10:00"` | Cap on the doubling. |
| `OnErrorResult` | `ErrorResultAction` | `DeadLetter` | What an error `Result` means: `DeadLetter`, `Retry`, or `Discard`. |
| `OnExhausted` | `ExhaustedAction` | `DeadLetter` | What a spent budget means: `DeadLetter` or `Discard`. |

Durations are invariant-culture `TimeSpan` strings because attributes cannot carry `TimeSpan` values.
`MaxAttempts` must be ≥ 1, every duration must be positive, and `MinBackoff` must not exceed
`MaxBackoff` — all enforced at startup.

With the defaults, backoff runs 10s, 20s, 40s, 80s, 160s, 320s, then caps at 10 minutes — roughly ten
minutes of retrying across seven attempts.

Commands with no attribute get the defaults. `OnExhausted = Discard` is for self-healing work that a
recurring sweep redoes anyway; a permanent failure always dead-letters regardless, because a defect is
worth surfacing even when something else would redo the work.

## What a handler throws

| Situation | Handler does | Result | Spends an attempt? |
|---|---|---|---|
| Success | returns normally | acknowledged | no |
| Wait for something time will fix — a rate limit, a paused integration | `throw new QueueHandlerRetryAfterException(TimeSpan.FromMinutes(10))` | re-enqueued as a fresh task after the delay | **no** — bounded only by `MaxAge` |
| Cannot ever succeed — entity gone, payload unreadable, version mismatch | `throw new QueueHandlerPermanentException("…")` | dead-lettered immediately | n/a |
| Business error as a value | returns a failed `Result` | per `OnErrorResult` — dead-letter by default | only under `Retry` |
| Anything else | throws | retried with backoff, then per `OnExhausted` | yes |

A deferral is a wait, not a failure. That distinction is why `MaxAttempts` and `MaxAge` are separate
knobs: attempts bound how many times work can *fail*, age bounds how long it can stay alive at all.

## Dead letters

When the queue gives up, it writes a `DeadLetterEntry` through `IDeadLetterWriter`:

| Field | Notes |
|---|---|
| `IdempotencyKey` | Transport task id — stable across redeliveries, so a retried dead-letter write cannot duplicate. |
| `Source` | `Queue` or `Scheduler`. Member names are persisted; they are a data contract. |
| `CommandTypeName`, `PayloadJson`, `Error`, `Attempts` | |
| `Description` | From `QueueOptions.Description`, when supplied. |

Which package supplies the writer depends on your transport: SQL Server
[ships one](queue-mssql.md), Cloud Tasks does not — pair it with
[`Spinneret.Queue.Firestore`](queue-firestore.md) or write your own.

**Dead-lettering never drops work.** If the dead-letter store is unreachable the delivery is retried
rather than acknowledged, logged at Critical with the full payload. Its availability is on the
critical path.

## What you must register

Whichever transport you choose, the host supplies:

- **`ISpinneretMediator` and the handlers** — `services.AddMediator(assemblies)`.
- **The assemblies containing your `IRequest<>` command types**, passed to the transport's `Add*`
  call. They are indexed by `Type.FullName`, which is the name on the wire — so renaming or moving a
  queued command type is a breaking change for messages already in flight.
- **`IDeadLetterWriter`**, unless your transport ships one.

Two commands implementing `IRequest<>` twice, or two types with the same full name across the scanned
assemblies, fail at startup.

## Replacing a default

Defaults are registered with `TryAdd`, so your own implementation always wins: registered **before**
the transport's `Add*` call, the default is never added at all; registered **after**, yours shadows
it, because the container resolves the last registration for a service type.

Either order works. Registering first is the clearer habit — the container then holds exactly one
registration — and it is required for the one seam that is inspected rather than `TryAdd`ed, the
MSSQL transport's `IQueueDispatchBoundary`.

That applies to `IQueuePayloadSerializer`, `IDeadLetterWriter`, and the transport-specific seams each
page lists.
