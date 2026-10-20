# Spinneret.Queue.Mssql

SQL Server as the queue's storage: the outbox and dead-letter tables in your application's own
database. This page covers what is specific to SQL Server — configuration, schema, permissions. The
storage model, the transactional enqueue, the in-host worker and the guarantees are the same for
every SQL dialect and live in [queue-sql.md](queue-sql.md).

## Install

```sh
dotnet add package Spinneret.Queue.Mssql
```

```csharp
services.AddQueue(q => q
    .AddRequestAssemblies(typeof(SyncCustomer).Assembly)
    .UseMssqlStorage(configuration)
    .UseInHostDelivery(configuration));   // or .UseCloudTasksDelivery(configuration), or none
```

## Requires

| | |
|---|---|
| **A connection string** | To the app's own database — see [queue-sql.md](queue-sql.md#gotchas). |
| **Mediator + handlers** | `services.AddMediator([...])`, on hosts that consume. |
| **DDL rights**, if `CreateSchema` is left on. |

## Configuration — `Queue:Mssql`

| Key | Type | Default | Notes |
|---|---|---|---|
| `ConnectionString` | `string` | — | **Mandatory**, unless supplied via `ConnectionStringName`. |
| `ConnectionStringName` | `string?` | `null` | Name of an entry in the standard `ConnectionStrings` section. |
| `SchemaName` | `string` | `"dbo"` | Must already exist — the DDL creates tables, not schemas. |
| `QueueTableName` | `string` | `"SpinneretQueue"` | |
| `DeadLetterTableName` | `string` | `"SpinneretDeadLetters"` | |
| `CreateSchema` | `bool` | `true` | Create the tables idempotently at startup. |

Identifiers must be plain SQL identifiers — ASCII letters, digits and underscore, starting with a
letter or underscore, at most 116 characters. Anything else fails at startup rather than reaching a
query.

```json
{
  "ConnectionStrings": { "App": "Server=…;Database=MyApp;…" },
  "Queue": {
    "Mssql": { "ConnectionStringName": "App" },
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
[dbo].[SpinneretQueue]
    Id           BIGINT IDENTITY PRIMARY KEY
    MessageId    UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID()
    Channel      NVARCHAR(100)    NOT NULL
    VisibleAt    DATETIME2(3)     NOT NULL
    DedupeKey    NVARCHAR(200)    NULL
    Envelope     NVARCHAR(MAX)    NOT NULL
    LeasedUntil  DATETIME2(3)     NULL
    LeaseToken   UNIQUEIDENTIFIER NULL
  + IX_SpinneretQueue_Channel_VisibleAt (Channel, VisibleAt)
  + IX_SpinneretQueue_VisibleAt (VisibleAt) INCLUDE (LeasedUntil)
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
    TraceId         NVARCHAR(32)   NULL
  + IX_SpinneretDeadLetters_DeadLetteredAt (DeadLetteredAt DESC, IdempotencyKey DESC)
```

- `MessageId` is the stable name a message is delivered and dead-lettered under — and, over Cloud
  Tasks, the task name that makes forwarding idempotent. Rows created before the column existed are
  given one when the column is added.
- `LeasedUntil` / `LeaseToken` are how the relay to Cloud Tasks holds a row while it sends it. In-host
  delivery leaves them null.
- `IX_SpinneretQueue_VisibleAt` serves the relay, which leases across every channel by due time.
- `IX_SpinneretDeadLetters_DeadLetteredAt` serves the [dead-letter page's](queue.md#the-dead-letter-page)
  keyset paging.

Every column added after the first release and every index is guarded on its own existence rather
than on its table's, so a database that is missing one picks it up on the next startup instead of
being skipped because the table was already there.

`UX_SpinneretQueue_DedupeKey` is load-bearing rather than an optimisation: an enqueue detects a
duplicate `DedupeKey` by catching that index's unique violation, so a queue table missing it accepts
the duplicate and reports success. Re-running the script restores it.

### Owning the schema yourself

Set `CreateSchema = false` and emit the DDL from your own migrations:

```csharp
var ddl = MssqlQueueSchema.CreateScript(new MssqlStorageOptions
{
    SchemaName = "dbo",
    QueueTableName = "SpinneretQueue",
    DeadLetterTableName = "SpinneretDeadLetters",
});
```

The script is idempotent — safe to re-run, and each object is guarded on its own, so re-running it
after upgrading the package adds anything new without touching what is already there. This one switch
also gates the [scheduler's](scheduler-mssql.md) table.

It is also safe to run concurrently: it opens its own transaction and takes an
[application lock](https://learn.microsoft.com/sql/relational-databases/system-stored-procedures/sp-getapplock-transact-sql)
for the duration, so a fleet of replicas all running it at startup serialize instead of racing to
create the same table. Embedding it in a migration that already has a transaction open is fine —
the nested pair balances — but a migration runner that forbids transactional DDL will object.

> **Upgrading a large existing queue table.** Adding `MessageId` with its default fills every row
> already waiting. On a table with a long backlog, run the script in a maintenance window, or drain
> the queue first.

### Permissions

| Mode | Needs |
|---|---|
| `CreateSchema = true` | `CREATE TABLE`, `ALTER TABLE` and index creation on the target schema — roughly `db_ddladmin` or schema ownership. |
| `CreateSchema = false` | DML only: `SELECT`, `INSERT`, `UPDATE`, `DELETE` on the queue, dead-letter and scheduler tables. |

The second is the better production posture; run the DDL as part of a migration step with elevated
rights and let the application run with neither.

## Gotchas

- **Do not set `SET XACT_ABORT ON`** on connections used for enqueuing. Dedupe relies on catching a
  unique-key violation as a statement-level error so the caller's transaction survives.
- **Cross-database work needs MSDTC.** Everything atomic here is atomic within the one database the
  queue lives in.
