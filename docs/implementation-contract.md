# Implementation contract

**Status:** load-bearing. This document freezes the v0 implementation
surface so the project can validate one thin path before taking on the
rest of the design space. If a feature is not listed here as v0, it is
not part of v0.

## The point of the contract

The design docs intentionally explore a large space: multiple transport
modes, watch mode, alternate debuggers, richer deploy semantics, and
editor-specific emitters. That is useful for research, but it is too
much surface area for the first real implementation.

The contract narrows the work to one complete, opinionated slice:

- existing .NET project in a git repo,
- source/build/target roles declared in `roamfile.yaml`,
- one-shot pipeline run,
- remote publish over SSH,
- artifact deploy over SSH/SFTP,
- optional debugger config generation for VSCode,
- enough validation and state to keep the workflow understandable.

## Frozen v0 goals

v0 exists to prove one end-to-end story:

1. `roam init` can scaffold a usable `roamfile.yaml` from an existing
   .NET project.
2. `roam run <profile>` can mirror source to build, run `dotnet publish`
   on build, sync artifacts to target, start the target process, and
   verify readiness.
3. `roam attach <profile>` can emit deterministic VSCode attach config
   for the same profile.
4. The implementation is structured so later versions can add watch
   mode, richer transport choices, and alternate debuggers without
   rewriting the whole tool.

## v0 command surface

The v0 CLI surface is frozen to:

- `roam init`
- `roam run <profile>`
- `roam attach <profile>`

The full flag set, argument rules, `--help` golden output, and exit
codes are documented in [`cli.md`](cli.md) and
[`exit-codes.md`](exit-codes.md). Output formatting is pinned in
[`logging.md`](logging.md). Preflight validation is part of `run` and
`attach` (see [`preflight.md`](preflight.md)); it does not need a
separate top-level command in v0.

Explicitly **not** in v0:

- `roam watch`
- `roam doctor`
- `roam install-debugger`
- `roam migrate`
- arbitrary hooks/stages/tasks

## v0 pipeline behavior

The only execution pipeline in v0 is:

`sync-source → publish → stop → sync-artifacts → start → ready`

`attach` is a separate downstream operation that emits editor config; it
is not a prerequisite for `run` succeeding.

Required v0 behavior:

- fixed step ordering,
- role-coincidence collapse when source/build/target are the same host,
- stop before artifact replacement,
- manifest-scoped delete semantics during sync to avoid stale outputs
  (see [`paths.md`](paths.md) and [`state.md`](state.md)),
- per-step output matching [`logging.md`](logging.md) exactly,
- deterministic failure reporting when readiness fails, including the
  exit suffix defined in [`exit-codes.md`](exit-codes.md).

## Frozen v0 config shape

The config boundary for v0 is:

- `roamfile.yaml` owns multi-host orchestration,
- .NET files own .NET build/runtime concepts,
- editor files are generated artifacts.

The authoritative schema is
[`roamfile.schema.json`](roamfile.schema.json); the human-readable
companion is [`configuration.md`](configuration.md); the canonical
fixture is
[`../tests/fixtures/SampleApp/roamfile.yaml`](../tests/fixtures/SampleApp/roamfile.yaml).
The parser is strict: unknown keys are `config` errors (exit `3`).

The required v0 concepts are:

- schema `version: 1`,
- project root (`solution` or `csproj`),
- named hosts with SSH connection info and optional `workspace`, `os`,
- named profiles with `source`, `build`, `target`,
- `publish-profile`,
- `launch-profile`,
- optional profile-level `env:` overlay,
- `deploy.path`,
- optional `deploy.flatten-publish`,
- optional `deploy.stop`, `deploy.start`, `deploy.ready`,
- optional `deploy.ready-timeout` and `deploy.ready-interval-ms`,
- `debug` block with `enabled`, `debugger: vsdbg`, `editor: vscode`,
  `process-name`.

Out of v0 (rejected by the parser, not silently ignored):

- profile inheritance (`extends:`),
- multi-project profile orchestration,
- alternative sync modes (`source-sync.mode: git`),
- editor choice beyond VSCode (`debug.editor: rider`),
- debugger choice beyond VSDBG (`debug.debugger: netcoredbg`),
- target OS beyond Linux/macOS (`hosts.<h>.os: windows`),
- transport overrides (`transport.artifact-relay`).

## Supported v0 topology and platform expectations

v0 is optimized for the motivating path:

- source, build, and target are raw hosts reachable over SSH,
- source and build may each have a repo workspace,
- target is a deploy destination identified by `deploy.path`,
- the main target environment is Linux/macOS-style SSH hosts,
- systemd-aware readiness diagnostics are supported when applicable
  (see [`readiness.md`](readiness.md)).

Windows targets are explicitly rejected at preflight in v0 (see
[`preflight.md`](preflight.md)). The architecture introduces an
`ITargetShell` seam for stop / start / pgrep / journalctl so the v2
Windows work can add a PowerShell-backed implementation without
touching the transport or sync layers. Concretely: v0 ships one
`UnixTargetShell` implementation; v2 adds `WindowsTargetShell` against
the same interface.

## Internal subsystem boundaries

The v0 implementation should be split into these subsystems:

1. **Config loader** — parse `roamfile.yaml`, validate required fields,
   resolve profile references to .NET artifacts.
2. **Host resolver** — merge explicit host fields with `ssh -G`
   resolution, normalize connection settings, and run preflight checks.
3. **Transport layer** — own SSH.NET connections, remote commands,
   and SFTP file movement.
4. **Sync engine** — compute metadata diffs and perform source/artifact
   sync with delete semantics.
5. **Deploy/readiness layer** — execute stop/start/ready behavior and
   report failures cleanly.
6. **State store** — manage `.roam/` metadata in the source/build
   workspace used by the current invocation.
7. **Debugger emitter** — generate deterministic `.vscode/launch.json`
   entries and nothing more.
8. **CLI layer** — compose the subsystems into user-facing commands.

No v0 implementation should collapse these all into one giant
command-handler type unless the boundaries are still obvious in code.

## Required v0 preflight

The preflight contract is spelled out in [`preflight.md`](preflight.md)
with one section per check, including pass conditions and failure
messages. In summary v0 verifies, in order:

1. `profile-exists`
2. platform check (target OS is Linux or macOS)
3. `hosts-defined`
4. `ssh-config-resolved`
5. `ssh-auth-works`
6. `build-has-dotnet`
7. `workspaces-usable`
8. `deploy-path-writable`
9. `publish-profile-exists`
10. `launch-profile-exists`
11. `debug-prerequisites` (attach only)

Preflight fails fast: the first failing check exits `4` (`preflight`)
with the message defined in [`preflight.md`](preflight.md). No
destructive work runs before preflight completes.

## Required v0 state

`roam` owns a `.roam/` directory in the source host's workspace. The
on-disk layout (manifests, run summaries, tmp scratch), the gitignore
handling, and the idempotency semantics are frozen in
[`state.md`](state.md). `.roam/schema-version: 1` for v0.

Highlights that bind the implementation:

- `manifests/<profile>/artifacts.json` is the authoritative record of
  what lives under `deploy.path`. Delete semantics (see
  [`paths.md`](paths.md)) operate only on entries in this manifest.
- `runs/last.json` and `runs/<profile>.json` are written on every
  exit, success or failure, carrying the exit code and the step that
  failed.
- `roam init` appends `.roam/` to `.gitignore` and refuses to run if
  `.roam/` is already tracked.

## Frozen v0 transport and debugger choices

v0 transport is:

- SSH.NET for remote commands and SFTP file transfer,
- `ssh -G` as config resolution input when available,
- metadata-diffed SFTP sync as the only built-in sync mode.

Postpone to later versions:

- Mutagen-backed watch sync,
- rsync override support,
- FastRsync-style byte-level delta transfer,
- agent-forward or direct-mesh artifact transfer.

v0 debugger support is:

- emit deterministic VSCode `coreclr` attach config,
- rely on the Microsoft extension's own remote bootstrap flow,
- treat debugger support as config emission, not debugger execution.

Postpone to later versions:

- `netcoredbg`,
- Rider emission,
- CLI-driven debugger bootstrap/install flows.

## v0 non-goals

v0 is not:

- a CI/CD tool,
- a build system,
- a secrets manager,
- a remote-execution farm,
- a general-purpose sync tool,
- a debugger frontend,
- a cross-language orchestrator.

## Exit criteria for v0

v0 is done when:

- the three commands in [`cli.md`](cli.md) work against the real
  motivating project,
- every preflight check in [`preflight.md`](preflight.md) is
  implemented and exercised by the Compose integration suite,
- every exit code in [`exit-codes.md`](exit-codes.md) is produced by
  at least one test,
- the Compose integration suite in
  [`test-architecture.md`](test-architecture.md) runs green end-to-end
  against the canonical fixture roamfile,
- the docs and config shape are stable enough to onboard another user,
- the code structure can absorb the next round of features without a
  rewrite.

## Planned expansion after v0

### v1

- `roam doctor`
- `roam watch`
- Mutagen integration when installed
- `--plan` / dry-run mode for sync
- profile inheritance (`extends:`)
- richer retry/timeout controls
- stronger target diagnostics
- optional `source-sync.mode: git` workflow

### v2

- alternate debugger path (`netcoredbg`)
- Rider emitter
- alternate artifact transport modes
- more advanced deploy strategies such as stage-and-swap retention
- deeper Windows-target support (`WindowsTargetShell` implementation
  of the `ITargetShell` seam introduced in v0)

Anything beyond v2 should wait for real-user pressure rather than being
designed in advance.
