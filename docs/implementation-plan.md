# v0 implementation plan

**Status:** historical implementation breakdown. Many v0 slices have since
landed and the live engineering truth has moved to
[`platform-readiness.md`](platform-readiness.md) and
[`roadmap.md`](roadmap.md). Keep this file as a decomposition/reference
artifact; do not treat unchecked rows below as authoritative without checking
current code and the readiness tracker.

## How to use this document

- Each slice is sized to land in a day or less.
- Slices list their dependencies, exit artifacts, and the tests that
  prove they work.
- Do not start an `in_progress` slice without moving any other `in_progress`
  slice back to `pending` or forward to `done`.
- When a slice lands, flip its status, tick the acceptance boxes, and
  link the commits/PRs.
- Status values: `todo`, `doing`, `review`, `done`, `blocked`.

## Progress table

| # | Phase | Slice | Owner | Status | Depends on | Notes |
|---|---|---|---|---|---|---|
| 1  | 0. Foundations | Lock roamfile schema doc + JSON Schema + fixture | — | done  | —                 | schema at `docs/roamfile.schema.json`; fixture at `tests/fixtures/SampleApp/roamfile.yaml` |
| 2  | 0. Foundations | Pin exit-code taxonomy doc                         | — | done  | —                 | `docs/exit-codes.md` |
| 3  | 0. Foundations | Pin logging / output format doc                    | — | done  | —                 | `docs/logging.md` |
| 4  | 0. Foundations | Pin preflight doc                                  | — | done  | 1                 | `docs/preflight.md` |
| 5  | 0. Foundations | Pin state-store doc                                | — | done  | —                 | `docs/state.md` |
| 6  | 0. Foundations | Pin CLI doc + golden help text                     | — | done  | 2, 3              | `docs/cli.md` |
| 7  | 0. Foundations | Align configuration.md / debugger.md / readiness.md / paths.md / transport.md / implementation-contract.md to v0 lock | — | done | 1–6 | removed "straw-man" labels, rejected post-v0 keys |
| 7a | 0. Foundations | Adopt ADR 0001 (logging and diagnostics strategy) | — | done | 3 | `docs/adr/0001-logging-and-diagnostics-strategy.md`; reconciled levels into `docs/logging.md` |
| 8  | 1. Scaffolding | Add Roam.slnx projects for Roam, UnitTests, IntegrationTests (done) | — | done | — | already checked in |
| 9  | 1. Scaffolding | Add NuGet refs to `src/Roam` (YamlDotNet, SSH.NET, System.CommandLine, Microsoft.Extensions.Logging, Microsoft.Extensions.Logging.Abstractions, Microsoft.Extensions.Logging.Console, System.Text.Json.Schema) | — | todo | 8 | pin versions in `Directory.Packages.props`; `[LoggerMessage]` source generator ships with `Microsoft.Extensions.Logging.Abstractions` |
| 10 | 1. Scaffolding | Replace `Program.cs` Hello World with `System.CommandLine` root + stubs for `init`, `run`, `attach` | — | todo | 6, 9 | stubs just `return 0` / `return 2` |
| 10a | 1. Scaffolding | Stand up `ILoggerFactory` in CLI host: console formatter for stdout/stderr, JSONL formatter behind `--log-file`, level gating from `-v`/`-q` per ADR 0001 §3 + `logging.md` matrix | — | todo | 7a, 10 | one factory per invocation; subsystems take `ILogger<T>` only |
| 10b | 1. Scaffolding | ~~Define subsystem `Meter`s with the v0 instrument set~~ | — | deferred | — | **Deferred (ADR 0001 §6): metrics dropped from v0 — a one-shot CLI has no collector. Dependent metrics rows (22b, 24a, 29b, 34b, 50b, 50c) deferred with it; user-facing stats come from roadmap #6.** |
| 11 | 1. Scaffolding | Golden-file test for `roam --help`, `roam run --help`, `roam attach --help`, `roam init --help` | — | todo | 10 | asserts against verbatim text in `docs/cli.md` |
| 11a | 1. Scaffolding | Test harness wiring: capture `ILogger` records and `MeterListener` measurements per test for assertion | — | todo | 10a, 10b | xUnit `ITestOutputHelper`-backed logger provider; `MeterListener` records into a per-test bag |
| 12 | 2. Config loader | YAML parse → POCO model (`Roamfile`, `HostSpec`, `ProfileSpec`, `DeploySpec`, `DebugSpec`) | — | todo | 9 | YamlDotNet; no defaults yet |
| 13 | 2. Config loader | JSON Schema validation pass (reject unknown keys, wrong types, missing required) | — | todo | 1, 12 | return first error with line/col; exit `3` |
| 14 | 2. Config loader | V0 explicit-rejection pass (`extends`, `source-sync.mode`, `debug.debugger=netcoredbg`, `debug.editor=rider`, `hosts.<h>.os=windows`, `transport.*`) | — | todo | 13 | friendly "post-v0 feature" messages |
| 15 | 2. Config loader | Default filling pass (`ready-timeout=15`, `ready-interval-ms=500`, `flatten-publish=false`, `debug.install-on-target=false`) | — | todo | 13 | |
| 16 | 2. Config loader | Config discovery (walk up from cwd to `roamfile.yaml`; `--roamfile` override) | — | todo | 10, 12 | |
| 17 | 2. Config loader | Unit tests: valid roamfile, schema-violation, v0-rejection, defaulting, discovery | — | todo | 12–16 | |
| 18 | 3. Host resolver | `ssh -G` invocation + parser for flat key/value output | — | todo | 9 | cross-platform process spawn |
| 19 | 3. Host resolver | Merge explicit host fields with `ssh -G` output; fallback when `ssh` absent | — | todo | 18 | reject ProxyJump in fallback |
| 20 | 3. Host resolver | Unit tests with recorded `ssh -G` fixtures (alias, ProxyJump chain, explicit-only) | — | todo | 19 | |
| 21 | 4. Transport | SSH.NET session factory: key/agent auth, password disabled, ProxyJump via port-forward | — | todo | 19 | ProxyJump implementation is gap-13 spike |
| 22 | 4. Transport | Exception → exit-code mapping (`transport.md` table) + log formatter | — | todo | 3, 21 | |
| 22a | 4. Transport | `[LoggerMessage]` source generators for SSH command channel (per-command Trace, transient-failure Warning) per ADR 0001 §4 | — | todo | 10a, 21 | hot path: every preflight probe + every remote command |
| 22b | 4. Transport | Wire `roam.transport.commands_executed` Counter and `roam.transport.command_latency_ms` Histogram | — | todo | 10b, 21 | tag with host name |
| 23 | 4. Transport | ProxyJump prototype spike against Compose bastion | — | todo | 21, 35 | gate subsequent ProxyJump tests on outcome |
| 24 | 5. Preflight | Implement checks 1–11 from `preflight.md` | — | todo | 13, 21 | one method per check, returns `PreflightResult` |
| 24a | 5. Preflight | Wire `roam.preflight.checks_passed` and `roam.preflight.failures` Counters; emit pass/fail Debug/Error logs per ADR 0001 §8 | — | todo | 10b, 24 | tag with check name |
| 25 | 5. Preflight | Unit + integration tests for each failure message | — | todo | 24, 35 | |
| 26 | 6. SFTP sync spike | Prototype SFTP `ReadDir` metadata round-trip against Compose lab; confirm mtime+size reliability | — | todo | 21, 35 | write-up in `docs/` if findings demand |
| 27 | 7. Sync engine | `git ls-files` source enumeration on source host | — | todo | 21 | SSH-side `git` invocation |
| 28 | 7. Sync engine | Sync planner: diff local manifest vs. remote ReadDir → transfer list + delete list | — | todo | 26, 27 | |
| 29 | 7. Sync engine | Sync executor: `put` transfers, scoped deletes, atomic manifest write | — | todo | 28 | obeys refusal conditions from `paths.md` |
| 29a | 7. Sync engine | `[LoggerMessage]` source generators for the executor inner loop: `LogFileSynced`, `LogFileSkipped`, `LogFileDeleted`, `LogTransientFailure`, `LogPeriodicSyncProgress` (Debug, every 50 files) per ADR 0001 §4 + §7 | — | todo | 10a, 29 | hot path; must be Trace-gated |
| 29b | 7. Sync engine | Wire `roam.sync.files_synced`, `roam.sync.bytes_synced`, `roam.sync.deletes` Counters and `roam.sync.file_latency_ms` Histogram | — | todo | 10b, 29 | tag with profile + direction |
| 30 | 7. Sync engine | Unit tests (planner) + integration tests (executor) | — | todo | 29, 35 | |
| 31 | 8. State store | Write/read `.roam/schema-version`, manifests, run summaries | — | todo | 5, 29 | JSON-only; atomic temp-file + rename |
| 32 | 8. State store | `roam init` `.gitignore` append + tracked-file refusal | — | todo | 31 | |
| 33 | 9. Deploy/readiness | Publish driver (`dotnet publish` with `-p:ContinuousIntegrationBuild=true` when source != build) | — | todo | 21 | warn on non-deterministic PDB |
| 34 | 9. Deploy/readiness | `ITargetShell` seam + `UnixTargetShell` (stop, start, pgrep, journalctl) | — | todo | 21 | Windows impl is v2 |
| 34a | 9. Deploy/readiness | `[LoggerMessage]` source generators for the readiness poll loop (per-attempt Trace) per ADR 0001 §4 | — | todo | 10a, 34 | hot path |
| 34b | 9. Deploy/readiness | Wire `roam.pipeline.step_duration_ms` Histogram (tag with step name) | — | todo | 10b, 34, 41 | reuse for every pipeline step |
| 35 | 10. Compose lab harness | Testcontainers-dotnet fixture that builds Roam, brings up compose, exposes handles | — | todo | 9 | SSH keys checked in under `tests/labs/compose/host-keys/` |
| 36 | 10. Compose lab harness | Add `bastion` service to `docker-compose.yml` for ProxyJump coverage | — | todo | 35 | |
| 37 | 10. Compose lab harness | Extend SampleApp with `stale-output-check` mode (emits configurable file set) | — | todo | — | assertion target for delete semantics |
| 38 | 10. Compose lab harness | Add `DeterministicSourcePaths` snippet to SampleApp's `Directory.Build.props` | — | todo | — | so cross-host PDB mapping is exercised |
| 39 | 10. Compose lab harness | Readiness timing measurement: cold-start p95 for healthy + delayed-start + crash-on-start | — | todo | 35 | update defaults in `readiness.md` if measurement contradicts |
| 40 | 11. CLI composition | Wire `roam init` end-to-end | — | todo | 14, 32 | |
| 41 | 11. CLI composition | Wire `roam run` end-to-end (preflight → sync-source → publish → stop → sync-artifacts → start → ready) | — | todo | 24, 29, 31, 33, 34 | |
| 42 | 11. CLI composition | Wire `roam attach` end-to-end (preflight → emit launch.json with absolute `sourceFileMap`) | — | todo | 14, 24 | |
| 43 | 12. Debugger emitter | `launch.json` writer: namespaced `roam: <profile>` entry, preserve others, deterministic key ordering | — | todo | 14 | |
| 44 | 12. Debugger emitter | Golden-file + determinism tests (emit twice, byte-identical) | — | todo | 43 | |
| 45 | 13. Integration tests | Happy path: `source != build != target` | — | todo | 35, 41 | |
| 46 | 13. Integration tests | Coincidence matrix: source==build, build==target, source==target, all equal | — | todo | 41, 45 | assert step skipping |
| 47 | 13. Integration tests | Preflight failure tests (one per check) | — | todo | 24, 35 | |
| 48 | 13. Integration tests | Readiness tests: default pgrep success, timeout, explicit ready success, explicit ready timeout, journalctl on failure | — | todo | 34, 37, 41 | |
| 49 | 13. Integration tests | Attach emission tests (determinism, namespacing, preserve non-roam entries) | — | todo | 43 | |
| 50 | 13. Integration tests | SSH topology tests (explicit fields only, `ssh -G` alias, ProxyJump through bastion) | — | todo | 23, 36, 46 | |
| 50a | 13. Integration tests | Logging contract test: assert per-step `Information` lines on stdout match `logging.md` format; `Debug` only with `-v`; `Trace` only in `--log-file` | — | todo | 10a, 11a, 41 | covers ADR 0001 §3 + verbosity matrix |
| 50b | 13. Integration tests | Metrics contract test: `MeterListener` on a happy-path run records every Counter + Histogram named in ADR 0001 §6 | — | todo | 10b, 11a, 41 | regression guard on instrument names |
| 50c | 13. Integration tests | Hot-path allocation test: run sync engine over a 5k-file fixture with `Trace` disabled and assert allocation budget per ADR 0001 §4 | — | todo | 29a, 35 | catches accidental `string.Format` regressions |
| 51 | 14. Release prep | `dotnet pack` produces a valid `dotnet tool` nupkg; local install smoke test | — | todo | 41 | |
| 52 | 14. Release prep | Dogfood: first successful `roam run` against the real Avalonia project | — | todo | 41 | real-world validation gate for v0 exit |
| 53 | 14. Release prep | Tag `v0.1.0`, publish to NuGet (manual) | — | todo | 51, 52 | signing is post-v0 |

## Phase narrative

Phases are a reading aid, not a waterfall — multiple phases can be in
flight at once so long as each slice honors its declared dependencies.

### Phase 0. Foundations (done)

All v0 contracts are written down: schema, CLI, preflight, exit codes,
logging, state, and the logging-and-diagnostics ADR. This is what lets
subsequent slices be tested against a fixed contract rather than a
moving target.

### Phase 1. Scaffolding

Replace the Hello-World `Program.cs` with a real CLI skeleton, pin
package versions, and stand up the `ILoggerFactory` the ADR requires
(metrics deferred — ADR 0001 §6). The test harness wires up an `ILogger`
capture so every later slice can assert on records without bespoke plumbing.

### Phase 2. Config loader

Turn `roamfile.yaml` into typed objects with strict validation and
explicit v0-rejection errors.

### Phase 3. Host resolver

Make `ssh -G` a first-class input and implement the fallback.

### Phase 4. Transport

Real SSH.NET sessions, error mapping to exit codes. ProxyJump spike
gates the non-trivial topology work.

### Phase 5. Preflight

Implement the 11 checks exactly as spelled out in `preflight.md`.
This is the first slice where failures become user-visible.

### Phase 6. SFTP sync spike

Before writing the sync engine, prove the metadata round-trip is
reliable. One afternoon, in-lab, results go into a short
`docs/spikes/sftp-metadata.md` if the findings force a design change.

### Phase 7. Sync engine

The load-bearing subsystem: planner, executor, manifest-scoped delete.

### Phase 8. State store

On-disk `.roam/` reading/writing and the `roam init` gitignore work.

### Phase 9. Deploy / readiness

Publish driver plus the `ITargetShell` seam. The seam itself is small;
its purpose is to make v2 Windows additive.

### Phase 10. Compose lab harness

Testcontainers-dotnet wiring, the new `bastion` service, SampleApp
extensions, and the timing measurement pass. This phase runs in
parallel with Phases 4–9 so integration tests are ready to light up
the moment the subsystems do.

### Phase 11. CLI composition

Wire the three commands end-to-end. The smallest phase in LOC; the
largest in "did we actually build the contract we signed."

### Phase 12. Debugger emitter

`launch.json` writer with deterministic output. Golden-file tested.

### Phase 13. Integration tests

Fill in the v0 coverage matrix on top of the Compose harness. This is
where the exit criteria in `implementation-contract.md` become
checkable.

### Phase 14. Release prep

Package, dogfood against the motivating Avalonia project, tag.

## Risks and spikes

Work items that might surface a design gap. If any of these spikes
fails, the affected slices move to `blocked` and the relevant doc is
updated before proceeding.

- **Spike: SFTP metadata reliability (slice 26).** If mtime or size
  round-trips are unreliable on any lab platform, switch to SHA-256
  per-file and take the performance hit.
- **Spike: ProxyJump port-forward (slice 23).** If SSH.NET's forwarding
  API cannot carry a nested session cleanly, document the limitation
  and ship v0 without ProxyJump support (fallback-only).
- **Spike: Readiness timings (slice 39).** If p95 for healthy startup
  exceeds 15s in Compose or on the motivating Avalonia cold start,
  update defaults in `readiness.md`.

## Out-of-scope for v0 (reminder)

For quick reference, post-v0 items — all already rejected by the
parser or guarded by preflight:

- `roam watch`, `roam doctor`, `roam migrate`, `roam install-debugger`
- profile `extends:` inheritance
- `source-sync.mode: git`
- `netcoredbg` and Rider emission
- Windows targets
- FastRsync byte-level delta
- Mutagen watch backend
- Stage-and-swap deploy strategies
- Signed NuGet release

Slices for these land in v1 / v2 implementation plans written when
that work is picked up.
