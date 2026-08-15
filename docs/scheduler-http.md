# Spinneret.Scheduler.Http

An authorized endpoint that runs one scheduler sweep per request.

The counterpart to `AddSchedulerSweeper()`: use it where an external scheduler owns the clock. That is
the normal arrangement on a host that scales to zero, which has no thread of its own to tick — and
where an inbound request conveniently both wakes the service and triggers the sweep.

It knows nothing about where jobs are stored. Pair it with either storage package.

## Install

```sh
dotnet add package Spinneret.Scheduler.Http
```

```csharp
services.AddGcpQueue(configuration, typeof(SyncCustomer).Assembly);
services.AddFirestoreScheduler(configuration);

app.UseAuthentication();
app.UseAuthorization();
app.MapSchedulerSweep(OidcAuthSetup.PolicyName);   // using Spinneret.Queue.Gcp;
```

```csharp
// Signature
app.MapSchedulerSweep(
    authorizationPolicy: "...",                       // required
    pattern: "/internal/scheduler/sweep");   // optional, this is the default
```

## Requires

| | |
|---|---|
| **A storage provider** | `AddFirestoreScheduler` or `AddMssqlScheduler`. Mapping without one throws — a sweep endpoint with nothing to sweep looks healthy while doing nothing. |
| **An authorization policy** | Required, not defaulted. Passing a blank one throws. |
| **Auth middleware** | `UseAuthentication()` / `UseAuthorization()` before endpoints. |

### Why the policy is required

The sweep dispatches **every due job**, so an unauthenticated endpoint hands that to anyone who finds
the URL. This package cannot know what authenticates your trigger, and guessing wrong would silently
leave the route open — so it makes you say.

On Cloud Tasks, pass `OidcAuthSetup.PolicyName` to reuse the queue's OIDC scheme, which `AddGcpQueue`
already registered. Any other host passes its own policy name.

## Responses

| Status | Meaning |
|---|---|
| `200` | The sweep ran. The body reports what it did: `{"jobsDispatched":3}`. |
| `401` / `403` | The caller did not satisfy the authorization policy — the first thing a misconfigured cron returns. |
| `500` | The sweep threw. Deliberate: a non-success response is what makes an external scheduler retry the tick. |

Per-job failures do not surface here — the sweep handles those internally, dead-lettering the
occurrence and continuing. A 500 means the pass itself could not run.

## Infrastructure

A cron that calls the endpoint. On GCP that is a Cloud Scheduler job minting an OIDC token with the
same audience the queue's dispatch endpoint validates:

```hcl
resource "google_cloud_scheduler_job" "scheduler_dispatch" {
  name      = "scheduler-dispatch"
  region    = "europe-west1"
  schedule  = "*/5 * * * *"
  time_zone = "Etc/UTC"

  http_target {
    http_method = "POST"
    uri         = "https://worker.example.com/internal/scheduler/sweep"

    oidc_token {
      service_account_email = google_service_account.cloud_tasks_invoker.email
      audience              = "https://worker.example.com/internal/queue/dispatch"
    }
  }
}
```

Two things to get right:

- **The `audience` must be whatever the queue's OIDC policy validates** — that is
  `Queue:Gcp:OidcAudience`, or `Queue:Gcp:DispatcherUrl` when you have not set an audience explicitly
  (the example above shows the latter). It is *not* the scheduler URL. Set an audience explicitly,
  copy this example verbatim, and every tick returns 401.
- **The Cloud Scheduler service agent needs `roles/iam.serviceAccountTokenCreator`** on the invoker
  service account, so it can mint that token:

```hcl
resource "google_service_account_iam_member" "scheduler_can_mint_tokens" {
  service_account_id = google_service_account.cloud_tasks_invoker.name
  role               = "roles/iam.serviceAccountTokenCreator"
  member             = "serviceAccount:service-${data.google_project.current.number}@gcp-sa-cloudscheduler.iam.gserviceaccount.com"
}
```

The `uri` path must match the `pattern` you mapped.

## Choosing the interval

The cron's cadence is what bounds how late a job runs, so it plays the role `Scheduler:Sweeper:SweepInterval`
plays for the timer. A schedule slot finer than the cron's period is reached on the following tick.

It is also the failure backoff: a provider that cannot make progress returns rather than spinning, and
the next tick is what throttles the retry.

Worth knowing on GCP: the free tier allows three Cloud Scheduler jobs. Because every scheduled job in
the app rides this one sweep, one cron covers all of them — do not add a cron per job.

## Gotchas

- **Nothing dispatches if you map neither trigger.** The storage packages register the sweep *engine*
  only; both triggers are opt-in, and a scheduler with no trigger is silent rather than loud.
- **Do not map this and `AddSchedulerSweeper()` on the same host** unless you mean it. Both would drive
  the same sweep — and unlike two timer ticks, an inbound request can land *during* a timer sweep, so
  the two genuinely overlap. That is safe (sweeps already race safely across hosts) but you are paying
  twice for one cadence.
- **The route is excluded from API description**, so it will not appear in Swagger.
- **A 500 is retried by the caller, not by the app.** If your cron does not retry, a transient failure
  waits for the next tick.
