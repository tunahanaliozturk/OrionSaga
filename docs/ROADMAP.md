# OrionSaga Roadmap

Where OrionSaga might go, and how you can help shape it.

This document is a set of **ideas under consideration, not promises**. Nothing here has a committed
date or release number. Items move, merge, and get dropped as real usage shows what matters. If you
want something here, open an issue and describe the workload that needs it; demand is what moves an
idea forward.

For what already ships today, see [FEATURES.md](FEATURES.md).

---

## Guiding principles

OrionSaga is deliberately small. It is an in-process orchestrator with one job: run a few steps and
unwind them cleanly when one fails. Any addition is weighed against that focus.

1. **Stay dependency-light.** The library leans on nothing but the DI abstractions today. New
   features that pull in heavy dependencies belong in optional companion packages, not the core.
2. **Do not pretend to be a durable workflow engine.** OrionSaga coordinates one operation inside one
   process. Durable, resumable, cross-process workflow is a different product with different
   trade-offs, and conflating the two would hurt both.
3. **Keep the happy path allocation-light.** The forward run is the part consumers pay for on every
   call; instrumentation and ergonomics must not tax it.

---

## Ideas under consideration

These are grouped by theme. Order does not imply priority.

### Ergonomics

- **Typed step results.** Let a step return a value that flows into the context or the next step,
  rather than mutating shared state by hand.
- **Step grouping / sub-sagas.** Compose a saga out of smaller named sagas so a complex flow reads as
  a few stages rather than one long list.
- **Conditional steps.** A first-class way to skip a step based on the context, instead of branching
  inside the forward delegate.

### Observability

- **Richer observer payloads.** Pass step duration and ordinal alongside the name, so an observer can
  build timing breakdowns without its own bookkeeping.
- **Activity / tracing integration.** Emit a `System.Diagnostics.Activity` span per saga run and per
  step so traces show the orchestration shape next to the existing meter counters.
- **A run-level summary on `SagaResult`.** Surface counts (steps completed, compensated) so callers
  do not have to reconstruct them from an observer.

### Resilience

- **Per-step retry policies.** An opt-in retry-with-backoff around a forward action before it is
  treated as a failure, for transient faults.
- **Compensation retry.** The same idea for compensations, since a failed undo is the most expensive
  outcome.
- **Configurable cancellation budget for rollback.** Today rollback uses a non-cancelled token; a
  bounded timeout for the unwind is worth exploring.

### Testing support

- **A test helper package.** Recording observers and assertion helpers for verifying step and
  compensation order without hand-rolling them in each test.

---

## Explicitly out of scope (for now)

These come up often and are deliberately *not* planned, to keep the library honest about what it is:

- **Durable / persisted sagas** that survive a process restart. This is the defining boundary of the
  library. If you need resumable workflow, a dedicated engine is the right tool.
- **Distributed, cross-service sagas** coordinated over a message broker. OrionSaga is in-process.
- **A scheduler or background host.** OrionSaga runs when you call `RunAsync`; it owns no threads of
  its own.

If your use case needs one of these, that is useful signal. Open an issue and describe it. It will
not change the in-process core, but it helps map where the boundary should sit and whether a separate
companion package makes sense.

---

## How to influence this

- Open an issue describing the workload, not just the feature. "I have N steps across M systems and
  need X" is far more actionable than "please add X".
- Real demand reorders this list. A clear, common need beats a clever but speculative one.
- Small, focused pull requests that fit the principles above are welcome.
</content>
