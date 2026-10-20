# Spinneret.Queue.Firestore

A Firestore-backed dead-letter store: one document per task the queue gave up on, written by
`IDeadLetterWriter` and read back through `IDeadLetterStore`.

It exists because a delivery is not always a store. A SQL storage keeps dead letters in a table beside
the outbox; Cloud Tasks on its own stores nothing you can query, so a host on Cloud Tasks without a
SQL storage needs somewhere to put them. This package depends only on the provider-agnostic core, so
it composes with any queue — and, chosen alongside a SQL storage, takes over from its table.

## Install

```sh
dotnet add package Spinneret.Queue.Firestore
```

```csharp
services.AddQueue(q => q
    .AddRequestAssemblies(typeof(SyncCustomer).Assembly)
    .UseCloudTasksDelivery(configuration)
    .UseFirestoreDeadLetters(configuration));
```

That one call registers both the writer and the store, ahead of anything the queue's storage would
register — and anything you registered yourself before `AddQueue` still wins.

## Requires

A host-registered `FirestoreDb`. This package does not create one, because how you build it
(project id, emulator detection, credentials) is a host decision:

```csharp
services.AddSingleton(_ => new FirestoreDbBuilder
{
    ProjectId = configuration["Queue:CloudTasks:ProjectId"],
    EmulatorDetection = EmulatorDetection.EmulatorOrProduction,
}.Build());
```

With `EmulatorDetection.EmulatorOrProduction`, setting `FIRESTORE_EMULATOR_HOST` is all local
development needs.

## Configuration — `Queue:Firestore`

| Key | Type | Default | Notes |
|---|---|---|---|
| `Collection` | `string` | `"dead_letters"` | Root collection dead letters are written to. Must be non-blank. |

Or in code:

```csharp
q.UseFirestoreDeadLetters(o => o.Collection = "failed_tasks");
```

## Document shape

The document id is the entry's `IdempotencyKey` — the transport's task id — which is what makes a
retried write land on the document it already wrote instead of creating a second one.

| Field | Type | Notes |
|---|---|---|
| `source` | string | `"Queue"` or `"Scheduler"`. Matches the MSSQL column value, so one reader serves either store. |
| `commandTypeName` | string | |
| `description` | string \| null | Present but null when the enqueuer supplied none, so readers see a consistent shape. |
| `payloadJson` | string | |
| `error` | string | |
| `attempts` | int | |
| `deadLetteredAt` | timestamp | When the queue gave up. |

**The first write wins.** A redelivered dead-letter write is ignored rather than overwriting, so
`deadLetteredAt` records when the failure actually happened rather than when it was last retried.
This mirrors the MSSQL writer, whose insert swallows a duplicate key.

These field names are a data contract — the `IDeadLetterStore` in this package binds to them, and so
does the MSSQL one to its columns, so treat a rename as a breaking change.

## Reading them back

`UseFirestoreDeadLetters` also registers `IDeadLetterStore`, so listing, discarding and
[resending](queue.md#the-dead-letter-page) work against this collection with nothing further to wire.

Entries come back newest first, paged by an opaque cursor:

```csharp
var page = await store.ListAsync(new DeadLetterQuery { PageSize = 50, Cursor = cursor });
```

## Infrastructure

**No index to create.** Listing orders by `deadLetteredAt` descending and then by document id in the
same direction — which is what Firestore does implicitly anyway, and is served by the automatic
single-field index. Lookups and deletes go by document id.

That holds only as long as the query stays unfiltered. If a future version filters by `source`, or you
add your own filtered query, that one **will** need a composite index — Firestore's error message
names the index to create.

Retention is manual. Nothing expires these documents, which is usually what you want for a store whose
whole purpose is that a human looks at it. If you would rather they aged out, add a Firestore TTL
policy on a field you populate yourself.

## Gotchas

- **Nothing is atomic with the delivery.** Firestore and the queue are separate systems, so unlike a
  SQL storage's table — which joins the delivery transaction — a dead letter here commits on its own.
  The delivery processor accounts for that by retrying rather than acknowledging a failed write,
  which is why the write must stay idempotent. Over a SQL storage this also means a delivery whose
  commit fails after the dead letter was written leaves an entry behind for an attempt that is
  redelivered; prefer the storage's own table unless your admin tooling lives in Firestore.
- **Availability is on the critical path.** A task whose dead-letter write keeps failing keeps being
  redelivered rather than dropped, logged at Critical with the full payload.
- **A writer or store you register yourself always wins.** Both are `TryAdd`, so registering your own
  before `AddQueue` means this one is never added; registering it after shadows this one, since the
  container resolves the last registration. Either order works.
- **A resend here is ordered, not atomic.** The enqueue goes to the transport and the delete to
  Firestore, and no transaction spans the two. The enqueue happens first, so an interruption between
  them leaves an entry that can be resent again rather than a payload that is gone. A SQL storage's
  own table, sharing a database with its outbox, commits both together.
