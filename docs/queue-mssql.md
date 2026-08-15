# Spinneret.Queue.Mssql

SQL Server as the transport. Messages are rows in your application's own database, claimed by polling
workers. Because the queue lives in the same database as your business tables, an enqueue commits
atomically with the write that caused it, and a handler's writes commit atomically with the dequeue.

Unlike Cloud Tasks, this transport is also its own store — it ships a dead-letter table and needs no
companion package.

Read [queue.md](queue.md) first for the retry model; this page is the SQL-specific half.

## Install

```sh
dotnet add package Spinneret.Queue.Mssql
```

```csharp
services.AddMssqlQueue(configuration, typeof(SyncCustomer).Assembly);
services.AddMssqlQueueWorker();   // only on hosts that should consume
```

`AddMssqlQueueWorker()` is separate so a producer — a public API next to a worker service — never
consumes messages.

## Requires

| | |
|---|---|
| **A connection string** | To the app's own database — see the same-database gotcha below. |
| **Mediator + handlers** | `services.AddMediator(...)`. |
| **DDL rights**, if `CreateSchema` is left on. |

A dead-letter writer and a payload serializer are supplied by the package.

## Configuration — `Queue:Mssql`

| Key | Type | Default | Notes |
|---|---|---|---|
| `ConnectionString` | `string` | — | **Mandatory**, unless supplied via `ConnectionStringName`. |
| `ConnectionStringName` | `string?` | `null` | Name of an entry in the standard `ConnectionStrings` section. |
| `SchemaName` | `string` | `"dbo"` | Must already exist — the DDL creates tables, not schemas. |
| `QueueTableName` | `string` | `"SpinneretQueue"` | |
| `DeadLetterTableName` | `string` | `"SpinneretDeadLetters"` | |
| `CreateSchema` | `bool` | `true` | Create the tables idempotently at startup. |
| `PollInterval` | `TimeSpan` | `00:00:02` | How long an idle worker waits before polling again. |
| `ChannelParallelism:<channel>` | `int` | 1 per channel | Concurrent deliveries for that channel. |

Identifiers must be plain SQL identifiers — ASCII letters, digits and underscore, starting with a
letter or underscore, at most 116 characters. Anything else fails at startup rather than reaching a
query.

`ChannelParallelism` keys must name a channel some `[QueuePolicy]` declares (or `default`), so a typo
fails the host instead of silently configuring a channel nothing rides on.

```json
{
  "ConnectionStrings": { "App": "Server=…;Database=MyApp;…" },
  "Queue": {
    "Mssql": {
      "ConnectionStringName": "App",
      "ChannelParallelism": { "default": 4, "reports": 1 }
    }
  }
}
```

## Infrastructure

No cloud resources — just a database. With `CreateSchema` on (the default) the tables are created
idempotently at startup, retrying up to five times because at boot the database is often still
warming up beside the host. A database that stays unreachable fails startup, which is the right
outcome for a host whose queue cannot work.

Two tables, using the defaults:

```sql
[dbo].[SpinneretQueue]
    Id           BIGINT IDENTITY PRIMARY KEY
    Channel      NVARCHAR(100)   NOT NULL
    VisibleAt    DATETIME2(3)    NOT NULL
    DedupeKey    NVARCHAR(200)   NULL
    Envelope     NVARCHAR(MAX)   NOT NULL
  + IX_SpinneretQueue_Channel_VisibleAt (Channel, VisibleAt)
  + UX_SpinneretQueue_DedupeKey (DedupeKey) WHERE DedupeKey IS NOT NULL

[dbo].[SpinneretDeadLetters]
    IdempotencyKey  NVARCHAR(200) PRIMARY KEY
    Source          NVARCHAR(20)   NOT NULL   -- 'Queue' | 'Scheduler'
    CommandTypeName NVARCHAR(500)  NOT NULL
    Description     NVARCHAR(1000) NULL
    PayloadJson     NVARCHAR(MAX)  NOT NULL
    Error           NVARCHAR(MAX)  NOT NULL
    Attempts        INT            NOT NULL
    DeadLetteredAt  DATETIME2(3)   NOT NULL
```

### Owning the schema yourself

Set `CreateSchema = false` and emit the DDL from your own migrations:

```csharp
var ddl = MssqlQueueSchema.CreateScript(new MssqlQueueOptions
{
    SchemaName = "dbo",
    QueueTableName = "SpinneretQueue",
    DeadLetterTableName = "SpinneretDeadLetters",
});
```

The script is idempotent, and the method needs no DI. Note this one switch also gates the
[scheduler's](scheduler-mssql.md) table.

### Permissions

| Mode | Needs |
|---|---|
| `CreateSchema = true` | `CREATE TABLE` plus index creation on the target schema — roughly `db_ddladmin` or schema ownership. |
| `CreateSchema = false` | DML only: `SELECT`, `INSERT`, `UPDATE`, `DELETE` on the queue, dead-letter and scheduler tables. |

The second is the better production posture; run the DDL as part of a migration step with elevated
rights and let the application run with neither.

### No transport retry cap to get wrong

The GCP page warns that the queue's `retry_config` must be an unlimited backstop. There is no
equivalent here to misconfigure: a rolled-back delivery simply reappears in the table with its
`VisibleAt` pushed out, and nothing counts transport-level attempts. The application's
`[QueuePolicy]` is the only budget.

## Transactional enqueue

The point of putting the queue in your own database. Two ways to use it:

```csharp
// 1. Explicit — pass the transaction.
await using var tx = await connection.BeginTransactionAsync();
await repository.SaveAsync(order, tx);
await transactionalQueue.Enqueue(new SyncOrder(order.Id), tx);
await tx.CommitAsync();

// 2. Ambient — publish it once, then use plain IQueue.
transactions.Use(tx);
try { await queue.Enqueue(new SyncOrder(order.Id)); }
finally { transactions.Use(null); }
```

Either way the message row commits with the business write, or neither does.

**Without a transaction the enqueue still works** — it opens its own connection and auto-commits —
but you lose that guarantee. That is legal and sometimes what you want; it is just no longer an
outbox.

`IMssqlTransactionProvider` is the ambient seam, backed by `AsyncLocal` by default. A host with its
own unit-of-work mechanism can replace it, registering theirs *before* `AddMssqlQueue`.

## The worker

`AddMssqlQueueWorker()` starts one polling loop per channel, times that channel's
`ChannelParallelism` (default 1). Every channel any `[QueuePolicy]` declares gets a loop, plus
`default`.

Each delivery runs in its own transaction: the destructive dequeue *is* the lock, that transaction is
published as the ambient one so the handler's writes and any cascade enqueues join it, and the commit
makes the whole delivery atomic. A crash or rollback puts the message back untouched — the
redelivery path that deliberately does not spend an attempt.

An empty channel waits `PollInterval`; a delivered message polls again immediately, so a backlog
drains at full speed.

`ChannelParallelism` is read once at startup — changing it later adds no loops.

## Gotchas

- **The queue must live in the app's own database.** Point it at a separate "queue database" and every
  atomicity guarantee above is gone; cross-database transactions would need MSDTC.
- **Register a custom `IQueueDispatchBoundary` before `AddMssqlQueue`.** Unlike the `TryAdd` seams —
  where either order works — this one is *inspected* at call time: `AddMssqlQueue` replaces the
  boundary unless it finds one already registered that isn't the pass-through default. Registered
  afterwards, yours is added but the transport's savepoint boundary was already put in its place.
- **Do not set `SET XACT_ABORT ON`** on connections used for enqueuing. Dedupe relies on catching a
  unique-key violation as a statement-level error so the caller's transaction survives.
- **FIFO is best-effort.** Workers skip rows locked by peers (`READPAST`) rather than blocking, so a
  slow handler does not stall the channel — but ordering across concurrent workers is not guaranteed.
- **A long-running handler holds a row lock and an open transaction** for its whole duration. Keep
  queued work short, or raise `ChannelParallelism` so one slow message does not starve the channel.
- **`AddMssqlQueue` throws during registration** for bad configuration, not at `Build()`.
- **Dead-lettering never drops work**: if the dead-letter table is unreachable the message keeps being
  redelivered, logged at Critical with the payload.
