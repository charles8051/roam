# ADR-XXXX: roamfile v2 — remove the ceremony

**Status:** Proposed (2026-08-28). Number assigned at merge per
[`README.md`](README.md).

> Companion to [`../configuration.md`](../configuration.md), which is the
> human-readable spec for the schema, and to
> [`../roamfile.schema.json`](../roamfile.schema.json), which is authoritative.
> This ADR proposes the `version: 2` bump those two documents would describe.

## Context

The v0 schema (`version: 1`) is frozen and the parser is strict: unknown keys at
any level are a `config` error (exit 3). That strictness was deliberate — it
prevents silent schema drift and forces extensions through a version bump. It
also means every field in the schema is a field an author must consider, and
several fields in the schema do not earn that consideration.

The immediate trigger was a request to shorten the minimal roamfile. The
README's single-host example is 30 lines for one project on one machine. Reading
the loader and the command layer end to end to find defaults surfaced a second,
larger problem: some of that length is fields that could default, and some of it
is fields that do nothing, or do something other than what they read like.

Defaults fix the first category. Only a schema change fixes the second.

### What the code actually does

**`source:` is not a host.** `roam run` resolves the source host with
`isLocal: true` unconditionally
([`RoamCommands.cs:253`](../../src/Roam/RoamCommands.cs:253)), and the resulting
`HostResolution` is then read only for step labels and one error message. Its
`ssh:`, `user:`, `port:`, and `identity-file:` are never used to open a
connection, and its `workspace:` is never read — the workspace root comes from
the roamfile's own directory
([`ProjectMetadataResolver.cs:10`](../../src/Roam/ProjectMetadataResolver.cs:10)).

What `source` genuinely does is act as the name that the other two roles compare
themselves against to decide whether they are local
([`RoamCommands.cs:254`](../../src/Roam/RoamCommands.cs:254)):

```csharp
var buildHost  = await _ssh.ResolveAsync(profile.Build,  ..., isLocal: profile.Build  == profile.Source, ...);
var targetHost = await _ssh.ResolveAsync(profile.Target, ..., isLocal: profile.Target == profile.Source, ...);
```

So `source:` is a five-line host block plus a per-profile reference that encodes
one bit: which host name means "here". Writing `source: workstation` while
running roam from a laptop does not read files from the workstation; it reads
the laptop's files and labels them `workstation`. The `--source` override on
`run` and `deploy` has the same shape.

**`deploy:` and `run:` are the same block, and only one of them executes.**
Every lifecycle field is read from `profile.Run.*` at execution time — start,
stop, ready, timeouts, interactive-session, run-level, detach. Nothing reads
`profile.Deploy.Stop`, `.Start`, `.Ready`, `.ReadyTimeoutSeconds`,
`.ReadyIntervalMilliseconds`, `.InteractiveSession`,
`.InteractiveSessionTrigger`, `.RunLevel`, or `.Detach`. Those nine fields exist
only to be copied into a `RunSpec` when `run:` is absent
([`ConfigLoader.cs:254`](../../src/Roam/ConfigLoader.cs:254)).

When `run:` *is* present the copy does not happen, and the fallback is
inconsistent about it. `ready-timeout` and `ready-interval-ms` fall back to the
deploy block ([`ConfigLoader.cs:285`](../../src/Roam/ConfigLoader.cs:285));
`stop`, `ready`, `interactive-session`, `interactive-session-trigger`,
`run-level`, and `detach` reset to their hardcoded defaults. A working profile
with `deploy.detach: true` that gains a `run:` block loses detach silently.

A `run:` block in service mode with no `command:` is also a silent no-op: the
start step returns early
([`RoamCommands.cs:1081`](../../src/Roam/RoamCommands.cs:1081)), readiness
passes, exit 0, nothing running.

**Readiness is configured under `debug:`, and skips silently.** With no
`deploy.ready`, the probe falls back to polling for `debug.process-name`; with
that unset too, `WaitForReadinessAsync` returns `"skipped"` and the step reports
success without checking anything
([`RoamCommands.cs:1185`](../../src/Roam/RoamCommands.cs:1185)). The schema
documents `process-name` as defaulting to the csproj `AssemblyName`; no code
implements that fallback. So readiness — a deploy concern — is spelled inside
the debugger block, and the one thing that would make it safe to omit is
documented but absent.

**`solution:` is dead.** `ResolvedProjectPaths.SolutionPath` is assigned
([`ProjectMetadataResolver.cs:65`](../../src/Roam/ProjectMetadataResolver.cs:65))
and never read anywhere in the codebase. The solution branch of
`ResolveProjectPaths` searches for csproj files and errors unless exactly one
survives, so `solution:` is `csproj:` with more ways to fail. `project:`,
documented as "Informational only", is in fact the disambiguating selector for
that search — the one case where it is load-bearing is the case the branch
mostly cannot handle anyway.

**Three debug fields have one legal value each.** `debug.debugger` must be
`vsdbg`, `debug.editor` must be `vscode`, and `debug.install-on-target` must be
`false` ([`ConfigLoader.cs:310`](../../src/Roam/ConfigLoader.cs:310)).
`roam attach` additionally rejects a profile that leaves the first two unset
([`RoamCommands.cs:663`](../../src/Roam/RoamCommands.cs:663)), because it
compares them against the literal rather than treating null as the default. So
two of the three must be typed out in full, to say the only thing they can say.

## Decision

Ship `version: 2` with six changes. Four remove schema surface that carries no
information; two make an existing default reachable.

The optional-field defaults (§Defaults, below) **have already shipped against
`version: 1`**, because they are backward compatible and needed no version bump.
They are kept here so the resulting file shape is legible in one place. Their
authoritative description is
[`configuration.md`](../configuration.md#defaults).

`version: 1` continues to parse unchanged for at least one minor release.

### 1. Remove `source:`. Local is where roam runs.

Delete the `source` role from profiles, the `--source` override from `roam run`
and `roam deploy`, and the requirement to declare a host block for the machine
you are sitting at. `build:` and `target:` default to the reserved host name
`local`; a host is local if and only if its name is `local`.

`local` becomes reserved: a `hosts:` entry named `local` is a config error, so
"local" cannot be quietly pointed at a remote machine. Everything the source
host supplied operationally — the workspace root — already comes from the
roamfile's directory, so nothing is lost.

### 2. One lifecycle block.

Merge `run:` into `deploy:`. There is one runtime spec, populated from one YAML
block, so there is no precedence to get wrong and no field that silently resets.

- `deploy.start` is the service-mode command. `run.command` is dropped;
  one-shot mode uses `deploy.start` too.
- `deploy.mode` carries `service` (default) or `one-shot`.
- `deploy.timeout` and `deploy.success-exit-codes` move over from `run:`, and
  are rejected unless `mode: one-shot`.
- `mode: service` with no `start` is a config error, not a silent no-op.

`RunSpec` and `DeploySpec` collapse into one record in
[`Models.cs`](../../src/Roam/Models.cs).

### 3. `csproj:` only.

Drop `solution:` and `project:`. `csproj:` is optional and auto-discovered: the
single `.csproj` under the roamfile directory, excluding `bin/` and `obj/`.
Ambiguity is a config error naming the candidates, which is the current
solution-branch failure mode made explicit and reachable without a dead field.

This removes the top-level `oneOf` and the unread `SolutionPath`.

### 4. `debug:` keeps only `enabled`.

Drop `debugger`, `editor`, and `install-on-target`. Each has exactly one legal
value, so none of them is a choice. `roam attach` emits a VS Code `vsdbg` attach
config, which is what it already does; when a second debugger or editor lands it
arrives as a real enum with more than one member, in whatever version is current
then.

`debug: {enabled: true}` shortens to `debug: true` as a scalar.

### 5. `process-name` moves to the profile and gets its default.

Readiness is not a debugger concern. `process-name` becomes a profile-level
field defaulting to the csproj `AssemblyName` (falling back to the csproj file
name), which is what the v1 schema already claims. `roam attach` reads the same
field for the emitted `processName`.

With a real default, the default readiness probe fires for every profile that
omits `ready`. Readiness never silently skips. A profile that genuinely wants no
probe writes `ready: false`.

This is the one change in the set with a behavioral consequence for existing
profiles, and it is the point of the change: today those profiles report `ready`
without having checked.

### 6. `publish:` is the default, `publish-profile:` an override.

Drop the profile-level `oneOf`. `publish:` with a defaulted `rid` applies when
neither is set; `publish-profile:` overrides it wholesale and is an error only
when combined with an explicit `publish:` block.

### Defaults

Shipped against `version: 1`; reproduced here for context. Under v2 the
`source`, `build`, and `target` rows collapse into §1, and the `process-name`
row into §5.

| Field | Default when omitted |
|---|---|
| `version` | `1` today, `2` after the bump |
| `csproj` | the single csproj under the roamfile directory (§3) |
| host body | empty is legal; `ssh:` already falls back to the host key, and `user`/`port`/`identity-file` to `ssh -G` |
| `hosts` | omit entirely — the reserved `local` host is implicit (§1) |
| `build`, `target` | `local` (§1) |
| `workspace` | the roamfile directory for `local`; `~/.roam/src/<project>` for a remote build host |
| `publish` | `{rid: <target os>-<controller arch>, self-contained: true, configuration: Release}` (§6) |
| `publish.rid` | same, inside an explicit `publish:` block. `configuration` is *not* defaulted there — an explicit block that omits it keeps dotnet's default, so no existing profile changes what it builds |
| `launch-profile` | the first profile in `launchSettings.json`; no launch profile when the file is absent, rather than today's hard error at [`ProjectMetadataResolver.cs:161`](../../src/Roam/ProjectMetadataResolver.cs:161) |
| `deploy.path` | `<workspace>/.roam-dev` locally, `~/.roam/apps/<project>` remotely |
| `process-name` | csproj `AssemblyName` (§5) |

### What v2 looks like

Minimal, one machine, everything defaulted:

```yaml
profiles:
  dev-local:
    deploy:
      start: ./MyApp
```

The README's current 30-line example, in full:

```yaml
profiles:
  dev-local:
    deploy:
      start: ./MyApp
      stop: pkill -f "[M]yApp" || true
      detach: true
    debug: true
```

Three hosts, nothing defaulted away that matters:

```yaml
csproj: src/EdgeWorker/EdgeWorker.csproj

hosts:
  builder:
    ssh: buildbox.tailnet.example
    workspace: /home/dev/src/edge-worker
  edge-01:
    ssh: edge-01.tailnet.example
    user: edge

profiles:
  edge:
    description: Build on the build box, deploy to the edge host.
    build: builder
    target: edge-01
    publish:
      rid: linux-arm64
    launch-profile: Production
    deploy:
      path: /opt/edge-worker
      flatten-publish: true
      stop: sudo systemctl stop edge-worker || true
      start: sudo systemctl start edge-worker
      ready: systemctl is-active --quiet edge-worker
      ready-timeout: 45
```

## Consequences

**The strict parser stays strict.** Nothing here loosens unknown-key rejection.
v2 has fewer keys, not laxer ones.

**Two parsers for one release.** `ConfigLoader` dispatches on `version` and
keeps the v1 path intact. The v1 path is a shim that produces v2 records:
`source:` collapses to the local-name comparison it already performs, `run:`
merges into the single lifecycle spec with today's precedence preserved
verbatim, `solution:` resolves to a csproj. This is the mechanical part and it
is where the migration risk sits — the v1 shim must reproduce the *current*
`run:`/`deploy:` fallback inconsistency, bugs included, or v1 files change
behavior on upgrade.

**One v1 behavior does change on migration, deliberately.** A v1 profile with no
`ready` and no `debug.process-name` skips readiness today. Migrated to v2 it
gets the assembly-name probe and can now fail at the `ready` step. That is the
correct outcome and it should be called out in the migration notes, because it
turns a silent pass into a visible failure for anyone whose start command was
never actually working.

**`roam init` shrinks.** The scaffold at
[`RoamCommands.cs:80`](../../src/Roam/RoamCommands.cs:80) is a single
interpolated string producing 25 lines; under v2 it produces the minimal file
plus a commented block showing what can be set.

**Docs that move:** [`configuration.md`](../configuration.md) (the schema
companion, and its "owns vs. consumes vs. emits" table),
[`roamfile.schema.json`](../roamfile.schema.json),
[`getting-started.md`](../getting-started.md),
[`../../README.md`](../../README.md), all four files under
[`../../examples`](../../examples), the three `tests/labs/xplat` roamfiles, and
[`implementation-contract.md`](../implementation-contract.md), which records
schema versions.

**A migration command is worth writing.** `roam migrate` reads a v1 roamfile and
writes the v2 equivalent, reporting the readiness change above. Without it the
v1 shim has to live indefinitely.

## Alternatives considered

**Defaults only, no version bump.** This was the original request, it does real
work — the minimal file drops from 30 lines to 4 — and it has shipped. It leaves
every gotcha in §Context in place, and `deploy.path` now defaults next to a
`deploy:` block whose lifecycle fields are the actual problem. A first step, not
a substitute.

**Fix the bugs, keep the schema.** Make `run:` fall back to `deploy:`
consistently, error on `mode: service` with no start, implement the
`process-name` default. This is smaller and fixes the sharp edges, but it
preserves two blocks that are one block, a host role that is not a host, and a
dead top-level field. The schema stays as long as v0's, and the next author
still has to learn why `source:` exists.

**Merge `deploy:` into `run:` rather than the reverse.** `run:` is the newer
block and its name matches `roam run`. But `deploy.path` and `flatten-publish`
are the fields every profile sets, `roam deploy` is a real command that stops
before the run steps, and the deploy block is what all existing files are built
around. Merging into `deploy:` is the smaller diff for authors.

**Infer `build`/`target` from a single `host:` key.** A profile that names one
host for everything could write `host: edge-01`. Rejected: it adds a third
spelling of the same thing next to `build:` and `target:`, and with both
defaulting to `local` the single-host case already writes nothing.

## Open questions

**Default `start` / `stop` / `ready` from the deployed entrypoint.** Start
`<deploy.path>/<AssemblyName>` detached, stop by killing that path, ready when
the process is alive. That would reduce the minimal profile to `dev-local: {}`.
It is a behavior default rather than a schema change, it is guessable wrongly
for anything behind systemd or a scheduled task, and it can land independently.
Deferred, not rejected.

**`flatten-publish` default.** All four shipped examples and all three lab
roamfiles set it to `true`; the default is `false`. Flipping it is a one-line
change with a real blast radius for anyone relying on the nested layout.
Deferred to its own decision.

**Does `publish-profile:` survive v2?** It is a documented legacy compatibility
path for `.pubxml`. Nothing in-repo uses it except the fixture at
[`../../tests/fixtures/SampleApp/roamfile.yaml`](../../tests/fixtures/SampleApp/roamfile.yaml).
Keeping it costs one field and one branch in `ResolvePublishSettings`; that
seems cheap enough to keep, but it is worth confirming rather than assuming.

**Windows source/build.** [`platform-readiness.md`](../platform-readiness.md)
flags source and build roles as Unix-only. Removing `source:` makes "the
controller is the source" explicit, which sharpens the question of what a
Windows controller means. Out of scope here; noted because §1 touches the same
ground.
