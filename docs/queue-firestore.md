# Spinneret.Queue.Firestore

A Firestore-backed dead-letter store: one document per task the queue gave up on, written by
`IDeadLetterWriter` and read back through `IDeadLetterStore`.

It exists because a transport is not always a store. SQL Server keeps dead letters in a table beside
the queue; Cloud Tasks stores nothing you can query, so a host on Cloud Tasks needs somewhere to put
them. This package depends only on the provider-agnostic core, so it works behind any transport — not
just Cloud Tasks.

## Install

```sh
dotnet add package Spinneret.Queue.Firestore
```

```csharp
services.AddGcpQueue(configuration, o => o.RequestAssemblies = [typeof(SyncCustomer).Assembly]);
services.AddFirestoreDeadLetters(configuration);
```

That one call registers both the writer and the store. Registration order does not matter, and both
are registered with `TryAdd` — anything you registered yourself always wins.

## Requires

A host-registered `FirestoreDb`. This package does not create one, because how you build it
(project id, emulator detection, credentials) is a host decision:

```csharp
services.AddSingleton(_ => new FirestoreDbBuilder
{
    ProjectId = configuration["Queue:Gcp:ProjectId"],
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
services.AddFirestoreDeadLetters(o => o.Collection = "failed_tasks");
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

`AddFirestoreDeadLetters` also registers `IDeadLetterStore`, so listing, discarding and
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

- **Nothing is atomic with the delivery.** Firestore and the queue transport are separate systems, so
  unlike the MSSQL writer — which joins the delivery transaction — a dead letter here commits on its
  own. The delivery processor accounts for that by retrying rather than acknowledging a failed write,
  which is why the write must stay idempotent.
- **Availability is on the critical path.** A task whose dead-letter write keeps failing keeps being
  redelivered rather than dropped, logged at Critical with the full payload.
- **A writer or store you register yourself always wins.** Both are `TryAdd`, so registering your own
  before `AddFirestoreDeadLetters` means this one is never added; registering it after shadows this
  one, since the container resolves the last registration. Either order works.
- **A resend here is ordered, not atomic.** The enqueue goes to the transport and the delete to
  Firestore, and no transaction spans the two. The enqueue happens first, so an interruption between
  them leaves an entry that can be resent again rather than a payload that is gone. The SQL Server
  store, sharing a database with its queue, commits both together.
