<!-- markdownlint-disable MD024 -->

# Changelog

All notable changes to OrionSaga are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.2.1] - 2026-06-20

### Performance

- `Saga.RunAsync` no longer allocates a per-step closure to dispatch observer notifications, and it
  skips the notification path entirely when no observer is registered (the default null observer).
  On the no-observer happy path this cuts per-run orchestration allocation substantially. Behaviour,
  public API, and observer call ordering are unchanged.

## [0.2.0] - 2026-06-19

### Added

- Per-step timeouts: a step can declare a maximum forward-action duration via the new `timeout`
  parameter on `SagaBuilder.AddStep` and the `SagaStep` constructor (exposed as `SagaStep.Timeout`).
  When a step overruns its budget it is cancelled and the saga rolls back the completed steps,
  reporting the timeout as the cause. The per-step deadline is honoured alongside any caller-supplied
  `CancellationToken` through a linked token, so external cancellation still works.
- `SagaOutcome` enum and `SagaResult.Outcome`, with `Cancelled`, `Failed`, and `TimedOut`
  convenience properties.

### Fixed

- A cancellation (caller token or per-step timeout) is now reported as a distinct
  `SagaOutcome.Cancelled` outcome instead of being conflated with a generic failure, so callers can
  tell an operator or timeout cancellation apart from a business failure. `SagaResult.Succeeded` and
  the existing `FailedStep` / `Failure` / `CompensationFailures` / `RolledBackCleanly` members are
  unchanged.

## [0.1.0] - 2026-06-15

### Added

Initial release. In-process saga orchestration.

- `Saga<TContext>` / `SagaBuilder<TContext>`: declare ordered steps, each with a forward action and
  a compensation; run them over a shared context.
- Automatic reverse compensation on failure: the failing step is not compensated, every completed
  step is, in reverse order.
- `SagaResult`: success, the failed step and exception, compensation failures, and
  `RolledBackCleanly`.
- `ISagaObserver`: fault-safe step-completed / failed / compensated / compensation-failed hook.
- `SagaDiagnostics`: `Moongazing.OrionSaga` meter with run, step, and compensation counters.
- `AddOrionSaga()` DI extension registering the shared diagnostics.

### Tests

10 tests across success ordering, reverse compensation, the failing step not being compensated,
compensation-failure isolation, empty saga, cancellation rollback, observer notifications and fault
isolation, and registration.

[0.2.0]: https://github.com/tunahanaliozturk/OrionSaga/releases/tag/v0.2.0
[0.1.0]: https://github.com/tunahanaliozturk/OrionSaga/releases/tag/v0.1.0
