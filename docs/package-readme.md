# Spinneret

**Spin up your .NET app.**

A *spinneret* is the organ a spider uses to spin its web — different silks for different jobs, woven together. Spinneret is that organ for .NET: small, sharply focused packages that spin the threads a serious application hangs from.

| Package | What it spins |
|---|---|
| `Spinneret.Functional` | `Result` / `Either` with `Match`/`Map`/`Bind` combinators and an awaitable `TaskResult` |
| `Spinneret.Mediator` | Request dispatch with declarative, tag-invalidated caching |
| `Spinneret.Parsing` | Parse-don't-validate boundaries with localizable property errors |
| `Spinneret.Queue` | Durable command queue — the app owns the retry policy per command |
| `Spinneret.Queue.Gcp` | Google Cloud Tasks transport with OIDC-authenticated dispatch |
| `Spinneret.Queue.Mssql` | SQL Server transport with transactional enqueue and polling workers |
| `Spinneret.Scheduler` | Recurring jobs declared in code, installed idempotently at startup |
| `Spinneret.Scheduler.Gcp` | Firestore-backed scheduling with transactional dispatch |
| `Spinneret.Scheduler.Mssql` | SQL Server-backed scheduling with transactional dispatch |
| `Spinneret.ViewModel` | MVVM for Blazor: typed two-way bindings with validation state |
| `Spinneret.View` | Blazor views that resolve their view model from DI |

Errors are values. Boundaries parse, they don't validate. Your app owns its behavior — a typo fails the host at boot, not a delivery at 2 a.m. Infrastructure is a seam.

Full documentation, examples, and the rest of the web: **[github.com/spinneret-net/spinneret](https://github.com/spinneret-net/spinneret)**

MIT licensed. Spin freely.
