# Spinneret.Queue.CloudTasks

Google Cloud Tasks as the **delivery**. Each message becomes a task that Cloud Tasks POSTs to an
OIDC-protected endpoint in your app, which runs the handler.

It composes two ways:

- **On its own.** Each enqueue is a call to Cloud Tasks. Simple, but not atomic with anything the
  caller writes — and Cloud Tasks stores nothing you can query, so dead letters need a store of their
  own ([`Spinneret.Queue.Firestore`](queue-firestore.md)).
- **Over a SQL storage** ([`Spinneret.Queue.Sql`](queue-sql.md) with a dialect:
  [`Spinneret.Queue.Mssql`](queue-mssql.md) or [`Spinneret.Queue.Postgres`](queue-postgres.md)). Every enqueue lands in the outbox table inside the
  caller's transaction, a relay forwards it to Cloud Tasks, and each delivery runs in one SQL
  transaction committed before the endpoint answers. See [Over a SQL storage](#over-a-sql-storage).

Read [queue.md](queue.md) first for the retry model; this page is the Cloud Tasks half.

## Install

```sh
dotnet add package Spinneret.Queue.CloudTasks
```

```csharp
builder.Services.AddQueue(q => q
    .AddRequestAssemblies(typeof(SyncCustomer).Assembly)
    .UseCloudTasksDelivery(builder.Configuration)
    .UseFirestoreDeadLetters(builder.Configuration));

app.UseAuthentication();
app.UseAuthorization();
app.MapCloudTasksDispatch();   // consumer hosts only
```

## Requires

| | |
|---|---|
| **Dead-letter store** | A SQL storage's own table, `q.UseFirestoreDeadLetters()`, or your own `IDeadLetterWriter`. `MapCloudTasksDispatch` refuses to map without one. |
| **Mediator + handlers** | `services.AddMediator([...])`. |
| **`Http.Json.JsonOptions`** | Unless you choose a serializer with `q.UsePayloadSerializer<T>()`, payloads serialize with ASP.NET Core's **HTTP** JSON options. Producer and consumer must agree on converters or payloads will not round-trip. |
| **Auth middleware** | `UseAuthentication()` / `UseAuthorization()` before endpoints. |
| **Credentials** | Application Default Credentials. No key file is ever read; on Cloud Run that is the service identity. |

> **Producer-only hosts need the full configuration too.** A service that only enqueues still needs
> `DispatcherUrl` and `ServiceAccountEmail`, because both are written into every task it creates —
> they describe where the *consumer* receives it, not where the producer runs.

## Configuration — `Queue:CloudTasks`

Mandatory. A missing one fails at `AddQueue` and again at host start:

| Key | Notes |
|---|---|
| `ProjectId` | |
| `LocationId` | The Cloud Tasks region. Need not match where the app runs — Cloud Tasks is not available in every region Cloud Run is. |
| `Channels:default` | Always required; the channel commands ride when they declare none. |
| `Channels:<name>` | One per channel any `[QueuePolicy(Channel = …)]` declares. An unmapped channel fails at boot. |
| `DispatcherUrl` | Absolute URL Cloud Tasks posts to. Must be `https` outside the emulator. |
| `ServiceAccountEmail` | The account Cloud Tasks impersonates to mint the OIDC token. |

Optional:

| Key | Default | Notes |
|---|---|---|
| `OidcAudience` | `DispatcherUrl` | The JWT `aud`. An opaque string, not necessarily a URL. |
| `OidcIssuer` | `https://accounts.google.com` | **Required when `EmulatorEndpoint` is set.** |
| `EmulatorEndpoint` | none | gRPC `host:port` of a local emulator. Its presence switches the whole package into emulator mode. |
| `Relay:*` | see below | Only used over a SQL storage. |

```json
{
  "Queue": {
    "CloudTasks": {
      "ProjectId": "my-project",
      "LocationId": "europe-west1",
      "Channels": { "default": "my-default", "reports": "my-reports" },
      "DispatcherUrl": "https://worker.example.com/internal/queue/dispatch",
      "ServiceAccountEmail": "cloud-tasks-invoker@my-project.iam.gserviceaccount.com"
    }
  }
}
```

In production these usually arrive as environment variables (`Queue__CloudTasks__ProjectId`, …) so
the values can come from whatever provisions the infrastructure.

## The dispatch endpoint

`MapCloudTasksDispatch()` routes on the **path of `DispatcherUrl`**, so the address Cloud Tasks posts
to and the route the app listens on cannot drift. Passing a pattern that disagrees throws at startup.

That check exists because the mismatch is otherwise near-invisible: every task 404s, and since the
queue's retry configuration is an unlimited backstop, it retries until it expires — with nothing in
the app to show for it.

The endpoint answers:

| Status | When |
|---|---|
| `200` | The delivery is finished — succeeded, retry booked, deferred, discarded or dead-lettered — and, over a SQL storage, committed. |
| `429` + `Retry-After` | The app wants redelivery: the outcome could not be booked, or the delivery's commit failed. Not counted against the attempt budget. |
| `400` | No `X-CloudTasks-TaskName` header. The task name is the delivery's identity — what its dead letter is filed under — and Cloud Tasks always sends it, so a request without one did not come from Cloud Tasks. |

### Trace context on the task

Besides the envelope, every task carries the traceparent as a `traceparent` header (and `tracestate`
when set). Cloud Tasks reserves the `X-CloudTasks-*`, `X-Google-*` and `X-AppEngine-*` prefixes, so
this passes through untouched.

The header is what puts ASP.NET Core's own server span for the dispatch request in the business
trace. Without it that span is a fresh root, and every line the framework logs for the delivery —
request logging, an auth failure, an envelope too corrupt to read — lands in a trace unrelated to
the work.

The envelope stays the source of truth: it is what the consumer parses, it is transport-neutral, and
it survives dead-lettering, a resend, and the hop through a SQL outbox. The header is taken from the
envelope rather than the ambient activity so the two cannot disagree.

### The OIDC scheme it registers

`UseCloudTasksDelivery` also registers a JwtBearer authentication scheme and an authorization policy,
both named `QueueOIDC` and exposed as `CloudTasksAuth.SchemeName` / `CloudTasksAuth.PolicyName`. The
dispatch endpoint guards itself with that policy.

Use the constant to protect your own internal endpoints with the same Google-minted tokens — it is
what [`Spinneret.Scheduler.AspNetCore`](scheduler-aspnetcore.md) and `MapQueueRelay` expect you to pass:

```csharp
app.MapSchedulerSweep(CloudTasksAuth.PolicyName);
```

## Over a SQL storage

```csharp
builder.Services.AddQueue(q => q
    .AddRequestAssemblies(typeof(SyncCustomer).Assembly)
    .UseMssqlStorage(builder.Configuration)
    .UseCloudTasksDelivery(builder.Configuration));
```

What changes, compared to Cloud Tasks on its own:

| | Cloud Tasks alone | Over a SQL storage |
|---|---|---|
| **Enqueue** | A call to Cloud Tasks. Not atomic with the caller's writes. | An outbox insert that joins the caller's transaction: it commits with the business write, or neither does. |
| **Delivery** | Each write is durable as it is made. | One SQL transaction holds the handler's writes, its cascade enqueues, the retry or deferral booking and the dead letter; it commits before the `200`. A failed commit answers `429` and leaves nothing behind. |
| **Dead letters** | Need a separate store. | The storage's table, written in the delivery's transaction. |
| **Resend** | Enqueue, then delete — ordered. | One transaction. |
| **SQL scheduler** | Not possible. | A due job's booking and its outbox insert commit together. |

At-least-once still holds at the edge: if the `200` is lost after the commit, Cloud Tasks redelivers
and the handler runs again. Make handlers idempotent for side effects outside the database.

### The relay

Messages leave the outbox through a relay, which leases due rows, creates a task for each, and
deletes the row once Cloud Tasks has it. Every task is created under a name Cloud Tasks deduplicates
on — the producer's `DedupeKey` when there is one, otherwise the message's id — which is what makes
forwarding idempotent: a relay that dies between sending and deleting leaves the row leased, a later
pass sends it again, Cloud Tasks answers "already exists", and the row goes. A duplicate delivery is
possible only if the row outlives Cloud Tasks' memory of the name, about an hour after the task ran.

Messages are forwarded at three moments:

- **As they commit**, when a delivery, a scheduler sweep, a resend, or an enqueue outside any
  transaction commits — before the dispatch endpoint answers, so a CPU-throttled host still forwards
  them while it has the CPU.
- **On a timer**, in every host that registered the queue (`Relay:RunInBackground`, on by default).
  This picks up messages committed in the application's own transactions, which cannot be seen to
  commit, and anything the moment-of-commit forward missed.
- **On request**, from `MapQueueRelay`, for hosts that turn the timer off.

| Key | Default | Notes |
|---|---|---|
| `Relay:RunInBackground` | `true` | Off on a host that scales to zero or is CPU-throttled outside requests (Cloud Run's default); map `MapQueueRelay` and drive it from Cloud Scheduler instead. |
| `Relay:PollInterval` | `00:00:01` | Between background passes. |
| `Relay:BatchSize` | `100` | Messages leased at a time. |
| `Relay:LeaseDuration` | `00:01:00` | How long a message is hidden from other relays while it is sent. |
| `Relay:ForwardAhead` | `1.00:00:00` | How far ahead of its delivery time a delayed message is handed to Cloud Tasks; at most 29 days, since Cloud Tasks schedules no further. Messages due later wait in the outbox — queryable, and cancellable by deleting the row. |

```csharp
app.MapQueueRelay(CloudTasksAuth.PolicyName);   // POST /internal/queue/relay
```

A send that fails stays in the outbox and is retried after a 30-second backoff. Ordering is not
preserved — Cloud Tasks does not order tasks either.

> **Named tasks.** Every forwarded task is named, and Cloud Tasks documents lower throughput for named
> tasks than for anonymous ones. Correctness wins here; very high-volume queues should measure.

> **One delivery per outbox.** A table drained by the relay should not also be drained by an in-host
> worker. A worker skips rows a relay holds, so a fleet migrating between the two stays safe, but
> which host takes which message is then undefined.

## Infrastructure

None of this is created by the library.

**One Cloud Tasks queue per mapped channel**, at
`projects/{ProjectId}/locations/{LocationId}/queues/{queueId}`, with a generic HTTP target.

```hcl
resource "google_cloud_tasks_queue" "default" {
  name     = "my-default"
  location = "europe-west1"

  rate_limits {
    max_dispatches_per_second = 20
    max_concurrent_dispatches = 10
  }

  # An unlimited backstop, deliberately. The application terminates tasks; these values
  # only cover deliveries that never reached it. Lowering max_attempts silently
  # overrides every [QueuePolicy] in the codebase.
  retry_config {
    max_attempts       = -1
    max_retry_duration = "604800s"
    min_backoff        = "10s"
    max_backoff        = "600s"
  }
}
```

Rate limits, unlike retries, *do* belong here — and are the reason to declare extra channels. Give a
rate-limited integration its own channel and cap `max_dispatches_per_second` on that queue alone.

**A service account for `ServiceAccountEmail`**, plus:

| Grant | On | To |
|---|---|---|
| `roles/cloudtasks.enqueuer` | the project / each queue | every identity that enqueues (or relays) |
| `roles/iam.serviceAccountUser` | the invoker SA | every identity that enqueues (or relays) |
| `roles/run.invoker` | the target service | the invoker SA |

```hcl
resource "google_service_account" "cloud_tasks_invoker" {
  account_id   = "cloud-tasks-invoker"
  display_name = "Cloud Tasks → Cloud Run OIDC invoker"
}

resource "google_project_iam_member" "enqueuer" {
  project = var.project_id
  role    = "roles/cloudtasks.enqueuer"
  member  = "serviceAccount:${google_service_account.backend.email}"
}

# CreateTask performs an act-as check on the account named in the OIDC token,
# which serviceAccountTokenCreator alone does not satisfy.
resource "google_service_account_iam_member" "can_act_as_invoker" {
  service_account_id = google_service_account.cloud_tasks_invoker.name
  role               = "roles/iam.serviceAccountUser"
  member             = "serviceAccount:${google_service_account.backend.email}"
}

resource "google_cloud_run_service_iam_member" "tasks_invoker" {
  service = google_cloud_run_service.backend.name
  role    = "roles/run.invoker"
  member  = "serviceAccount:${google_service_account.cloud_tasks_invoker.email}"
}
```

Two things routinely surprise people:

- **The dispatch host is itself an enqueuer.** Application-level retries and deferrals re-enqueue a
  fresh task — directly, or through the outbox and its relay — so the consumer needs enqueue rights
  too, not just the producer.
- **`serviceAccountUser`, not just `serviceAccountTokenCreator`**, as the comment above notes.

### The security perimeter is the audience, not the caller

The endpoint validates that the JWT was issued by the configured issuer and carries the configured
audience. It **does not** compare the token's identity against `ServiceAccountEmail`. Any principal
holding a Google-issued token with the right audience is accepted, so treat the audience — which
defaults to the dispatcher URL — as the thing keeping the endpoint private.

## Local development

```yaml
services:
  cloud-tasks:
    image: ghcr.io/aertje/cloud-tasks-emulator:latest
    command: >
      -host 0.0.0.0 -port 8123
      -openid-issuer http://127.0.0.1:8980
    ports: ["8123:8123", "8980:8980"]
```

```json
{
  "Queue": {
    "CloudTasks": {
      "ProjectId": "my-project-dev",
      "LocationId": "europe-west1",
      "Channels": { "default": "my-default", "reports": "my-reports" },
      "DispatcherUrl": "http://host.docker.internal:5028/internal/queue/dispatch",
      "ServiceAccountEmail": "local-dev@my-project-dev.iam.gserviceaccount.com",
      "EmulatorEndpoint": "127.0.0.1:8123",
      "OidcIssuer": "http://127.0.0.1:8980"
    }
  }
}
```

Four couplings decide whether this works:

1. **`EmulatorEndpoint` is the master switch.** It points the client at the emulator over an insecure
   channel *and* relaxes the HTTPS-metadata requirement on the dispatch endpoint.
2. **`OidcIssuer` must equal the emulator's `-openid-issuer`.** The emulator does **not** disable JWT
   validation — issuer and audience are still checked in full; only the metadata transport is relaxed.
   Omitting the issuer means tokens are validated against `accounts.google.com` and every delivery
   401s, so this fails at startup rather than at run time.
3. **`DispatcherUrl` must be reachable from inside the container** — `host.docker.internal`, not
   `localhost`, because the emulator calls back into your app.
4. **Queues are created for you.** Each channel's queue is created on the emulator as the host
   starts, so `Channels` is the only place they are declared. (Emulator builds without
   queue-creation support log a warning; declare those with `-queue` flags instead.)

That creation runs in the background and retries with capped, jittered backoff, so an emulator
coming up alongside the host — or started minutes later — still gets its queues. **It never fails
the host.** An emulator that stays unreachable is logged, at Error once it stops looking like a
container still starting, and the first enqueue names the queue it could not find.

## Gotchas

| Symptom | Cause |
|---|---|
| Every delivery 401s | `OidcIssuer` does not match the token's issuer, or `OidcAudience` does not match what the caller requested. |
| Tasks retry forever, app never sees them | The route and `DispatcherUrl`'s path disagree — caught at startup — or the service rejects the caller before the app runs. |
| `PermissionDenied` on enqueue | Missing `cloudtasks.enqueuer`, or missing `iam.serviceAccountUser` on the invoker SA. |
| Tasks stop retrying earlier than the policy says | The queue's `retry_config` is overriding the application's `[QueuePolicy]`. |
| A channel works locally but not in production | The channel is mapped in `Channels` but no queue exists for it in that project/location. |
| `NotFound` on every local enqueue | The emulator was unreachable when the host started, so its queues do not exist yet. The log says so; starting the emulator is enough. |
| Over SQL: messages sit in the outbox on Cloud Run | The relay timer is CPU-throttled between requests. Turn `Relay:RunInBackground` off, map `MapQueueRelay`, and call it from Cloud Scheduler. |
