# Spinneret.Queue.Sql

The queue's **storage** in your application's own database: messages are rows in an outbox table, and
every enqueue is an insert that joins the caller's transaction. Because the queue lives beside your
business tables, an enqueue commits atomically with the write that caused it — and, during a delivery,
a handler's writes commit atomically with the delivery's outcome.

This package is the database-neutral half, written once against ADO.NET. You install it through a
dialect package, which supplies the SQL and the connection:

| Dialect | Database |
|---|---|
| [`Spinneret.Queue.Mssql`](queue-mssql.md) | SQL Server |
| [`Spinneret.Queue.Postgres`](queue-postgres.md) | PostgreSQL |

Both behave the same — the same tests run against each — and differ only in spelling: the SQL, the
DDL, the table-name defaults, and how a database error affects a transaction.

The storage needs a **delivery** — something that moves messages from the table to their handlers:

| Delivery | Package | Fits |
|---|---|---|
| `q.UseInHostDelivery()` | this one | An always-on service: polling workers in the host drain the table. |
| `q.UseCloudTasksDelivery(...)` | [`Spinneret.Queue.CloudTasks`](queue-cloudtasks.md) | Cloud Run and other push-driven hosts: a relay forwards the table to Cloud Tasks, which calls back into the host. |

A host that only produces — a public API in front of a worker service — composes the storage with no
delivery at all.

Read [queue.md](queue.md) first for the retry model.

## Install

```csharp
services.AddQueue(q => q
    .AddRequestAssemblies(typeof(SyncCustomer).Assembly)
    .UseMssqlStorage(configuration)       // or .UsePostgresStorage(configuration)
    .UseInHostDelivery(configuration));   // only on hosts that should consume
```

The storage supplies the dead-letter writer and store (its own table), the unit of work, and the
ambient-transaction seam. A dead-letter store chosen explicitly — `q.UseFirestoreDeadLetters()` —
takes over from the table.

## Transactional enqueue

The point of putting the queue in your own database. Two ways to use it:

```csharp
// 1. Explicit — pass the transaction.
await using var tx = await connection.BeginTransactionAsync();
await repository.SaveAsync(order, tx);
await transactionalQueue.Enqueue(new SyncOrder(order.Id), tx);   // ISqlTransactionalQueue
await tx.CommitAsync();

// 2. Ambient — publish it once, then use plain IQueue.
transactions.Use(tx);                                            // ISqlTransactionProvider
try { await queue.Enqueue(new SyncOrder(order.Id)); }
finally { transactions.Use(null); }
```

Either way the message row commits with the business write, or neither does.

**Without a transaction the enqueue still works** — it opens its own connection and auto-commits —
but you lose that guarantee. That is legal and sometimes what you want; it is just no longer an
outbox.

`ISqlTransactionProvider` is the ambient seam, backed by `AsyncLocal` by default. A host with its own
unit-of-work mechanism can replace it by registering theirs *before* `AddQueue`.

## Deliveries are units of work

Whichever delivery you choose, each message is delivered inside one database transaction, published
as the ambient one so everything SQL-touching in the delivery joins it:

- the handler's own writes (through `ISqlTransactionProvider.Current`),
- the messages it enqueues — cascades land in the outbox in the same transaction,
- the outcome the queue books: a retry or a deferral is a new outbox row, a dead letter a row in the
  dead-letter table.

The handler runs under a savepoint. A handler that fails has its partial writes — cascades included —
rolled back to the savepoint, while the delivery transaction survives to book the retry or the dead
letter. A handler that *catches* a database error and returns normally counts as failed when the
error left the transaction unusable (as any error does on Postgres): committing would silently drop
its writes, so they are rolled back to the savepoint and the attempt is booked like any other
failure. Then the whole delivery commits at once. A crash or a failed commit leaves nothing behind and
the message is redelivered as it was, without spending an attempt.

The edge stays at-least-once: work outside the database — an HTTP call, an email — can happen and
then be rolled back with everything else. Make those idempotent.

## In-host delivery

`q.UseInHostDelivery()` starts one polling loop per channel, times that channel's
`ChannelParallelism` (default 1). Every channel any `[QueuePolicy]` declares gets a loop, plus
`default`.

Each delivery's transaction begins with a destructive dequeue, which *is* the lock: the row is
invisible to other workers exactly while the delivery runs, and a rollback puts it back. An empty
channel waits `PollInterval`; a delivered message polls again immediately, so a backlog drains at full
speed.

### Configuration — `Queue:InHost`

| Key | Type | Default | Notes |
|---|---|---|---|
| `PollInterval` | `TimeSpan` | `00:00:02` | How long an idle worker waits before polling again. |
| `ChannelParallelism:<channel>` | `int` | 1 per channel | Concurrent deliveries for that channel. |

`ChannelParallelism` keys must name a channel some `[QueuePolicy]` declares (or `default`), so a typo
fails the host instead of silently configuring a channel nothing rides on. It is read once at
startup — changing it later adds no loops.

### No transport retry cap to get wrong

Cloud Tasks needs its queue's `retry_config` to be an unlimited backstop. There is no equivalent here
to misconfigure: a rolled-back delivery simply reappears in the table with its visibility pushed
out, and nothing counts transport-level attempts. The application's `[QueuePolicy]` is the only
budget.

## Guarantees

| | In-host delivery | Cloud Tasks delivery |
|---|---|---|
| **Enqueue** | Atomic with the caller's transaction. | Same; the relay forwards it once committed. |
| **Delivery** | One transaction: claim, handler writes, cascades, booking, dead letter. | One transaction: handler writes, cascades, booking, dead letter; committed before the `200`. |
| **Resend** | Enqueue and dead-letter delete in one transaction. | Same. |
| **SQL scheduler** | A due job's booking and its enqueue commit together. | Same; then forwarded. |
| **Duplicates** | A handler re-runs only after a crash or failed commit. | Also when the `200` is lost after the commit. |

A dead letter written to a store outside the database (`q.UseFirestoreDeadLetters()`) is not part of
the delivery's transaction: it relies on its idempotency key alone, and a delivery whose commit then
fails is redelivered and may leave an entry for an attempt that later succeeds. Prefer the storage's
own table unless the admin tooling lives elsewhere.

## Gotchas

- **The queue must live in the app's own database.** Point it at a separate "queue database" and every
  atomicity guarantee above is gone.
- **A long-running handler holds an open transaction** for its whole duration — and, in-host, a row
  lock. Keep queued work short, or raise `ChannelParallelism` so one slow message does not starve the
  channel.
- **FIFO is best-effort.** Workers skip rows locked by peers rather than blocking, so a slow handler
  does not stall the channel — but ordering across concurrent workers is not guaranteed.
- **Dead-lettering never drops work**: if the dead-letter table is unreachable the message keeps being
  redelivered, logged at Critical with the payload.
- **Bad configuration fails at `AddQueue`**, not at `Build()` — and again at host start for values
  changed later.
