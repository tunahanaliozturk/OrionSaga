# OrionSaga Roadmap

Where OrionSaga has been and where it might go next.

Current release: **0.5.0**. An in-process saga orchestrator that runs an ordered set of steps over a
shared context and compensates the completed steps in reverse order when one fails, cancels, or
overruns its timeout, with opt-in per-step retry, compensation retry, a bounded rollback budget, and
composition and control flow: conditional steps, sub-sagas, and parallel step groups.

This document records what has shipped and lays out a forward plan. The shipped section is fact. The
forward plan is a direction with rough version targets, not a contract: items move, merge, and get
dropped as real usage shows what matters. If you want something here, open an issue and describe the
workload that needs it; demand is what moves an idea forward.

For the capability baseline see [FEATURES.md](FEATURES.md); the 0.2.0, 0.2.1, 0.3.0, 0.4.0, and 0.5.0 additions are in the Released section below and in the [changelog](../CHANGELOG.md).

---

## Guiding principles

OrionSaga is deliberately small. It is an in-process orchestrator with one job: run a few steps and
unwind them cleanly when one fails. Any addition is weighed against that focus.

1. **Stay dependency-light.** The core leans on nothing but the DI abstractions. Features that pull in
   heavy dependencies belong in optional companion packages, not the core.
2. **Do not pretend to be a durable workflow engine.** OrionSaga coordinates one operation inside one
   process. Durable, resumable, cross-process workflow is a different product with different
   trade-offs, and conflating the two would hurt both. Where durability is explored below it is scoped
   as an opt-in store behind a seam, never baked into the in-process executor.
3. **Keep the happy path allocation-light.** The forward run is the part consumers pay for on every
   call; instrumentation and ergonomics must not tax it.

---

## Released

What already ships, newest first. See [CHANGELOG.md](../CHANGELOG.md) for the full history.

### 0.5.0

- **Conditional steps.** A step can declare a `condition` predicate (on `AddStep` / `AddResultStep` /
  `SagaStep`, exposed as `SagaStep.Condition`). When it evaluates false against the context just before
  the step would run, the step is skipped: its forward action does not run, it is not counted as
  completed, and it is never compensated. This replaces branching inside the forward delegate, so a
  skip is visible in `SagaResult.StepsSkipped` and the new `ISagaObserver.OnStepSkipped` notification
  (a default no-op, so existing observers are unaffected) rather than being invisible.
- **Step grouping / sub-sagas.** `SagaBuilder.AddSubSaga(name, configure)` composes a named sub-saga
  over the same context into the parent. The sub-saga's steps are flattened inline, each prefixed as
  `"{name}/{step}"`, so they become ordinary steps of the parent and participate in the parent's single
  ordered run and single reverse-order rollback. A failure anywhere after the sub-saga unwinds the
  sub-saga's completed steps along with the rest, newest-first, across the whole composed saga.
- **Parallel step groups.** `SagaBuilder.AddParallelGroup(name, configure, condition)` runs a set of
  independent member steps concurrently within one stage. Strictly opt-in; the default stays
  sequential. The group is composed onto a single slot in the parent's flat list, so the parent's
  ordering and overall reverse-order rollback are preserved. On any member failure the group waits for
  the in-flight members to settle, compensates every member that completed (in reverse of their
  declaration order in the group), and surfaces the failure so the earlier stages roll back; the
  faulted member is not compensated. A completed group later rolled back unwinds its members in the
  same reverse declaration order. Each member's per-step timeout and forward retry are honoured.
- All additions are opt-in and additive: with no conditional step, sub-saga, or parallel group, the
  forward run, compensation, `SagaResult`, and observer behaviour are unchanged from 0.4.0, and the
  no-observer / no-condition happy path stays allocation-light.

### 0.4.0

- **Per-step forward retry.** An opt-in `RetryPolicy` (attempts plus constant or exponential backoff)
  declared per step via `forwardRetry` on `AddStep` / `AddResultStep` / `SagaStep`, so a transient
  forward fault is retried before the step is treated as a failure and a flaky call does not force a
  full rollback. Cancellation and per-step timeouts are terminal and never retried; a per-step timeout,
  when set, bounds each attempt individually, and backoff waits honour the step's cancellation token.
- **Compensation retry.** The same `RetryPolicy` for a step's compensation, declared per step via
  `compensationRetry` or saga-wide via `WithCompensationRetry` (per-step overrides saga-wide), since a
  failed undo is the most expensive outcome. A transient compensation fault is retried before being
  recorded as a `CompensationFailure`.
- **Configurable rollback budget.** `WithRollbackBudget` bounds the whole unwind phase, so a hung
  compensation cannot block forever. When the budget elapses the compensation token is cancelled, the
  remaining steps are recorded as failures, and the run reports `SagaResult.RollbackTimedOut`. With no
  budget set, rollback is unbounded exactly as before.
- All additions are opt-in and additive: with no retry policy and no rollback budget configured the
  forward run, compensation, `SagaResult`, and observer behaviour are unchanged from 0.3.0, and the
  no-observer / no-retry happy path stays allocation-light.

### 0.3.0

- **Typed step results.** A new generic `SagaBuilder.AddResultStep<TResult>` method takes a forward
  action that returns a value plus an `apply` callback that writes that value into the shared context,
  so a step hands its result to the next step instead of mutating shared state by hand. The typed step
  is adapted onto the existing untyped step, so ordering, compensation, per-step timeouts, and
  reporting are identical. It is a distinct method, not an `AddStep` overload, so it cannot capture an
  existing `AddStep` call. The untyped `AddStep`, `SagaStep`, and `SagaBuilder` surface is unchanged.
- **Run-level summary on `SagaResult`.** New `StepsCompleted` and `StepsCompensated` counts report how
  many forward actions completed and how many completed steps compensated cleanly, so callers do not
  reconstruct them from an observer. The step that ends the saga is not counted as completed, and a
  compensation that itself threw is recorded in `CompensationFailures` rather than counted as
  compensated.
- **Richer observer payloads.** `ISagaObserver` gains overloads that carry each step's one-based
  ordinal and measured duration alongside the name. They are default interface methods that forward to
  the original name-only methods, so an existing observer is unaffected; override an overload to
  receive the ordinal and duration. The no-observer happy path stays allocation-light: with no observer
  registered the executor captures no timing and makes no notification call.

### 0.2.1

- **Allocation-light notifications.** `Saga.RunAsync` no longer allocates a per-step closure to
  dispatch observer notifications, and it skips the notification path entirely when no observer is
  registered. On the no-observer happy path this cuts per-run orchestration allocation substantially.
  Behaviour, public API, and observer call ordering are unchanged.

### 0.2.0

- **Per-step timeouts.** A step can declare a maximum forward-action duration via the `timeout`
  parameter on `SagaBuilder.AddStep` and the `SagaStep` constructor (`SagaStep.Timeout`). When a step
  overruns its budget the linked `CancellationToken` is cancelled; a step that observes the token stops
  and the saga rolls back the completed steps, reporting the timeout as the cause. The cancellation is
  cooperative, not a hard kill: a step action that ignores its `CancellationToken` can run past the
  deadline, so step bodies must honour the token to get the timeout guarantee. The deadline is honoured
  alongside any caller-supplied `CancellationToken` through that linked token, so external cancellation
  still works.
- **Distinct cancellation outcome.** A cancellation (caller token or per-step timeout) is reported as
  `SagaOutcome.Cancelled`, separate from a business `Failed` and from `TimedOut`, so callers can tell
  an operator or timeout cancellation apart from a step that genuinely faulted. Exposed through the new
  `SagaOutcome` enum and `SagaResult.Outcome`, with `Cancelled`, `Failed`, and `TimedOut` convenience
  properties.

### 0.1.0

- Initial release: ordered steps with paired compensations, automatic reverse compensation on
  failure, `SagaResult` reporting, the `ISagaObserver` hook, `SagaDiagnostics` counters, and the
  `AddOrionSaga()` DI extension.

---

## Next

A rough plan with version targets. Dates are estimates, not commitments, and earlier items gate later
ones.

Composition and control flow (conditional steps, sub-sagas, and parallel step groups) shipped in
0.5.0; see the Released section above.

### Observability and tooling, alongside the above

- **Activity / tracing integration.** Emit a `System.Diagnostics.Activity` span per saga run and per
  step so traces show the orchestration shape next to the existing meter counters.
- **Saga-state inspection.** A read-only view of a run in progress (current step, completed steps,
  what would compensate) for diagnostics and tests, without exposing mutable internals.
- **Test helper package.** Recording observers and assertion helpers for verifying step and
  compensation order without hand-rolling them in each test.

### Companion packages (opt-in, outside the core)

These extend OrionSaga without adding dependencies to the in-process core. Each would ship as a
separate package and is gated on clear demand.

- **Reliable side effects via an outbox.** Bridge a saga's effects to a transactional outbox so a
  step's external message is published in the same transaction as its local state change, using
  [OrionPatch](https://github.com/tunahanaliozturk/OrionPatch) as the outbox.
- **Durable saga store.** An optional persistence seam that records step progress so a crashed run can
  be inspected and, where steps are idempotent, resumed. This does not turn the core into a workflow
  engine: the executor stays in-process and the store is an opt-in adapter behind an interface. This
  is the boundary case, and it only moves with concrete, described demand.

---

## Explicitly out of scope

These come up often and are deliberately not planned for the core, to keep the library honest about
what it is:

- **A durable workflow engine baked into the core.** Persistence, if it lands, is an opt-in companion
  behind a seam (see above), never a requirement of the in-process executor.
- **Distributed, cross-service sagas** coordinated over a message broker. OrionSaga is in-process.
- **A scheduler or background host.** OrionSaga runs when you call `RunAsync`; it owns no threads of
  its own.

If your use case needs one of these, that is useful signal. Open an issue and describe it.

---

## How to influence this

- Open an issue describing the workload, not just the feature. "I have N steps across M systems and
  need X" is far more actionable than "please add X".
- Real demand reorders this list. A clear, common need beats a clever but speculative one.
- Small, focused pull requests that fit the principles above are welcome.
