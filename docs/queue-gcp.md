# Spinneret.Queue.Gcp

Google Cloud Tasks as the transport. Producers create tasks; Cloud Tasks POSTs each one to an
OIDC-protected endpoint in your app, which runs the handler.

Cloud Tasks is delivery only — it stores nothing you can query — so dead letters need a store of
their own. See [`Spinneret.Queue.Firestore`](queue-firestore.md).

Read [queue.md](queue.md) first for the retry model; this page is the GCP-specific half.

## Install

```sh
dotnet add package Spinneret.Queue.Gcp
```

```csharp
builder.Services.AddGcpQueue(builder.Configuration, typeof(SyncCustomer).Assembly);
builder.Services.AddFirestoreDeadLetters(builder.Configuration);

app.UseAuthentication();
app.UseAuthorization();
app.MapGcpQueueDispatch();   // consumer hosts only
```

## Requires

| | |
|---|---|
| **Dead-letter store** | `AddFirestoreDeadLetters()` or your own `IDeadLetterWriter`. `MapGcpQueueDispatch` refuses to map without one. |
| **Mediator + handlers** | `services.AddMediator(...)`. |
| **`Http.Json.JsonOptions`** | Payloads serialize with ASP.NET Core's **HTTP** JSON options. Producer and consumer must agree on converters or payloads will not round-trip. |
| **Auth middleware** | `UseAuthentication()` / `UseAuthorization()` before endpoints. |
| **Credentials** | Application Default Credentials. No key file is ever read; on Cloud Run that is the service identity. |

> **Producer-only hosts need the full configuration too.** A service that only enqueues still needs
> `DispatcherUrl` and `ServiceAccountEmail`, because both are written into every task it creates —
> they describe where the *consumer* receives it, not where the producer runs.

## Configuration — `Queue:Gcp`

Mandatory. A missing one fails at `AddGcpQueue` and again at host start:

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

```json
{
  "Queue": {
    "Gcp": {
      "ProjectId": "my-project",
      "LocationId": "europe-west1",
      "Channels": { "default": "my-default", "reports": "my-reports" },
      "DispatcherUrl": "https://worker.example.com/internal/queue/dispatch",
      "ServiceAccountEmail": "cloud-tasks-invoker@my-project.iam.gserviceaccount.com"
    }
  }
}
```

In production these usually arrive as environment variables (`Queue__Gcp__ProjectId`, …) so the
values can come from whatever provisions the infrastructure.

## The dispatch endpoint

`MapGcpQueueDispatch()` routes on the **path of `DispatcherUrl`**, so the address Cloud Tasks posts
to and the route the app listens on cannot drift. Passing a pattern that disagrees throws at startup.

That check exists because the mismatch is otherwise near-invisible: every task 404s, and since the
queue's retry configuration is an unlimited backstop, it retries until it expires — with nothing in
the app to show for it.

The endpoint answers `200` when the delivery is finished and `429` with `Retry-After` when the app
wants redelivery.

### The OIDC scheme it registers

`AddGcpQueue` also registers a JwtBearer authentication scheme and an authorization policy, both
named `QueueOIDC` and exposed as `OidcAuthSetup.SchemeName` / `OidcAuthSetup.PolicyName`. The dispatch
endpoint guards itself with that policy.

Use the constant to protect your own internal endpoints with the same Google-minted tokens — it is
what [`Spinneret.Scheduler.Http`](scheduler-http.md) expects you to pass:

```csharp
app.MapPost("/internal/outbox/drain", DrainAsync)
   .RequireAuthorization(OidcAuthSetup.PolicyName);
```

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
| `roles/cloudtasks.enqueuer` | the project / each queue | every identity that enqueues |
| `roles/iam.serviceAccountUser` | the invoker SA | every identity that enqueues |
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
  fresh task, so the consumer needs enqueue rights too — not just the producer.
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
    "Gcp": {
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
   401s, so this now fails at startup rather than at run time.
3. **`DispatcherUrl` must be reachable from inside the container** — `host.docker.internal`, not
   `localhost`, because the emulator calls back into your app.
4. **Queues are created for you.** Each channel's queue is created on the emulator at startup, so
   `Channels` is the only place they are declared. (Emulator builds without queue-creation support
   log a warning; declare those with `-queue` flags instead.)

## Gotchas

| Symptom | Cause |
|---|---|
| Every delivery 401s | `OidcIssuer` does not match the token's issuer, or `OidcAudience` does not match what the caller requested. |
| Tasks retry forever, app never sees them | The route and `DispatcherUrl`'s path disagree — now caught at startup — or the service rejects the caller before the app runs. |
| `PermissionDenied` on enqueue | Missing `cloudtasks.enqueuer`, or missing `iam.serviceAccountUser` on the invoker SA. |
| Tasks stop retrying earlier than the policy says | The queue's `retry_config` is overriding the application's `[QueuePolicy]`. |
| A channel works locally but not in production | The channel is mapped in `Channels` but no queue exists for it in that project/location. |
