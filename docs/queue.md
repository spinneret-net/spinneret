# Spinneret.Queue

The model every queue shares: enqueue a mediator request, have it executed later on a worker, and
decide per command type how hard to try before giving up. You do not install this package directly —
the storage and delivery packages you compose with `AddQueue` ([SQL](queue-sql.md),
[Cloud Tasks](queue-cloudtasks.md)) bring it in.

Read this page for the parts you write regardless of composition: the retry policy, what a handler
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

`DedupeKey` is enforced where the message waits, so its rules are that place's:

| | Cloud Tasks alone | SQL storage, in-host | SQL storage over Cloud Tasks |
|---|---|---|---|
| Allowed characters | `A-Z a-z 0-9 _ -` only — it becomes the task name | any, stored as `NVARCHAR(200)` | `A-Z a-z 0-9 _ -` — it becomes the task name |
| Max length | 500 | 200 | 200 |
| Window | roughly an hour after the task completes | only while the message is still pending, since the row is deleted on dequeue | while pending in the outbox, and then roughly an hour after the task completes |

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
| `IdempotencyKey` | The message's task id — stable across redeliveries, so a retried dead-letter write cannot duplicate. |
| `Source` | `Queue` or `Scheduler`. Member names are persisted; they are a data contract. |
| `CommandTypeName`, `PayloadJson`, `Error`, `Attempts` | |
| `Description` | From `QueueOptions.Description`, when supplied. |
| `TraceId` | The failed execution's 32-hex trace id — paste it into your log query to reach the request that caused this. See [Tracing](#tracing). |

Which package supplies the writer depends on your composition: a [SQL storage](queue-sql.md) has
its own table; Cloud Tasks on its own has none — pair it with
[`Spinneret.Queue.Firestore`](queue-firestore.md) or write your own.

**A delivery naming a type the consumer does not know is redelivered for 15 minutes, then
dead-lettered.** During a rolling deploy a new instance can enqueue a command an old instance
receives; once the rollout finishes the redelivery lands. A type renamed or removed across a deploy
never resolves, and dead-letters when the window closes. The window is measured from the original
enqueue, so a task delayed past it dead-letters on its first unknown delivery.

**Dead-lettering never drops work.** If the dead-letter store is unreachable the delivery is retried
rather than acknowledged, logged at Critical with the full payload. Its availability is on the
critical path.

## The dead-letter page

Writing is only half of it. `IDeadLetterStore` reads the entries back and removes them, and
`IDeadLetterResender` puts one back on the queue — between them, everything an admin page needs:

```csharp
public sealed class DeadLetterEndpoints(IDeadLetterStore store, IDeadLetterResender resender)
{
    public Task<DeadLetterPage> List(string? cursor) =>
        store.ListAsync(new DeadLetterQuery { PageSize = 50, Cursor = cursor });

    public async Task<IResult> Resend(string key, string? correctedPayload) =>
        (await resender.ResendAsync(key, correctedPayload)).Match(
            () => Results.NoContent(),
            error => error switch
            {
                ResendDeadLetterError.NotFound => Results.NotFound(),
                ResendDeadLetterError.UnknownCommandType e => Results.Conflict(e.CommandTypeName),
                ResendDeadLetterError.InvalidPayload e => Results.BadRequest(e.Message),
            });
}
```

The store comes from whichever holds your dead letters — a [SQL storage](queue-sql.md)'s own table,
or [Firestore](queue-firestore.md) — and the resender is registered by `AddQueue`, so both are
available once the queue is composed.

**Paging is by cursor, not offset.** `ListAsync` returns entries newest first plus a `NextCursor`;
feed it back to get the next page, and stop when it comes back null — not when a page comes back
short, since the last full page has no cursor. The cursor is opaque and safe to put in a query
string. An offset would slide by one every time the page's own delete button was used.

**Resend takes the command type from the entry, never from the payload.** An operator may correct
the JSON; they cannot redirect it at a different command. A payload that no longer deserializes, or
a command type the queue no longer knows, comes back as a `ResendDeadLetterError` with the entry left
in place, so nothing recoverable is thrown away.

**How tightly the resend binds its two halves depends on the storage.** With a SQL storage — whatever
the delivery, Cloud Tasks included — the enqueue lands in the outbox and the delete in the dead-letter
table, and both commit together. Without one, they are merely ordered — enqueue first, so an
interruption leaves an entry to resend again rather than losing the payload.

## Tracing

The queue carries [W3C trace context](https://www.w3.org/TR/trace-context/) across the hop, so a
message is processed in the trace of whatever enqueued it — however long later, and however many
attempts in. `QueueEnvelope.TraceParent` and `TraceState` hold it; the consumer restores it before
the handler runs.

Spans are emitted on the `Spinneret.Queue` source (`QueueDiagnostics.ActivitySourceName`): a
`{Request} publish` producer span at enqueue, and a `{Request} process` consumer span covering the
whole delivery. Both are named for the request's own type name — `SendWelcomeEmail publish` — with
the qualified name on the `spinneret.request.type` tag and the channel on
`messaging.destination.name`; most hosts run one channel, so naming spans after it would say nothing.
Every Spinneret source name begins with `Spinneret.`, and that prefix is a stability guarantee:
subscribe by prefix and new packages are picked up as they ship.

```csharp
// OpenTelemetry
services.AddOpenTelemetry().WithTracing(t => t.AddSource(QueueDiagnostics.ActivitySourceName));
```

| Tag | | On |
| --- | --- | --- |
| `messaging.system` | Always `spinneret`. | both |
| `messaging.operation` | `publish` or `process`. | both |
| `messaging.destination.name` | The channel. | both |
| `spinneret.request.type` | The request type's qualified name. | both |
| `spinneret.queue.dedupe_key` | The idempotency key, when the enqueue supplied one. | publish |
| `messaging.message.id` | The id the *transport* assigned the message. | process |
| `spinneret.queue.attempt`, `spinneret.queue.max_attempts` | Where this delivery sits in the budget. | process |
| `spinneret.queue.outcome` | How the delivery ended: `ack`, `retry`, `defer`, `deadletter`, `discard`, or `transport-retry`. | process |

`retry`, `deadletter`, `discard` and `transport-retry` also set the span's status to `Error`; `ack`
and `defer` leave it unset. These strings are a contract — dashboards query them.

The dedupe key is deliberately not reported as `messaging.message.id`: that attribute means the id
the delivery assigned, and only Cloud Tasks derives one from the dedupe key (it becomes the task
name). A SQL storage's id is the row's own `MessageId`, unrelated to whatever key you passed, so
folding the two together would make a producer-to-consumer join work on one composition and
silently not on another.

Two properties are worth knowing:

- **Propagation does not need a listener.** Context is captured from `Activity.Current`, which
  ASP.NET Core populates whether or not anything listens. Registering a listener adds the spans; it
  is not what makes the trace id travel.
- **Retries and deferrals keep the original traceparent.** Every attempt of a task, and the dead
  letter it may end as, answer to one trace id — which is what makes "here is the dead letter, show
  me the request behind it" a single query. Do not rewrite it to the current attempt's context: on a
  transport redelivery the ambient activity belongs to the transport, not to the business operation.

A consequence worth expecting: a task deferred or retried over hours belongs to a trace that stays
open for hours. That is intended, not a leak.

## Composing the queue

The queue is registered by one call, `AddQueue`, which composes it from up to three choices — see
[the setup guide](README.md) for which to pick:

```csharp
services.AddQueue(q => q
    .AddRequestAssemblies(typeof(SyncCustomer).Assembly)   // the command types, once
    .UseMssqlStorage(configuration)                        // storage: where messages wait
    .UseInHostDelivery(configuration));                    // delivery: how they reach handlers
```

| Choice | Options |
|---|---|
| **Storage** | `UseMssqlStorage` or `UsePostgresStorage` ([SQL](queue-sql.md)) — or none, with a push delivery. |
| **Delivery** | `UseInHostDelivery` (drains a SQL storage in this host), `UseCloudTasksDelivery` ([Cloud Tasks](queue-cloudtasks.md)) — or none, on a producer-only host with a storage. |
| **Dead letters** | The storage's own table, `UseFirestoreDeadLetters` ([Firestore](queue-firestore.md)), or your own `IDeadLetterWriter`. |

Each choice is made at most once. `AddQueue` checks the combination when it returns — no request
assemblies, nowhere to put messages, two storages or two deliveries, or an in-host delivery with no
SQL storage all fail there, rather than at the first enqueue.

The host also supplies **`ISpinneretMediator` and the handlers** on every host that consumes —
`services.AddMediator([typeof(Program).Assembly])`.

Command types are indexed by `Type.FullName`, which is the name on the wire — so renaming or moving a
queued command type breaks messages already in flight. Two commands implementing `IRequest<>` twice,
or two types with the same full name across the scanned assemblies, fail at startup.

## Replacing a default

Defaults are registered with `TryAdd`, so your own implementation always wins: registered **before**
`AddQueue`, the default is never added at all; registered **after**, yours shadows it, because the
container resolves the last registration for a service type. Registering first is the clearer habit —
the container then holds exactly one registration.

That applies to `IQueuePayloadSerializer` (or choose one with `q.UsePayloadSerializer<T>()`),
`IDeadLetterWriter`, and the provider-specific seams each page lists. `IQueue` itself is the one
exception: it is composed by `AddQueue` alone, and an `IQueue` registered beforehand fails it.
