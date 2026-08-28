# Architecture Decision Records

Numbered, cross-cutting decisions for roam — the horizontal choices no single
feature owns and many parts of the tool cite (the debugger stance, the
provisioning boundary, agent-first usability, logging / transport / state
contracts).

One immutable, status-lifecycled decision per file: `NNNN-<slug>.md`, status
`Proposed` -> `Accepted` -> `Superseded by ADR-XXXX`. The number is a stable
citation handle (`see ADR-0002`); **assign it at merge, not at authoring**
(draft under a slug, number it when it lands) so parallel branches never
collide. This index already reflects one such near-collision: two branches
independently authored an `ADR-0001`, so the agent-first ADR took **0002**.

Most roam design docs are *not* ADRs — they live flat in `docs/` as living
specs (`state.md`, `transport.md`, `readiness.md`, ...). Reach for a numbered
ADR only when a decision is genuinely horizontal. This matches the
workspace-wide docs convention in the root `CLAUDE.md`.

## Index

- [ADR-0001](0001-logging-and-diagnostics-strategy.md) — Logging and Diagnostics
  Strategy: `ILogger<T>` discipline, `[LoggerMessage]` hot paths, and the
  log-level taxonomy — the *producer* side of diagnostics. Adopted as the target
  standard and reframed for roam-as-CLI (a `RoamLog` -> `ILogger<T>` migration;
  `System.Diagnostics.Metrics` deferred). **Accepted.**
- [ADR-0002](0002-agent-first-usability.md) — Agent-first usability: treat
  agents as first-class consumers; prioritize machine-consumable diagnostics
  (a fetchable log/dump/trace bundle, `--json` output) over interactive
  debugger attach for them — the *consumer* side. **Accepted.**
- [roamfile v2](roamfile-v2-schema.md) — remove the ceremony from `roamfile.yaml`:
  drop `source:` (never a host), merge `run:` into `deploy:` (one lifecycle spec),
  drop dead `solution:`/`project:` and the single-valued `debug:` fields, and give
  `process-name` the default the schema already documents so readiness stops
  silently skipping. **Proposed** — unnumbered draft.
