# Spinneret.Queue.Postgres

PostgreSQL as the queue's storage: the outbox and dead-letter tables in your application's own
database. This page covers what is specific to Postgres — configuration, schema, permissions,
connection pooling. The storage model, the transactional enqueue, the in-host worker and the
guarantees are the same for every SQL dialect and live in [queue-sql.md](queue-sql.md).

## Install

```sh
dotnet add package Spinneret.Queue.Postgres
```

```csharp
services.AddQueue(q => q
    .AddRequestAssemblies(typeof(SyncCustomer).Assembly)
    .UsePostgresStorage(configuration)
    .UseInHostDelivery(configuration));   // or .UseCloudTasksDelivery(configuration), or none
```

If the application already has an `NpgsqlDataSource` — typically from `AddNpgsqlDataSource`, shared
with EF Core or Dapper — hand that over instead of a connection string. The queue then uses the same
pool, and leaves disposing it to the host:

```csharp
services.AddNpgsqlDataSource(configuration.GetConnectionString("App")!);
services.AddQueue(q => q
    .AddRequestAssemblies(typeof(SyncCustomer).Assembly)
    .UsePostgresStorage(sp => sp.GetRequiredService<NpgsqlDataSource>(), configuration)
    .UseInHostDelivery(configuration));
```

## Requires

| | |
|---|---|
| **A connection string or data source** | To the app's own database — see [queue-sql.md](queue-sql.md#gotchas). |
| **PostgreSQL 13 or later** | For `gen_random_uuid()` without an extension. |
| **Mediator + handlers** | `services.AddMediator([...])`, on hosts that consume. |
| **DDL rights**, if `CreateSchema` is left on. |

## Configuration — `Queue:Postgres`

| Key | Type | Default | Notes |
|---|---|---|---|
| `ConnectionString` | `string` | — | **Mandatory**, unless supplied via `ConnectionStringName` or a data source is given. |
| `ConnectionStringName` | `string?` | `null` | Name of an entry in the standard `ConnectionStrings` section. |
| `SchemaName` | `string` | `"public"` | Must already exist — the DDL creates tables, not schemas. |
| `QueueTableName` | `string` | `"spinneret_queue"` | |
| `DeadLetterTableName` | `string` | `"spinneret_dead_letters"` | |
| `CreateSchema` | `bool` | `true` | Create the tables idempotently at startup. |

Identifiers must be plain SQL identifiers — ASCII letters, digits and underscore, starting with a
letter or underscore. Postgres silently truncates names longer than 63 bytes, so the limit is checked
not only for the configured names but for every index name derived from them (`<table>_dedupekey_key`
and so on): a table name long enough to push one over fails at startup, naming the key, instead of
two truncated names colliding later. Anything else fails at startup rather than reaching a query.

Names are quoted everywhere, so they keep the case they are configured with. The snake_case defaults
need no quoting when you query the tables yourself; a mixed-case name like `Outbox` does
(`SELECT * FROM "Outbox"`). Column names are unquoted in the DDL, so they are stored in lower case
and read the same with or without quotes.

```json
{
  "ConnectionStrings": { "App": "Host=…;Database=my_app;Username=…;Password=…" },
  "Queue": {
    "Postgres": { "ConnectionStringName": "App" },
    "InHost": { "ChannelParallelism": { "default": 4, "reports": 1 } }
  }
}
```

## Schema

No cloud resources — just a database. With `CreateSchema` on (the default) the tables are created
idempotently at startup, retrying up to five times because at boot the database is often still
warming up beside the host. A database that stays unreachable fails startup, which is the right
outcome for a host whose queue cannot work.

Two tables, using the defaults:

```sql
public.spinneret_queue
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY
    messageid    uuid         NOT NULL DEFAULT gen_random_uuid()
    channel      varchar(100) NOT NULL
    visibleat    timestamptz  NOT NULL
    dedupekey    varchar(200) NULL
    envelope     text         NOT NULL
    leaseduntil  timestamptz  NULL
    leasetoken   uuid         NULL
  + spinneret_queue_channel_visibleat_idx (channel, visibleat)
  + spinneret_queue_visibleat_idx (visibleat) INCLUDE (leaseduntil)
  + spinneret_queue_messageid_idx (messageid)
  + spinneret_queue_dedupekey_key UNIQUE (dedupekey) WHERE dedupekey IS NOT NULL

public.spinneret_dead_letters
    idempotencykey  varchar(200)  PRIMARY KEY
    source          varchar(20)   NOT NULL   -- 'Queue' | 'Scheduler'
    commandtypename varchar(500)  NOT NULL
    description     varchar(1000) NULL
    payloadjson     text          NOT NULL
    error           text          NOT NULL
    attempts        integer       NOT NULL
    deadletteredat  timestamptz   NOT NULL
    traceid         varchar(32)   NULL
  + spinneret_dead_letters_deadletteredat_idx (deadletteredat DESC, idempotencykey DESC)
```

- Envelopes and payloads are `text`, not `jsonb`: a message must come back byte for byte as it was
  written, and nothing queries inside it.
- Timestamps are `timestamptz` and always written in UTC; the session time zone does not matter.
- `messageid` is the stable name a message is delivered and dead-lettered under — and, over Cloud
  Tasks, the task name that makes forwarding idempotent. Its index serves the eager forward of a
  message just committed.
- `leaseduntil` / `leasetoken` are how the relay to Cloud Tasks holds a row while it sends it.
  In-host delivery leaves them null.
- `spinneret_queue_dedupekey_key` is load-bearing rather than an optimisation: it is the arbiter of
  the enqueue's `ON CONFLICT`, so a queue table missing it cannot enqueue with a `DedupeKey` at all.
  Re-running the script restores it.

Every column added after the first release and every index is guarded on its own existence
(`ADD COLUMN IF NOT EXISTS`, `CREATE INDEX IF NOT EXISTS`) rather than on its table's, so a database
that is missing one picks it up on the next startup.

### Owning the schema yourself

Set `CreateSchema = false` and emit the DDL from your own migrations:

```csharp
var ddl = PostgresQueueSchema.CreateScript(new PostgresStorageOptions
{
    SchemaName = "public",
    QueueTableName = "spinneret_queue",
    DeadLetterTableName = "spinneret_dead_letters",
});
```

The script is idempotent — safe to re-run, and each object is guarded on its own, so re-running it
after upgrading the package adds anything new without touching what is already there. This one switch
also gates the [scheduler's](scheduler-postgres.md) table.

It is also safe to run concurrently. It is a single `DO` block — one statement, so it applies
atomically — that first takes a transaction-scoped
[advisory lock](https://www.postgresql.org/docs/current/explicit-locking.html#ADVISORY-LOCKS) keyed on
the table name. `CREATE TABLE IF NOT EXISTS` is not safe against itself: two replicas booting together
can both find the table missing, and the loser fails on a duplicate catalog entry. The lock makes the
fleet take turns. Inside a migration that already has a transaction open, the lock simply lasts until
that transaction ends.

> **Upgrading a large existing queue table.** Adding `messageid` with its default rewrites the table to
> fill every row already waiting. On a table with a long backlog, run the script in a maintenance
> window, or drain the queue first.

### Permissions

| Mode | Needs |
|---|---|
| `CreateSchema = true` | `CREATE` on the target schema, and ownership of the tables to alter and index them later. |
| `CreateSchema = false` | DML only: `SELECT`, `INSERT`, `UPDATE`, `DELETE` on the queue, dead-letter and scheduler tables (the identity column needs no sequence grant beyond that). |

The second is the better production posture; run the DDL as part of a migration step with elevated
rights and let the application run with neither.

## How it is safe on Postgres

Postgres aborts a whole transaction on any failed statement: everything after it fails until a
rollback. That shapes the dialect in two places.

- **No expected outcome raises an error.** A duplicate dedupe key is an `ON CONFLICT … DO NOTHING`
  that returns no row; a duplicate dead letter likewise. Neither can take the caller's transaction
  down with it.
- **A handler that swallows a database error is not acknowledged.** If a handler catches an exception
  from its own SQL and returns normally, the delivery's transaction is already aborted, and
  committing it would silently roll back the handler's writes along with the acknowledge. After each
  handler the storage checks the transaction (one `SELECT 1`); an aborted one is rolled back to the
  handler's savepoint and the attempt fails like any other — retried per `[QueuePolicy]`, then
  dead-lettered. Let database exceptions propagate, or wrap a statement that may fail in a savepoint
  of your own.

Claims use `FOR UPDATE SKIP LOCKED`, so concurrent workers, relays and sweeps pass over each other's
rows instead of queueing behind them.

## Gotchas

- **Size the pool for concurrent deliveries.** Each in-host worker and each Cloud Tasks delivery holds
  one pooled connection for its whole transaction. Npgsql's default `Maximum Pool Size` is 100 per data
  source; keep the sum across replicas under the server's `max_connections`.
- **PgBouncer needs transaction pooling, and Npgsql needs telling.** Everything here is
  transaction-scoped — savepoints, advisory locks — so transaction mode works; session mode works
  too. Behind a transaction-mode pooler set `No Reset On Close=true` and leave automatic preparation
  (`Max Auto Prepare`) off, as for any Npgsql app.
- **Share your data source, share your transaction.** To enqueue inside your own unit of work, the
  transaction must come from the same database: begin it on a connection from your `NpgsqlDataSource`
  and pass it to `ISqlTransactionalQueue.Enqueue`, or publish it through `ISqlTransactionProvider`.
  With EF Core, `context.Database.CurrentTransaction!.GetDbTransaction()` is that transaction.
- **Text comparisons are case-sensitive.** Dedupe keys `Sync` and `sync` are two keys here, where SQL
  Server's usual collation treats them as one.
- **Cross-database work is not atomic.** Everything atomic here is atomic within the one database the
  queue lives in.
