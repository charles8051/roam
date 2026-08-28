# ADR 0001: Logging and Diagnostics Strategy

## Status

Accepted as the **target** logging standard for `roam` (a CLI). Adopted because
`ILogger<T>` dependency injection is the .NET standard idiom and keeps `roam`
consistent with the projects around it — that consistency, not embeddability, is
the deciding rationale.

This is the migration target, **not the current state**: roam logs through the
static `RoamLog` façade today (see "Current state and the migration" below).
`System.Diagnostics.Metrics` (§6) is **deferred** for v0 — a one-shot CLI has no
collection window — so v0 ships `ILogger<T>` only. See
[`../explorations/logging-strategy-review.md`](../explorations/logging-strategy-review.md)
for the review that produced these scoping decisions.

## Context

`roam` is a .NET console tool, but its subsystems (config loader,
host resolver, transport, sync engine, deploy/readiness, state store,
debugger emitter, CLI composition — see
[`../implementation-contract.md`](../implementation-contract.md)) are
intentionally library-shaped: each is testable in isolation, accepts
its dependencies via constructor injection, and is composed by the
CLI layer at the top.

The CLI layer owns provider configuration; everything underneath
ships only `ILogger<T>` so that the integration test harness, future
embedders, and any v1 web-of-tools host can plug in their own sinks.

Logging decisions made early and applied consistently prevent a
painful retrofit as the codebase grows. Without agreed conventions,
teams produce inconsistent patterns across subsystems, miss
diagnostic context in the layers that need it most, and risk
introducing allocation pressure in performance-sensitive code paths
(notably the SFTP sync engine).

This ADR establishes the logging, metrics, and diagnostics
conventions for the project. The user-facing terminal output
contract — what stdout/stderr looks like, what verbosity flags do,
what the JSONL log file format is — is a separate document at
[`../logging.md`](../logging.md). This ADR is about the *code-level*
conventions that produce the records that doc shapes.

## Current state and the migration

This ADR was drafted against the repo skeleton; the shipped code does **not**
match it yet. Today every subsystem logs through a static façade,
[`RoamLog`](../../src/Roam/RoamLog.cs): `RoamLog.Event(name, message, data)`
serializes `{Timestamp, Event, Message, Data}` to the `--log-file` JSONL and
mirrors `debug <name>: <message>` to stderr under `--verbose`. There is no
`Microsoft.Extensions.Logging`, no DI, no per-record level, and no metrics.
Adopting the decisions below is therefore a **migration**, not a greenfield
build — the original draft's "consistent from the first phase, not retrofitted"
framing does not hold and is corrected in Consequences.

The migration, in dependency order:

1. **Stand up an `ILoggerFactory` in the CLI host**, configured from the existing
   `-v` / `-q` / `--log-file` flags ([`../logging.md`](../logging.md)). The JSONL
   `--log-file` becomes a **custom `ILoggerProvider`** (a sink), so the on-disk
   log contract is preserved rather than re-invented.
2. **Move call sites from `RoamLog.Event` to injected `ILogger<T>`** plus
   `[LoggerMessage]` on the hot paths (§4). Subsystems that are static today
   (`MetadataDiffSyncEngine`, the SSH command path) need a logger threaded in or
   to become instance-shaped — this plumbing is the bulk of the work.
3. **Preserve the dotted event-name taxonomy** (`sync.scan`,
   `sftp.upload_file.start`, ...) as `EventId` names or a structured property; it
   is load-bearing for log consumers and must not be dropped silently.
4. **The JSONL schema changes** from `{Timestamp, Event, Message, Data}` to the
   level-bearing shape in [`../logging.md`](../logging.md). That is a **breaking
   change to the log-file contract** — and the agent-facing `roam diag` reads
   that file — so it must be called out in release notes, not slipped in.

Until the migration lands, [`../logging.md`](../logging.md) marks the level model
as the target and documents the `RoamLog` shape as today's reality.

## Decision

### 1. Primary logging abstraction: `ILogger<T>`

All subsystems will use `Microsoft.Extensions.Logging.ILogger<T>` as
the sole logging abstraction. This is the .NET standard, integrates
with dependency injection, and allows the CLI host (or a future
embedder) to plug in any provider — Serilog, NLog, console, or
nothing.

Subsystems must not depend on any specific logging provider. They
ship `ILogger` usage only.

Accept `ILogger<T>` through constructor injection. When a logger is
optional (e.g., in types that can be newed up directly in tests),
accept `ILogger<T>?` and fall back to `NullLogger<T>.Instance`:

```csharp
public sealed class HostResolver
{
    private readonly ILogger<HostResolver> _logger;

    public HostResolver(ILogger<HostResolver>? logger = null)
    {
        _logger = logger ?? NullLogger<HostResolver>.Instance;
    }
}
```

The CLI layer constructs an `ILoggerFactory` once per invocation,
configured from the verbosity flags described in
[`../logging.md`](../logging.md), and threads `ILogger<T>` instances
into every subsystem it composes.

### 2. Structured logging — always

All log calls will use semantic message templates with structured
parameters rather than string concatenation or interpolation. This
ensures log entries are machine-parseable when the JSONL log file is
emitted, and remain searchable in any downstream sink.

```csharp
// Good — structured template
logger.LogInformation("Synced {FileCount} files to {Host} in {ElapsedMs}ms",
    fileCount, host, elapsedMs);

// Bad — string interpolation (allocates even when level is disabled)
logger.LogInformation($"Synced {fileCount} files to {host} in {elapsedMs}ms");
```

Use PascalCase for template parameter names. They become property
names in the JSONL output and in any structured sink the host
configures.

### 3. Log level conventions

Follow consistent log level assignments across all subsystems:

| Level | Usage | Roam examples |
|-------|-------|---------------|
| **Trace** | Per-item hot-path diagnostics, off by default even at `--verbose` | Per-file SFTP put/skip, per-byte progress on a large transfer, per-`pgrep` poll |
| **Debug** | Lifecycle transitions, internal state changes, decision points | Resolved SSH config per host, sync planner output (transfer/delete lists), `dotnet publish` argv, manifest writes |
| **Information** | Session-level or operation-level events visible in normal operation | Per-step pipeline lines from [`../logging.md`](../logging.md), preflight checks passed, run started/finished |
| **Warning** | Degraded but recoverable states | Non-deterministic PDB warning, retry on a flaky SSH read, fallback from `ssh -G` to explicit fields |
| **Error** | Operation failures the caller should know about | Preflight check failed, publish exited non-zero, sync deletion refused, readiness timed out |
| **Critical** | Unrecoverable failures that compromise the process | Internal exception in the orchestrator, corrupted `.roam/` state |

Rules of thumb:

- If an operator would want to see it in normal terminal output, it's
  **Information**.
- If a developer would only enable it during active debugging, it's
  **Debug**; if it's per-item-in-a-loop, it's **Trace**.
- If something went wrong but the system recovered, it's **Warning**.
- If the current step failed, it's **Error**.

This level mapping is consistent with [`../logging.md`](../logging.md):
the per-step lines are `Information`, `--verbose` raises the floor to
`Debug`, and `Trace` is reserved for per-file/per-byte hot paths and
must use `[LoggerMessage]` (see below).

### 4. Hot-path logging with `[LoggerMessage]` source generation

Logging in performance-sensitive code paths must use the
`[LoggerMessage]` source generator. This avoids allocating message
strings, boxing value-type arguments, or evaluating expressions when
the target level is disabled — important because the SFTP sync engine
walks every file in a publish output and the SSH command channel
runs every preflight probe.

```csharp
public sealed partial class SftpSyncEngine
{
    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Synced {Path} ({Bytes} bytes) in {ElapsedMs:F2}ms")]
    private static partial void LogFileSynced(
        ILogger logger, string path, long bytes, double elapsedMs);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Skipped {Path} (size+mtime match)")]
    private static partial void LogFileSkipped(
        ILogger logger, string path);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Retrying {Operation} on {Host} after transient failure (attempt {Attempt})")]
    private static partial void LogTransientFailure(
        ILogger logger, string operation, string host, int attempt);
}
```

Requirements for `[LoggerMessage]` usage:

- The containing class must be `partial`.
- Log methods are `private static partial void`.
- The first parameter is always `ILogger logger`.
- Exception parameters (if needed) go last: `Exception ex`.
- Use format specifiers in the message template for numeric precision
  (e.g., `{ElapsedMs:F2}`).

For non-hot-path code (CLI parsing, preflight, publish driver,
debugger emit), standard `ILogger` extension methods with structured
templates are acceptable:

```csharp
_logger.LogInformation("Preflight passed in {ElapsedMs}ms", elapsed);
```

Hot paths in `roam` for v0 are:

- the SFTP sync engine's per-file inner loop,
- the SSH command-execution path inside the transport layer,
- the readiness poll loop.

Anything else can use the standard extension methods.

### 5. Log method naming conventions

Source-generated log methods should follow a consistent naming
pattern:

| Pattern | Usage |
|---------|-------|
| `Log{Event}` | General events: `LogStarted`, `LogStopped`, `LogDisposed` |
| `Log{Subject}{Event}` | Scoped events: `LogFileSynced`, `LogPreflightFailed`, `LogStepCompleted` |
| `Log{Severity}{Subject}` | When severity is the distinguishing factor: `LogTransientFailure`, `LogReadyTimeout` |
| `LogPeriodic{Subject}` | Periodic status snapshots: `LogPeriodicSyncProgress` |

Group log methods together at the bottom of the class, separated by
a comment banner:

```csharp
// ── Source-generated log methods ─────────────────────────────────────

[LoggerMessage(Level = LogLevel.Debug,
    Message = "Sync planner produced {TransferCount} transfers and {DeleteCount} deletes")]
private static partial void LogPlannerOutput(
    ILogger logger, int transferCount, int deleteCount);

[LoggerMessage(Level = LogLevel.Information,
    Message = "Sync completed: files={FilesSynced}, bytes={BytesTransferred}, duration={DurationSec:F2}s")]
private static partial void LogSyncCompleted(
    ILogger logger, long filesSynced, long bytesTransferred, double durationSec);
```

### 6. Quantitative telemetry with `System.Diagnostics.Metrics` (deferred — not in v0)

> **Deferred (2026-06).** roam is a one-shot CLI: it runs for seconds and exits,
> so there is no live window for `dotnet-counters` or an OTel collector to
> scrape — meters defined here would emit into the void. This is the same
> reasoning that *deferred* `ActivitySource` (see Alternatives); it applies
> identically to metrics. Consistency of idiom justifies the
> **logging** idiom (`ILogger<T>`), not metrics that nothing collects. The
> user-facing "how much did this sync do" signal is served instead by sync stats
> in `--verbose` / the JSONL (roadmap #6) and by the `roam diag` bundle
> (agent-first usability ADR) — far lighter than standing up `Meter`s plus an
> in-process `MeterListener` to read them back. Revisit only if roam grows a
> long-running host or an on-exit exporter.

The design below is retained for that future, **not** v0 scope. Complement
`ILogger` with `System.Diagnostics.Metrics` for quantitative performance
telemetry; metrics are low-overhead and compatible with OpenTelemetry exporters.

#### Meter naming

Each subsystem gets one `Meter` with a dot-separated namespace under
the `roam.` prefix:

```csharp
private static readonly Meter SyncMeter = new("roam.sync", "0.1.0");
private static readonly Meter TransportMeter = new("roam.transport", "0.1.0");
private static readonly Meter PreflightMeter = new("roam.preflight", "0.1.0");
```

#### Instrument naming

Use lowercase dot-separated names following the OpenTelemetry
semantic-convention style:

```csharp
private static readonly Counter<long> FilesSyncedCounter =
    SyncMeter.CreateCounter<long>(
        "roam.sync.files_synced",
        description: "Number of files transferred during sync.");

private static readonly Histogram<double> SyncLatencyHistogram =
    SyncMeter.CreateHistogram<double>(
        "roam.sync.file_latency_ms",
        description: "Per-file SFTP transfer latency.");

private static readonly Counter<long> PreflightFailuresCounter =
    PreflightMeter.CreateCounter<long>(
        "roam.preflight.failures",
        description: "Number of preflight checks that failed.");
```

#### When to use Metrics vs. Logging

| Signal | Use |
|--------|-----|
| **Counter** | Monotonically increasing totals: files synced, retries performed, preflight failures, exit-code occurrences |
| **Histogram** | Distribution of values: per-file SFTP latency, per-step wall-clock duration, readiness-poll attempts |
| **ILogger (Trace/Debug)** | Per-item context with human-readable detail for active debugging |
| **ILogger (Information+)** | Discrete events with structured context: "preflight passed", "publish failed" |

Use both together when appropriate — a counter tracks that a
transient-failure retry happened, while a warning log provides the
surrounding context.

The instrumentation set, if revived, would be intentionally narrow:

- `roam.sync.files_synced`, `roam.sync.bytes_synced`,
  `roam.sync.file_latency_ms`, `roam.sync.deletes`.
- `roam.transport.commands_executed`, `roam.transport.command_latency_ms`.
- `roam.preflight.checks_passed`, `roam.preflight.failures`.
- `roam.pipeline.step_duration_ms` (tagged with the step name).

Wider coverage and a default OTel exporter ride v1.

### 7. Periodic status logging

For long-running processing loops — chiefly the initial cold-start
SFTP transfer of a publish output — emit periodic status logs at
**Debug** level that summarize cumulative progress. Gate these on a
modulo check or an elapsed-time window to avoid flooding:

```csharp
if (filesSynced % 50 == 0)
{
    LogPeriodicSyncProgress(_logger, filesSynced, bytesTransferred,
        totalFiles, elapsed.TotalSeconds);
}
```

This gives operators a heartbeat view of sync health without
requiring Trace-level verbosity.

### 8. Lifecycle event logging

Log lifecycle transitions consistently:

| Event | Level | What to include |
|-------|-------|-----------------|
| Run started | Debug | Profile name, resolved hosts, override flags |
| Step started | Debug | Step name, host, command summary |
| Step completed | Information | Step name, host, duration (this is the per-step line in [`../logging.md`](../logging.md)) |
| Step failed | Error | Step name, host, exit code, captured remote stderr summary |
| Preflight check passed | Debug | Check name |
| Preflight check failed | Error | Check name, failure message from [`../preflight.md`](../preflight.md) |
| Run completed | Debug | Cumulative stats: total duration, exit code |
| Subsystem disposed | Trace | Lifetime stats if non-trivial |

Step-completed logs are at **Information** because they carry the
session summary that operators need without enabling Debug. But the pretty
per-step line itself (`[2/6] publish ... 12.4s`) is rendered by a **dedicated
formatter** owned by [`../logging.md`](../logging.md), not by routing
`Information`-level `ILogger` output to the console — keep that presentation
bespoke so log-level gating never controls its format. This ADR governs the
records; that doc governs the rendering.

### 9. Diagnostics listener extension seam (deferred)

A focused callback interface separate from `ILogger`, for embedders
that need to react to events in code rather than parse logs, is a
v1 candidate. v0 ships only `ILogger` + `Meter` because the
integration test harness can capture both via standard
`ILoggerFactory` and `MeterListener` plumbing.

Revisit when:

- a real consumer needs to react to events in code (not just observe
  logs);
- a stable diagnostic contract independent of log message text is
  required;
- periodic performance snapshots need to be exposed as structured
  data outside the log stream.

## Consequences

### Positive

- Logging adopts the standard .NET idiom (`ILogger<T>` DI everywhere) — the
  decisive rationale. (This is a deliberate
  migration from the shipped `RoamLog` façade, see "Current state and the
  migration" — not the from-scratch "consistent from the first phase" the
  original draft claimed.)
- The CLI host owns provider choice; the test harness can route logs
  into xUnit's `ITestOutputHelper` or capture them as JSONL without
  touching subsystem code.
- Hot-path guards prevent logging from introducing allocation
  pressure or latency in tight loops — important for the SFTP sync
  engine's per-file inner loop.
- (Metrics are deferred for v0 — see §6 — so they are not a v0 consequence; the
  user-facing observability signal is roadmap #6's sync stats + `roam diag`.)
- Structured logging makes the JSONL log file
  ([`../logging.md`](../logging.md)) trivially queryable.

### Negative

- Every subsystem must follow the log level conventions and hot-path
  guard discipline from the start.
- Source-generated logging via `[LoggerMessage]` adds ceremony to
  hot-path code (partial class, static method, specific parameter
  ordering).
- Metrics instrumentation requires thought about which measurements
  are meaningful before the implementation is fully built.

## Alternatives considered

### EventSource and ETW only

Rejected because EventSource is Windows-centric in practice and
significantly harder for consumers to integrate compared to
`ILogger`. It also lacks the ecosystem of structured-logging
providers that `ILogger` enables. `roam` targets Linux/macOS first;
ETW is a non-starter as the primary signal.

### Custom logging abstraction

Rejected because `ILogger` is the established .NET standard.
Introducing a project-specific logging interface would force
integrators to write adapters and would duplicate work the ecosystem
has already solved.

### Defer logging decisions to a polish phase

Rejected because retrofitting structured logging across multiple
subsystems is painful, produces inconsistent conventions, and risks
missing diagnostic context in the subsystems that need it most.

### `ActivitySource` and distributed tracing in v0

Deferred. Distributed tracing is designed for service-to-service
request correlation; `roam` is a one-shot CLI. If a future host
ever needs trace-context propagation (e.g., a `roam`-as-a-service
flavor), `ActivitySource` support can be added without disrupting
the `ILogger` and `Metrics` foundations.

## Quick reference

### Decision checklist for new code

1. **Is this a hot path?** (tight loop, per-item processing) → Use
   `[LoggerMessage]` source generation at `Trace` level.
2. **Is this a lifecycle event?** (start, stop, state change) → Use
   structured `ILogger` with the appropriate level from section 3.
3. **Is this a countable thing?** (files synced, retries, preflight
   failures) → *Deferred (§6): no metrics in v0.* The count belongs in
   roadmap #6's sync stats, not a `Counter`.
4. **Is this a measurable distribution?** (per-file latency, step
   duration) → *Deferred (§6): no metrics in v0.*
5. **Would an operator want to see this in normal terminal output?**
   → `Information` level (rendered by the per-step formatter in
   [`../logging.md`](../logging.md)).
6. **Would a developer only care during active debugging?** →
   `Debug` (lifecycle / planner output) or `Trace` (per-item).

### Template

> The `Meter` / `Counter` / `Histogram` lines below are **deferred** (§6); a v0
> subsystem is `ILogger<T>`-only. Drop them until metrics are revived.

```csharp
public sealed partial class SyncEngine
{
    private static readonly Meter SyncMeter = new("roam.sync", "0.1.0");

    private static readonly Counter<long> FilesSyncedCounter =
        SyncMeter.CreateCounter<long>("roam.sync.files_synced");

    private static readonly Histogram<double> FileLatencyHistogram =
        SyncMeter.CreateHistogram<double>("roam.sync.file_latency_ms");

    private readonly ILogger<SyncEngine> _logger;

    public SyncEngine(ILogger<SyncEngine>? logger = null)
    {
        _logger = logger ?? NullLogger<SyncEngine>.Instance;
    }

    public void SyncFile(string path, long bytes)
    {
        var sw = Stopwatch.StartNew();

        // ... transfer ...

        sw.Stop();
        FilesSyncedCounter.Add(1);
        FileLatencyHistogram.Record(sw.Elapsed.TotalMilliseconds);
        LogFileSynced(_logger, path, bytes, sw.Elapsed.TotalMilliseconds);
    }

    // ── Source-generated log methods ─────────────────────────────────

    [LoggerMessage(Level = LogLevel.Trace,
        Message = "Synced {Path} ({Bytes} bytes) in {ElapsedMs:F2}ms")]
    private static partial void LogFileSynced(
        ILogger logger, string path, long bytes, double elapsedMs);
}
```
