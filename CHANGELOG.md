<!-- markdownlint-disable MD024 -->

# Changelog

All notable changes to OrionSaga are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

[0.1.0]: https://github.com/tunahanaliozturk/OrionSaga/releases/tag/v0.1.0
