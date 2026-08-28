# Logging and terminal output

**Status:** load-bearing for v0. `roam`'s usefulness collapses if a
developer can't tell what it did or why it failed. This document
pins down the **user-facing output contract** — the format, verbosity
flags, and diagnostics that reach the developer's terminal and log
files.

The complementary **code-level logging conventions** (`ILogger<T>`
discipline, `[LoggerMessage]` source generators, `System.Diagnostics.Metrics`
instrumentation, log-level mapping) live in
[`adr/0001-logging-and-diagnostics-strategy.md`](adr/0001-logging-and-diagnostics-strategy.md).
That ADR is the source of truth for how subsystems *produce* records;
this document is the source of truth for how those records are *rendered*
to the operator. The two must stay in sync — when one moves, the other
moves with it.

## Two output streams, two audiences

`roam` writes to two streams:

- **stdout** — the pipeline run log. One line per step, human-readable,
  designed to be watched live in a terminal. Never gated by `--quiet`
  unless noted below.
- **stderr** — diagnostics, warnings, errors, and the exit suffix
  from [`exit-codes.md`](exit-codes.md). Structured so scripts can
  parse it.

Stdout and stderr never interleave the same logical event. A step's
success line goes to stdout; its failure detail goes to stderr.

## The per-step line

Every pipeline step prints exactly one stdout line on completion:

```
  [1/6] sync-source     laptop → workstation       0.8s
  [2/6] publish          workstation                12.4s
  [3/6] stop             kiosk-01                   0.3s
  [4/6] sync-artifacts   workstation → kiosk-01     1.2s
  [5/6] start            kiosk-01                   0.2s
  [✓]   ready            kiosk-01  (pid 4821)       1.1s
  Done.
```

Format:

- `[<i>/<n>]` step counter for numbered steps, `[✓]` or `[✗]` for the
  terminal `ready` line and the final summary.
- Step name, left-padded to a fixed column width (15 chars).
- Host or `host → host` for transfer steps.
- Elapsed wall-clock time, right-aligned.

This format is a *stable* part of the v0 contract — the test harness
parses it to assert step ordering and coincidence collapse.

## Verbosity flags

| Flag            | Effect                                                           |
|-----------------|------------------------------------------------------------------|
| (none)          | Default. One line per step on stdout. Errors on stderr.          |
| `-v` / `--verbose` | Add `DEBUG` records: resolved SSH config per host, commands as they execute, SFTP file counts, manifest diffs. |
| `-q` / `--quiet`| Suppress per-step stdout. Emit only errors (stderr) and the final summary line. |
| `--log-file <path>` | Write a JSON-lines copy of every record, at `DEBUG` verbosity, to the given path regardless of terminal verbosity. |
| `--no-color`    | Suppress ANSI color / status glyphs. `[✓]` becomes `[ok]`, `[✗]` becomes `[fail]`. |

`-v` and `-q` are mutually exclusive; combining them is a usage error
(exit `2`).

## Colour and terminal detection

- Color is on by default when stdout is a TTY and `NO_COLOR` is unset.
- Color is off in every non-TTY context (piped, CI, redirected).
- `--no-color` overrides detection.
- Colors are used only to highlight `[✓]` (green) and `[✗]` (red).
  Step names and timings are never colorized.

## Failure output

On non-zero exit, `roam` writes to stderr in this order:

1. A one-line heading: `[✗] <step> <host>  <one-line reason>`.
2. Any captured remote stdout/stderr from the failing step, verbatim
   and un-reformatted, under a `---` separator. Lines are prefixed
   with `remote:` so they aren't confused with `roam`'s own output.
3. For readiness failures with systemd, the last 30 lines of
   `journalctl` as described in [`readiness.md`](readiness.md).
4. The exit suffix from [`exit-codes.md`](exit-codes.md).

Example:

```
[✗] start  kiosk-01  systemctl exited 1

---
remote: Job for kiosk-ui.service failed because the control process exited with error code.
remote: See "systemctl --user status kiosk-ui.service" for details.

roam: exit=7 step=start host=kiosk-01
```

## Log levels

> **Migration note (2026-06):** roam is migrating from the static
> [`RoamLog`](../src/Roam/RoamLog.cs) event façade (today's implementation —
> `RoamLog.Event(name, message, data)` writes JSONL to `--log-file` and mirrors
> `debug <name>: <message>` to stderr under `--verbose`, with **no per-record
> level**) to the `Microsoft.Extensions.Logging` model in
> [`adr/0001-logging-and-diagnostics-strategy.md`](adr/0001-logging-and-diagnostics-strategy.md).
> The taxonomy and matrix below describe the **target**; until the migration
> lands, levels are approximated (warnings/errors are direct stderr writes) and
> the JSONL keeps the `RoamLog` shape (see "JSON log file format" below).
> Migration plan and scope:
> [`explorations/logging-strategy-review.md`](explorations/logging-strategy-review.md).

Target level taxonomy; the terminal renderer maps levels to streams as follows:

- `Critical` — internal errors (exit `10`). Always printed; includes
  stack trace when `-v`.
- `Error` — step failures. Always printed.
- `Warning` — recoverable conditions (non-deterministic PDBs detected,
  an `ssh -G` key roam didn't understand, a fallback engaged).
  Always printed.
- `Information` — the per-step lines above.
- `Debug` — resolved SSH config, commands issued, sync planner
  output, manifest writes. Printed only with `-v`; always captured
  in `--log-file`.
- `Trace` — per-file SFTP put/skip, per-byte progress on a large
  transfer, per-`pgrep` poll. Routed exclusively through
  `[LoggerMessage]` source generators (see ADR 0001 §4) so they
  cost nothing when the level is disabled. Off by default even with
  `-v`; enable with `--log-file <path>` (the JSONL log captures all
  levels) or by setting `Logging:LogLevel:Default=Trace` in the
  environment.

## Verbosity vs. level matrix

How the verbosity flags above gate which records reach which sink:

| Level         | Default stdout | `-v` stdout | `-q` stdout | `--log-file` JSONL |
|---------------|:--------------:|:-----------:|:-----------:|:------------------:|
| `Critical`    | yes (stderr)   | yes (stderr)| yes (stderr)| yes                |
| `Error`       | yes (stderr)   | yes (stderr)| yes (stderr)| yes                |
| `Warning`     | yes (stderr)   | yes (stderr)| yes (stderr)| yes                |
| `Information` | yes (stdout)   | yes (stdout)| no          | yes                |
| `Debug`       | no             | yes (stdout)| no          | yes                |
| `Trace`       | no             | no          | no          | yes                |

`Trace` never reaches the terminal in v0; it is only useful in the
JSONL log file. This is deliberate — Trace is the level the SFTP
sync engine and SSH command path emit on every file and every
command, and surfacing it interactively would drown the per-step
lines.

## Code-level conventions (summary)

The full conventions live in
[`adr/0001-logging-and-diagnostics-strategy.md`](adr/0001-logging-and-diagnostics-strategy.md);
the points that affect how records show up in the terminal are:

- Subsystems take `ILogger<T>` via constructor injection. The CLI
  layer constructs the `ILoggerFactory` and configures providers
  from the verbosity flags above.
- Message templates are always structured (PascalCase parameter
  names). The JSONL output uses those names verbatim as JSON keys,
  so renaming a parameter is a breaking change for log consumers.
- Hot paths (sync engine inner loop, SSH command channel, readiness
  poll loop) use `[LoggerMessage]` source generators at `Trace`
  level. They cost nothing when Trace is disabled.
- Quantitative signals (file counts, byte totals, per-step durations) are
  **deferred** for v0 — `System.Diagnostics.Metrics` is not wired up (a one-shot
  CLI has no collection window; see ADR 0001 §6). The "how much did this sync
  do" signal comes from sync stats in `--verbose` / the JSONL (roadmap #6) and
  the `roam diag` bundle.

If you find yourself reaching for `string.Format` or `$"..."` to
build a log message, stop and re-read ADR 0001 §2.

## JSON log file format

When `--log-file` is set, every record is written as a single
newline-delimited JSON object:

```json
{"Timestamp":"2026-04-16T14:23:01.123Z","Event":"publish.end","Message":"dotnet publish completed","Data":{"host":"workstation","durationMs":12400}}
```

Fields, as emitted by `RoamLog` today: `Timestamp`, `Event` (the dotted
event name, e.g. `sync.scan`), `Message`, and an optional `Data` object
carrying the structured payload (step, host, counts, byte totals, ...). The
keys inside `Data` vary by event; consumers must ignore unknown keys. (A
flatter `ts` / `level` / `step` schema is part of the *target* M.E.L. model
(ADR 0001), not the current output — see the migration note above.)

## Never logged

- SSH private keys, agent socket paths, passphrases.
- Environment variable values that look like secrets (heuristic: name
  matches `*TOKEN*`, `*SECRET*`, `*PASSWORD*`, `*KEY*`). Names are
  logged; values are redacted to `***`.
- The contents of files synced during `sync-source` or
  `sync-artifacts`. Paths and sizes are logged at `Debug`; contents
  never are.

## Not in v0

- A structured progress bar for sync. The per-file count at `-v` is
  enough for first implementation.
- Per-step log colorization beyond pass/fail.
- Remote log streaming while the remote command runs. v0 buffers
  remote output and emits it at step completion.
- `--log-format=text|json` on stdout. Stdout is always text; JSON goes
  to `--log-file`.
- `System.Diagnostics.Metrics` / OpenTelemetry. Deferred entirely for v0 — a
  one-shot CLI has no live window for a collector to scrape, so v0 defines no
  meters (see ADR 0001 §6).
- `ActivitySource` / distributed tracing. Deferred per ADR 0001
  alternatives.
- A diagnostic listener extension seam. Deferred per ADR 0001 §9.
