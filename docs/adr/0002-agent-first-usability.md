# ADR-0002: Agent-first usability

**Status:** Accepted (2026-06-13).

> Companion to ADR-0001 ("Logging and Diagnostics Strategy", in-flight on branch
> `claude/review-roam-skeleton`): that ADR sets the *code-level* conventions for
> emitting log and metric records (the producer side); this one is the *consumer*
> side — who reads diagnostics and how an agent fetches them. Numbered 0002 to
> avoid the 0001 collision; ADR numbers are assigned at merge.

## Context

roam has two distinct classes of consumer, and they want different things from
the same tool:

- **Humans** drive roam from a terminal and an IDE. Their high-bandwidth
  debugging tool is an interactive debugger: set a breakpoint, inspect a frame,
  step. `roam attach` serves them — it emits a VS Code `launch.json` that lets
  the Microsoft C# extension drive `vsdbg` over SSH to the target (see
  [`debugger.md`](../debugger.md)). roam never touches the debugger binary; it
  only generates config.

- **Agents** (Claude Code and similar) drive roam from a shell in an automated
  edit -> deploy -> observe loop. An agent has no Debug Adapter Protocol client:
  it cannot set a breakpoint, read a call stack, or step a live process. The
  `launch.json` roam emits is inert to it. An agent's high-bandwidth tools are
  *readable artifacts*: structured logs, run summaries, crash dumps, traces, and
  machine-parseable command output it can consume over the same shell it already
  drives.

Until now roam's debug story has been written entirely for the human consumer.
The interactive attach loop was even briefly considered a headline item to prove
end-to-end. But proving or expanding interactive attach delivers nothing to the
agent consumer, and the agent consumer is a first-class user of this tool.

This ADR records the decision to treat agent usability as a first-class design
axis, and the de-prioritization and design choices that follow from it.

## Decision

**Treat agents as first-class consumers of roam. Where the human and agent paths
diverge, prioritize machine-consumable diagnostics for the agent over
interactive-debugger affordances — without degrading the human path.**

Concretely:

1. **De-prioritize the interactive debugger-attach loop as an agent feature.**
   `roam attach` (vsdbg via the Microsoft bootstrap; `netcoredbg` later) remains
   a **human-only DX feature** at its current maturity — `DebuggerEmitter` emits
   `launch.json`, unit-tested, unchanged. We will **not** invest in
   E2E-proving or expanding the attach loop for the agent's benefit. The
   [`debugger.md`](../debugger.md) stance (never touch a debugger binary; emit
   config only) is unchanged.

2. **The agent's debugger is a diagnostic bundle, not a live session.** roam will
   provide read-only capture of the artifacts an agent can actually consume —
   process logs, crash dumps, and traces — fetched from the target into a local
   directory, indexed by a machine-readable manifest. This promotes
   [`debugger.md`](../debugger.md) Open Question #4 (`dotnet-dump` /
   `dotnet-trace` post-mortem) from "open question" to "the decided agent-facing
   path."

3. **Structured output is additive and opt-in.** Read verbs (`status`, `diag`,
   run summaries) gain a `--json` mode emitting a stable schema to stdout for
   agents. The human default (the per-step text lines pinned in
   [`logging.md`](../logging.md)) is unchanged. Agent output never *replaces* the
   human default; it is requested with `--json`.

4. **Everything agent-facing stays inside the provisioning boundary.** Diagnostic
   capture is read-only on the target's host state. It is the read-only sibling
   [`provisioning-boundary.md`](../provisioning-boundary.md) already imagined for
   `roam doctor`. roam installs no packages to capture diagnostics; the design
   below shows how each artifact class respects this.

## Design — how remote log / dump / trace fetching works

The agent-facing capture is a new read-only verb, **`roam diag <profile>`**, with
the already-sketched `roam status` and `roam logs` verbs
(tracked as issues) folded under the same `--json` contract. It
reuses roam's existing transports — `SshHostResolver` + `SshCommandRunner` for
remote commands, `SftpDirectoryDownloader` for pulling files — and adds no new
transport. Index construction and path planning are pure (the functional core);
SSH, SFTP, and the clock live in the shell.

```
roam diag <profile> [--out <dir>] [--logs] [--dump] [--trace <seconds>]
                    [--since <duration>] [--json] [--keep-remote]
```

It captures up to three artifact classes, each chosen to respect the provisioning
boundary.

### (A) Process logs — default, pure read, zero target-side tooling

- **Service / `detach` mode:** roam already redirects the deployed process's
  stdout+stderr to `<deploy.path>/roam-<profile>.out` (the `nohup` wrapper from
  the `detach` work; the Windows scheduled task can redirect the same way).
  `roam diag` SFTP-downloads it. This is the agent's primary signal and it is
  *free* — roam already writes it.
- **systemd-managed:** `journalctl --user -u <unit> --since <since> --no-pager
  -o short-iso` over SSH, captured to `journal.log`. Unit resolved from a
  `diag.unit:` hint or parsed from the `start:` command.
- **Windows scheduled-task / service:** fetch the redirected `.out` if present;
  optionally a filtered `Get-WinEvent` of the Application log serialized to text.
- **App-authored logs:** `diag.logs: [<paths under the deploy root>]` — roam
  SFTP-downloads the matches.

All read-only; nothing is installed or mutated.

### (B) Crash dumps — opt-in at start, runtime-native, still no extra tooling

The .NET runtime can write a minidump on an unhandled crash with **no external
tool** — its built-in `createdump` already ships inside every self-contained
publish (the extensionless executable roam's exec-bit fix marks `0755`). roam
enables it by adding to the process env at start (opt-in via
`diag.crash-dumps: true`):

```
DOTNET_DbgEnableMiniDump=1
DOTNET_DbgMiniDumpType=2                 # 2 = Heap; 1 = Mini .. 4 = Full, configurable
DOTNET_DbgMiniDumpName=<deploy.path>/.roam-diag/dumps/core.%e.%p.%t.dmp
```

`roam diag --dump` then SFTP-downloads everything under
`<deploy.path>/.roam-diag/dumps/`. The only target-side footprint is one env
block (roam already owns the start command's env) and a directory under the
deploy root (roam already owns the deploy root). Fully inside the boundary, with
zero dependencies beyond what roam already ships.

### (C) Live dump / on-demand trace — opt-in, tool-gated

An on-demand dump of a *running* (non-crashed) process, or a CPU / GC /
allocation trace, needs `dotnet-dump` / `dotnet-trace`, which talk to the
runtime's diagnostic IPC socket. These are **not** in the publish. roam handles
this with two tiers, mirroring the vsdbg-vs-netcoredbg split in
[`debugger.md`](../debugger.md) and the lenient framework-runtime preflight
already in `RuntimeCompatibility`:

| `diag.tool-source` | Behavior | Boundary |
|---|---|---|
| `target` (default) | Assume `dotnet-trace` / `dotnet-dump` is on the target PATH (operator-provisioned). Preflight `dotnet-trace --version`; on absence, fail with an actionable message (`install with dotnet tool install -g dotnet-trace, or set diag.tool-source: bundled`). | roam mutates nothing. |
| `bundled` (explicit opt-in) | roam ships the single-file tool to `<deploy.path>/.roam-diag/tools/`, runs it, fetches the artifact, removes the tool. Legal (the diagnostic tools are redistributable) and reversible. | Writes a tool to a roam-owned scratch dir, then removes it. Explicit opt-in, never default — same reasoning as `debug.install-on-target: false`. |

Mechanism: `dotnet-trace collect -p <pid> --duration 00:00:<n> -o
<scratch>/trace.nettrace` (the pid comes from the readiness probe roam already
runs — see [`logging.md`](../logging.md), the `ready ... (pid 4821)` line).
Because `.nettrace` is binary and an agent cannot read it, roam optionally runs
`dotnet-trace convert --format speedscope` **locally** after fetch, producing
speedscope JSON the agent *can* read.

### The bundle + index — the agent's "attach"

Every artifact lands under `--out` (default `.roam/diag/<profile>/<run-id>/`).
roam writes `diag.json` — and prints it to stdout with `--json` — indexing each
artifact:

```json
{
  "profile": "kiosk", "target": "kiosk-01", "pid": 4821,
  "runtime": "10.0.0", "captured_utc": "2026-06-13T...",
  "artifacts": [
    {"kind":"log",   "target_path":"/opt/app/roam-kiosk.out",            "local_path":"roam-kiosk.out",        "bytes":81234,    "sha256":"..."},
    {"kind":"dump",  "target_path":".roam-diag/dumps/core.App.4821.dmp", "local_path":"dumps/core.App.4821.dmp","bytes":48210000, "sha256":"...", "reason":"crash"},
    {"kind":"trace", "local_path":"trace.speedscope.json",               "bytes":210345,   "sha256":"..."}
  ]
}
```

Logs and journal text are directly readable; dumps and traces are pointers the
agent navigates (or hands to a follow-up tool). One `roam diag --json` gives an
agent a structured map of every artifact — the read-only analogue of a human
hitting F5.

### Boundary compliance, restated

`roam diag` changes **no** OS or host state on the target. Its only target-side
writes are (1) the opt-in crash-dump env at start and (2) a roam-owned
`<deploy.path>/.roam-diag/` scratch dir, cleaned after fetch (`--keep-remote` to
retain). It installs no packages; the tool-gated tier is either assumed-present
(preflight) or explicitly bundled-then-removed. This is exactly the read-only
diagnostics [`provisioning-boundary.md`](../provisioning-boundary.md) sanctions.

## Consequences

- **Roadmap:** the agent-facing cluster — sync observability (#6), `roam doctor`
  (#9), and the new `roam diag` / `roam status --json` / `roam logs` verbs —
  becomes the way roam serves agent loops. Proving the interactive attach loop is
  explicitly deferred (a human-DX nicety, not on the agent-value path).
- **`roam attach` is unchanged** and stays documented as the human path. No
  regression, no new investment.
- **Reuses existing machinery:** SSH/SFTP transports, the readiness pid, the
  manifest/index pattern, the lenient-preflight pattern, and `createdump`
  (already deployed). Little new surface beyond the verb, the index schema, and
  the opt-in env/flags.
- **New schema:** a `diag:` block (`crash-dumps`, `logs`, `unit`, `tool-source`,
  `dump-type`) and `--json` on read verbs, to be pinned in
  [`configuration.md`](../configuration.md) and `roamfile.schema.json` when built.
- **Implementation is not yet built.** This ADR decides the direction and the
  shape; the work is tracked in the issue tracker ("Implement
  `roam diag`").

## Alternatives considered

- **Drive a debugger headlessly for the agent (netcoredbg over a scripted DAP
  client).** Rejected: it makes roam (or the agent) a DAP frontend — exactly
  what [`debugger.md`](../debugger.md) point 5 forbids ("never write a custom
  debugger frontend") — and an agent gains little from a live session it must
  script blind versus readable post-mortem artifacts.
- **Ship vsdbg for an agent path.** Illegal ([`debugger.md`](../debugger.md)) and
  pointless (no DAP client to consume it).
- **Structured logs only, no dumps/traces.** Insufficient: a crash with no
  managed stack in the log is opaque without a dump; a perf regression needs a
  trace. The runtime-native crash-dump path costs almost nothing, so excluding it
  would be a false economy.
- **Install diagnostic tools as part of `roam run`.** Rejected: crosses the
  provisioning boundary on the normal deploy path. Tool presence is
  preflight-checked or explicitly bundled, never silently installed.

## Open questions

1. **Default crash-dumps on for service mode?** Leaning yes for `detach`/service
   profiles (the env is harmless and the payoff is high), explicit opt-in
   otherwise. Revisit once `roam diag` has real usage.
2. **Dump type default.** `2` (Heap) balances size against usefulness; `4` (Full)
   for hard cases. Per-profile override via `diag.dump-type`.
3. **`roam status` vs `roam diag` boundary.** `status` is the cheap
   liveness / last-lines check (tracked separately); `diag` is the heavier artifact pull.
   Keep them as separate verbs sharing the `diag:` / `status:` config and the
   `--json` contract, or fold `status` into `diag --quick`? Leaning separate.
4. **Trace conversion dependency.** `dotnet-trace convert` needs `dotnet-trace`
   on the *controller* (where roam runs). Acceptable — the controller is a dev
   box with the SDK — but document it.
5. **Windows live-process dump.** `createdump` / `DOTNET_DbgEnableMiniDump` covers
   crashes cross-platform; on-demand live dumps of a Windows interactive-session
   task need the pid in the right session. Defer until a Windows consumer needs
   it.
