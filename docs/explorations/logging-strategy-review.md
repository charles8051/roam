# Logging strategy review — the in-flight "Logging and Diagnostics Strategy" ADR

**Status:** Review → **decided and actioned (2026-06-13).** Originally a survey of
the unmerged logging ADR; the decision has since been made and the reframe
executed on this branch.

> **Outcome:** roam is a **CLI**, and `ILogger<T>` DI is adopted for **consistency
> with the standard .NET idiom** (the decisive rationale — not embeddability). The logging ADR was cherry-picked into `main` and
> **reframed** accordingly: `System.Diagnostics.Metrics` is **dropped from v0**
> (no collector in a one-shot CLI), the ADR is recast as a `RoamLog` →
> `ILogger<T>` **migration** (not a greenfield), and `logging.md` /
> `packaging.md` / `README.md` / the implementation plan are reconciled to match.
> The original author's branch (`claude/review-roam-skeleton`) is left untouched
> for comparison. The sections below are the review that produced these calls.

## What this reviews

Branch `claude/review-roam-skeleton-8W4hz` (unmerged) adds:

- `docs/adr/0001-logging-and-diagnostics-strategy.md` (445 lines), and
- a rewrite of `docs/logging.md`'s level + JSONL sections,

prescribing a `Microsoft.Extensions.Logging` (M.E.L.) stack: `ILogger<T>` via
constructor injection in every subsystem, `[LoggerMessage]` source generation
for hot paths, and `System.Diagnostics.Metrics` instrumentation. It is marked
**Accepted**.

(It also claims ADR number 0001, which collided with the agent-first ADR; that
was resolved by renumbering the agent-first one to
ADR-0002 (in-flight on branch `docs/agent-first-usability`). This review is about the
logging ADR's *content*, independent of the number.)

## Verdict

A well-crafted, idiomatic .NET logging standard — internally consistent, good
ADR hygiene (real alternatives, sensible deferrals), and a clean
producer/renderer split between the ADR and `logging.md`. **But it describes a
logging system roam does not have, and books it as Accepted.** Recommendation:
do not merge as-is.

## The core mismatch

| The ADR prescribes | The shipped code does |
|---|---|
| `ILogger<T>` injected per subsystem | static `RoamLog.Event(name, message, data)` façade |
| M.E.L. + six severity levels | hand-rolled JSONL writer; `--verbose` on/off, no levels |
| `[LoggerMessage]` source-gen on hot paths | plain `RoamLog.Event(...)` calls |
| `System.Diagnostics.Metrics` instruments | none |

[`RoamLog`](../../src/Roam/RoamLog.cs) is a static façade: `Event(name, message,
dict)` serializes `{Timestamp, Event, Message, Data}` to the `--log-file` JSONL
and mirrors `debug <name>: <message>` to stderr under `--verbose`. No M.E.L., no
DI, no levels, no metrics. It is used pervasively (`SyncEngine`, `RoamCommands`,
`ProcessRunner`, `Program`).

The ADR (and its `logging.md` edits) **never mention `RoamLog`.** Consequences:

- Its headline "Positive" — *"logging patterns are consistent from the first
  phase instead of being retrofitted"* — is contradicted by the code. A pattern
  already exists; adopting the ADR **is** a from-scratch retrofit of the whole
  logging layer.
- The branch name (`review-roam-skeleton`) is the tell: it reads as a review of
  the skeleton / implementation-contract, written without reconciling against the
  `RoamLog` that actually shipped.

## Pre-existing drift (independent of this ADR)

`docs/logging.md` on `main` *already* overclaimed: it said "internally roam uses
Microsoft.Extensions.Logging with these levels," and documented a JSONL schema
(`ts` / `level` / `step` / `msg` ...) that does not match `RoamLog`'s actual
output (`Timestamp` / `Event` / `Message` / `Data`). This drift predates the ADR.
A surgical correction landed alongside this memo (describe `RoamLog` as today's
reality, mark M.E.L. as proposed). Note: that correction touches the same
`logging.md` sections the ADR branch rewrites, so the two will conflict on
merge — which is the right forcing function for the decision below.

## Keep vs. cut (if right-sized for roam)

- **Keep (cheap, correct):** the level taxonomy (ADR §3) and the
  structured-template discipline. `RoamLog` already emits structured events, so
  formalizing a level field + the verbosity×level matrix is a small, real win.
  `[LoggerMessage]` on the true hot paths (SFTP per-file loop, readiness poll) is
  correct best practice *if* the layer moves to M.E.L.
- **Cut / defer (mis-sized for a one-shot CLI):** `System.Diagnostics.Metrics`
  (ADR §6). It defines ~10 Meters/Counters/Histograms that **nothing collects** —
  the ADR states it "ships no default exporter" — in a process that runs for
  seconds and exits. The ADR's own Alternatives *deferred* `ActivitySource`
  because "roam is a one-shot CLI"; Metrics has the identical
  no-collector-in-a-CLI-lifecycle property but is accepted. And it serves
  **neither** consumer: humans have no scraper attached, agents read text/JSON.
  Roadmap #6 (return sync stats, print in `--verbose` / JSONL) is the right-sized
  observability.

## The question that decides it

**Is roam staying a CLI, or becoming an embeddable library?**

- **CLI:** `RoamLog` is arguably the *correct* altitude. The ADR over-builds for
  a six-step pipeline tool, and the Metrics apparatus is dead weight.
- **Embeddable library:** the ADR's strongest justification — "a future
  web-of-tools host plugs in its own `ILogger` sink," "embedders" — pays off, and
  `ILogger<T>` + DI is the right foundation, worth the retrofit.

The agent-first direction (ADR-0002)
makes "embeddable" semi-plausible (an agent harness wrapping roam), so the ADR is
not a crazy bet — but it is a *bet*, and should not be booked as "Accepted v0"
until the bet is made.

## Relationship to ADR-0002 and roadmap #6

The producer/consumer split is clean in principle (the logging ADR *produces*
records; ADR-0002 / `roam diag` *consumes* them). But on the observability
*mechanism* they diverge: the logging ADR's OpenTelemetry-Metrics path vs. the
stats-in-output path of ADR-0002 and roadmap #6. For roam's real consumers,
stats-in-output wins; Metrics is a "v1 if an embed host ever exists" feature. The
repo should not ship two competing observability stories — reconcile before
either is "the standard."

## Recommendation

1. **Do not merge the logging ADR as Accepted.** Either:
   - **(a)** Demote to **Proposed**; adopt the cheap/correct parts now (a level
     field + structured discipline on `RoamLog`); gate the M.E.L. / DI / Metrics
     migration behind the CLI-vs-library decision; or
   - **(b)** Reframe it honestly: describe `RoamLog` as today's façade, define
     `ILogger<T>` / levels / metrics as the *target* with an explicit migration
     plan and a trigger (e.g. "when roam gains its first library embedder").
2. **Fix the `logging.md` ↔ `RoamLog` drift first** (done surgically alongside
   this memo; full reconciliation belongs to whoever resolves the ADR).
3. **Reconcile Metrics-vs-stats** with ADR-0002 / roadmap #6 so there is one
   observability story.
4. **Drop the `System.Diagnostics.Metrics` apparatus from v0** regardless of
   (a)/(b).

## Not done here

This work did **not** edit the original ADR branch (`claude/review-roam-skeleton`).
The ADR was cherry-picked into `main` (the pre-reframe commit is the author's
original, preserved in history) and reframed on top; the superseded branch was
then deleted.
