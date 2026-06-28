<!-- markdownlint-disable MD024 -->

# Changelog

All notable changes to OrionSaga are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.6.0] - 2026-06-28

### Added

- Activity / tracing integration: the executor now emits a `System.Diagnostics.Activity` span per saga
  run, per step forward action, and per compensation, from a new `ActivitySource` named
  `Moongazing.OrionSaga` (the same name as the `SagaDiagnostics` meter, so one subscription string wires
  up both traces and metrics; the source and its tag/span-name constants are exposed on the new
  `SagaActivitySource` class). The step spans nest under the run span and a parallel group's member spans
  nest under the group's slot span, so a trace shows the orchestration shape. Each span carries the step
  name and one-based ordinal, and an outcome tag (`completed` / `skipped` / `failed` / `cancelled` /
  `timedout` for steps, `compensated` / `failed` / `timedout` for compensations, and the run outcome on
  the run span); span duration reflects how long the action ran. `ActivitySource.StartActivity` returns
  null when no listener is registered, so the no-listener happy path starts no `Activity`, sets no tags,
  and allocates nothing for tracing; the existing no-observer happy path is otherwise byte-for-byte as in
  0.5.0.
- Saga-state inspection: a read-only `SagaRunSnapshot` of a run in progress, handed to the new default
  no-op `ISagaObserver.OnProgress(SagaRunSnapshot)` notification before each step runs and once when the
  run completes. The snapshot reports the current step, the completed steps (in run order, which is what
  would compensate), the pending steps, and `WouldCompensate` (the completed steps in reverse unwind
  order), each as an immutable `StepReference` of name and ordinal. It copies the names and ordinals it
  reports into its own arrays at the moment it is taken, so it exposes none of the executor's mutable
  internals and a snapshot held past the run still reads the state it captured. `OnProgress` is a default
  interface method, so existing observers are unaffected, and like every notification it is gated on a
  real observer being registered: the no-observer path builds no snapshot and a fault it raises is
  swallowed.

### Notes

- All additions are opt-in and additive. With no `ActivityListener` and no observer registered, the
  forward run, compensation, `SagaResult`, observer behaviour, and per-run allocation are unchanged from
  0.5.0. A separate test-helper package (recording observers and assertion helpers as their own package)
  remains planned and is not part of this release; the recording observers and the captured-listener
  harness used to verify this milestone live in the test project for now.

## [0.5.0] - 2026-06-27

### Added

- Conditional steps: a step can declare a `condition` predicate via the new
  `SagaBuilder.AddConditionalStep` / `AddConditionalResultStep` methods and the `SagaStep` constructor
  (exposed as `SagaStep.Condition`). When the predicate evaluates false against the context just before
  the step would run, the step is skipped: its forward action does not run, it is not counted as
  completed, and it is never compensated. A predicate that throws is treated exactly like a forward
  fault: forward progress stops, the completed steps roll back, and the run reports failure on that
  step. This replaces branching inside the forward delegate, so a skip is reflected in the result and
  observer payloads instead of being invisible. A new `SagaResult.StepsSkipped` count and a new
  `ISagaObserver.OnStepSkipped` notification (a default no-op so existing observers are unaffected)
  report skips. The conditional capability is on distinct methods rather than an extra parameter on
  `AddStep` / `AddResultStep`, so those existing overloads keep their original signatures and every
  prior call site compiles and resolves unchanged.
- Step grouping / sub-sagas: `SagaBuilder.AddSubSaga(name, configure)` composes a named sub-saga over
  the same context into the parent. The sub-saga's steps are flattened inline at the call site, each
  prefixed as `"{name}/{step}"`, so they become ordinary steps of the parent. They participate in the
  parent's single ordered run and its single reverse-order rollback: a failure anywhere after the
  sub-saga unwinds the sub-saga's completed steps along with the rest, newest-first, across the whole
  composed saga, with no nesting and no special cases in the executor.
- Parallel step groups: `SagaBuilder.AddParallelGroup(name, configure, condition)` runs a set of
  independent member steps concurrently within one stage. The group is strictly opt-in; the default
  stays sequential. The group is composed onto a single step occupying one slot in the parent's flat
  list, so the parent's ordering and overall reverse-order rollback are preserved. On any member
  failure the group waits for the in-flight members to settle and then surfaces the failure so the
  parent rolls back; the faulted member is not compensated. Compensation of the group's completed
  members runs through the saga's own per-step compensation routine, in reverse of their declaration
  order: each member honours per-step compensation retry, emits the same observer notifications as a
  sequential step, and any compensation that itself fails is recorded in `SagaResult.CompensationFailures`
  and surfaced to the observer. This holds both when a member faults and when the group completed but a
  later stage fails. Each member's own per-step timeout, forward retry, and `condition` are honoured: a
  member whose condition is false is skipped (and never compensated) while the rest of the group still
  runs. An optional group-level `condition` skips the whole group.

### Notes

- All additions are opt-in and additive. With no conditional step, sub-saga, or parallel group used,
  the forward run, compensation, `SagaResult`, and observer behaviour are byte-for-byte as in 0.4.0,
  and the no-observer happy path stays allocation-light: an ordinary step pays nothing for the feature
  (it carries no condition and no group), and the existing `AddStep` / `AddResultStep` / retry /
  rollback-budget / observer behaviour is unchanged. The conditional surface is on the new
  `AddConditionalStep` / `AddConditionalResultStep` methods specifically so the existing `AddStep` /
  `AddResultStep` overloads keep their pre-composition signatures.

## [0.4.0] - 2026-06-27

### Added

- Per-step forward retry: a step can declare a `RetryPolicy` via the new `forwardRetry` parameter on
  `SagaBuilder.AddStep` / `AddResultStep` and the `SagaStep` constructor (exposed as
  `SagaStep.ForwardRetry`). When set, a transient forward fault is retried with backoff up to the
  policy's attempt count before the step is treated as failed, so a flaky call does not force a full
  rollback. A cancellation or a per-step timeout is terminal, not transient, and is never retried.
  When a per-step `timeout` is also set it bounds each attempt individually; the backoff waits honour
  the step's cancellation token.
- Compensation retry: a step can declare a `RetryPolicy` for its compensation via the new
  `compensationRetry` parameter (exposed as `SagaStep.CompensationRetry`), and a saga-wide default can
  be set with `SagaBuilder.WithCompensationRetry`. A transient compensation fault is retried before
  being recorded as a `CompensationFailure`, since a failed undo is the most expensive outcome. A
  per-step policy overrides the saga-wide one; with neither set, compensation runs once as before.
- Configurable rollback budget: `SagaBuilder.WithRollbackBudget` bounds the whole unwind phase to a
  maximum duration. Previously rollback ran with a non-cancelled token, so a hung compensation could
  block forever; with a budget set, the token passed to compensations is cancelled once the budget
  elapses and the run reports `SagaResult.RollbackTimedOut`. Compensations cut short or never reached
  are recorded as `CompensationFailure`s. With no budget set the prior unbounded behaviour is unchanged.
- `RetryPolicy` value type (`maxAttempts`, `baseDelay`, `RetryBackoff` of `Constant` or `Exponential`)
  and `SagaResult.RollbackTimedOut`.

### Notes

- All additions are opt-in and additive. With no retry policy and no rollback budget configured, the
  forward run, compensation, `SagaResult`, and observer behaviour are byte-for-byte as in 0.3.0, and
  the no-observer / no-retry happy path stays allocation-light: the backoff delayer is only touched on
  a retry path and the rollback budget allocates no cancellation source when unset.

## [0.3.0] - 2026-06-22

### Added

- Typed step results: a new generic `SagaBuilder.AddResultStep<TResult>` method takes a forward
  action that returns a value plus an `apply` callback that flows the value into the shared context,
  so a step can hand its result to the next step instead of mutating shared state by hand. The typed
  step is adapted onto the existing untyped step shape, so ordering, compensation, per-step timeouts,
  and reporting behave exactly as for an untyped step. It is intentionally a distinct method rather
  than an `AddStep` overload: a generic overload could capture an existing
  `AddStep(name, forward, compensate)` call whose forward returns a `Task<T>` and silently rebind its
  compensation to `apply`, so the typed surface is kept off the `AddStep` candidate set entirely. The
  existing `SagaBuilder`, `SagaStep`, and untyped `AddStep` surface is unchanged.
- Run-level summary on `SagaResult`: new `StepsCompleted` and `StepsCompensated` counts so callers
  can read how many forward actions completed and how many completed steps were compensated cleanly
  without reconstructing them from an observer. On success `StepsCompleted` equals the step count and
  `StepsCompensated` is zero; the step that ends the saga is not counted as completed, and a
  compensation that itself threw is recorded in `CompensationFailures` rather than counted as
  compensated.
- Richer observer payloads: `ISagaObserver` gains overloads that carry the step's one-based `ordinal`
  and measured `duration` alongside the name (`OnStepCompleted`, `OnStepFailed`, `OnCompensated`,
  `OnCompensationFailed`). They are default interface methods that forward to the original name-only
  methods, so an existing observer keeps compiling and behaving unchanged; override an overload to
  receive the ordinal and duration. The no-observer happy path is unaffected: when no observer is
  registered the executor captures no timing and makes no notification call.

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

[0.6.0]: https://github.com/tunahanaliozturk/OrionSaga/releases/tag/v0.6.0
[0.5.0]: https://github.com/tunahanaliozturk/OrionSaga/releases/tag/v0.5.0
[0.4.0]: https://github.com/tunahanaliozturk/OrionSaga/releases/tag/v0.4.0
[0.3.0]: https://github.com/tunahanaliozturk/OrionSaga/releases/tag/v0.3.0
[0.2.0]: https://github.com/tunahanaliozturk/OrionSaga/releases/tag/v0.2.0
[0.1.0]: https://github.com/tunahanaliozturk/OrionSaga/releases/tag/v0.1.0
