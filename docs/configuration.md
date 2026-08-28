# Configuration model

**Status:** v0 schema is frozen. The authoritative machine-readable
schema lives at [`roamfile.schema.json`](roamfile.schema.json); this
document is the human-readable companion. Changes to the schema bump
the `version` integer and are recorded in
[`implementation-contract.md`](implementation-contract.md). The earlier
"straw man" label has been retired — the shape described here is what
the v0 parser enforces.

## The question

Where does `roam`'s configuration live, and what's its relationship
with the files .NET and the editors already use to describe build
and run behavior?

## The short answer

`roam` has **its own config file** (`roamfile.yaml`). It still
**references the .NET launch profile by name**, but publish settings can
now live either in a small `publish:` block that `roam` owns or in a
legacy `.pubxml` referenced by `publish-profile:`. VSCode's `launch.json`
becomes a *generated artifact*, not a source of truth.

> **The decision boundary, phrased as a rule:**
>
> *If it's about how the app runs once started, it stays in the .NET
> launch profile.* `roam` references it by name.
>
> *If it's about which host runs which stage, how `dotnet publish`
> should shape the artifact, or how the deploy pipeline proceeds, it
> lives in `roamfile.yaml`.* That's `roam`'s reason to exist.

The rest of this document explains why.

## The existing files and what each one is good at

Before proposing anything new, inventory what's already on disk in a
typical .NET project:

### `Properties/launchSettings.json`

.NET-native profiles describing how to *run* the app. Each profile
carries `commandName`, `environmentVariables`, `commandLineArgs`, and
(for web) `applicationUrl`. Consumed by `dotnet run --launch-profile
X`, by every IDE's green "Run" button, and by anything else in the
.NET ecosystem that wants to know "what does 'Development' mode mean
for this project?"

**Authoritative for:** how the app runs once it's on the target host.

### `publish:` in `roamfile.yaml` (preferred)

A small Roam-owned publish contract. It captures the parts of `dotnet
publish` that matter for deploy orchestration without making users write
MSBuild XML:

- `rid`
- `self-contained`
- `configuration`
- optional `framework`

Roam turns this into the corresponding `dotnet publish` flags and writes
publish output to a deterministic Roam-managed path under the project
(`obj/roam/<profile>/publish`).

**Authoritative for:** default Roam-managed artifact shape.

### `Properties/PublishProfiles/*.pubxml` (legacy compatibility)

MSBuild publish profiles remain supported via `publish-profile:` for
projects that already rely on them or need richer MSBuild-specific
publish behavior.

### `.vscode/launch.json`

VSCode-specific debugger configuration. For .NET it defines `coreclr`
launch or attach configs with `program`, `args`, `cwd`, `env`, and
(critically for `roam`) `pipeTransport` — the SSH-based transport
that lets VSCode's debugger reach a process on another host. JetBrains
Rider has its own equivalent ("Remote Process" run configurations)
stored elsewhere.

**Authoritative for:** how VSCode specifically wires up the debugger.

### `.vscode/tasks.json`

VSCode task runner. Usually has a `build` task that calls
`dotnet build`, referenced from `launch.json` as `preLaunchTask`.
VSCode-specific.

### `.csproj` / `Directory.Build.props` / `global.json`

MSBuild-side project and SDK pinning. Authoritative for everything
about the project from MSBuild's perspective, including default RIDs,
assembly name, and target framework.

## Why `roam` can't just piggyback on an existing file

Each of these files is a good fit for *part* of the problem and a
bad fit for the rest. Going through them one by one:

### Why not extend `launchSettings.json`?

- It's a .NET-native schema. Adding host/sync/deploy fields means
  shipping keys that `dotnet run` will silently ignore, which is a
  footgun — you'll eventually have an IDE or CLI tool rewrite the
  file and drop the `roam` keys without warning.
- Its profiles are coupled to `commandName` (Project, Executable,
  IISExpress). They describe *local* run modes, not multi-host
  pipelines. Bolting "which SSH host" onto a commandName-shaped
  profile is a semantic mismatch.
- Even if the mismatch were solved, you'd still need somewhere for
  the pipeline steps (sync, deploy, restart). One file can't carry
  both cleanly.

### Why not extend `.pubxml`?

- It's MSBuild XML. You *can* add arbitrary properties and custom
  targets, and some teams do. But discoverability is awful, error
  messages are brutal, and asking humans to author MSBuild to
  configure a dev tool is a hostility.
- A single publish output can legitimately be deployed to several
  different targets with different env, different restart
  commands, and different debug configs. Publish-profile granularity
  is wrong for the per-target pipeline.

### Why not make `launch.json` the source of truth?

- VSCode-only. Rider users, CLI-only users, and anyone using a
  different editor get nothing.
- `launch.json` is about the debugger, not the build/deploy
  pipeline. Its schema has no concept of pipeline steps, hosts, or sync —
  you'd end up with a second file anyway, and now you have two
  sources of truth.
- Editors rewrite this file on their own (VSCode prompts to
  "generate configs"), which fights a tool that wants it stable.

### Why not a justfile / Taskfile / shell script?

This *is* what everyone uses today, and for small projects it's the
right answer. `roam` only earns its existence when the justfile
pattern starts hurting: multiple projects wanting to share the
pattern, teammates needing to read it cold, watch-mode debouncing
that survives network hiccups, debugger attach that the editor
picks up automatically. A justfile doesn't scale past the first
couple of those pains.

## The frozen v0 shape: `roamfile.yaml`

For a quick setup path, start with [`getting-started.md`](getting-started.md).
For copy/paste topology examples, see [`../examples`](../examples/README.md).
This section is the detailed schema companion.

`roam`'s config file composes the existing primitives rather than
replacing them. The schema is locked for v0:

```yaml
# roamfile.yaml
version: 1
project: KioskUi
solution: KioskUi.sln
# or, for a single-csproj project:
# csproj: src/KioskUi/KioskUi.csproj

hosts:
  laptop:
    ssh: laptop.tailnet.ts.net
    user: dev
    workspace: ~/src/kiosk-ui
    os: linux
  workstation:
    ssh: workstation.tailnet.ts.net
    user: dev
    workspace: ~/src/kiosk-ui
    os: linux
  kiosk-01:
    ssh: kiosk-01.tailnet.ts.net
    user: kiosk
    os: linux

profiles:

  dev-local:
    description: Publish and run everything on the laptop.
    source: laptop
    build:  laptop
    target: laptop
    publish:
      rid: linux-x64
      self-contained: true
      configuration: Debug
    launch-profile:  Development        # launchSettings.json::Development
    deploy:
      path: ~/apps/kiosk-ui
    debug:
      enabled: true
      debugger: vsdbg
      editor: vscode
      process-name: KioskUi

  workstation-to-laptop:
    description: Heavy publish on workstation, run on laptop.
    source: laptop
    build:  workstation
    target: laptop
    publish:
      rid: linux-x64
      self-contained: true
      configuration: Release
    launch-profile:  Development
    deploy:
      path: ~/apps/kiosk-ui
    debug:
      enabled: true
      debugger: vsdbg
      editor: vscode
      process-name: KioskUi

  kiosk:
    description: Push to the real kiosk hardware.
    source: laptop
    build:  workstation
    target: kiosk-01
    publish:
      rid: linux-arm64
      self-contained: true
      configuration: Release
    launch-profile:  Production
    env:
      DISPLAY_MODE: kiosk
    deploy:
      path: /opt/kiosk-ui
      flatten-publish: true
      stop:  systemctl --user stop kiosk-ui
      start: systemctl --user start kiosk-ui
      ready: systemctl --user is-active kiosk-ui
      ready-timeout: 20    # seconds, default 15
    debug:
      enabled: true
      debugger: vsdbg
      editor: vscode
      process-name: KioskUi
```

The parser is **strict**: unknown top-level keys, unknown host fields,
unknown profile fields, and unknown `deploy`/`debug` fields are
rejected with a `config` error (exit `3` — see
[`exit-codes.md`](exit-codes.md)). The same shape now also supports
`windows` for target hosts. Source and build roles remain Unix-only in
the current implementation; Windows support is intentionally limited to
the target role for deploy/run. This prevents silent schema drift and
forces future extensions through a deliberate version bump.

Each **profile** is a complete dev scenario — one noun for "what the
developer wants to do right now." A profile names:

- the three hosts (source / build / target),
- either a Roam-native `publish:` block or a legacy publish profile,
- the launchSettings profile to pass to the running binary,
- the deploy path on the target, service or one-shot run commands,
  and an optional service readiness check,
- whether to emit a debugger-attach config for this profile.

The pipeline for every profile is the same fixed sequence through
artifact sync: sync source → publish → stop → sync artifacts. After
that, service profiles start and poll readiness; one-shot profiles run
the command in the foreground and succeed when it exits with an allowed
code. Steps without a corresponding command (e.g., `stop` is omitted
for `dev-local`) are no-ops. See [`design.md`](design.md) section 2
for why this ordering is fixed.

Everything else (extra env vars, command-line args, debugger shape)
lives in the file that already owns it. Roam intentionally keeps the
publish contract small; if a project truly needs richer MSBuild-only
publish behavior, `publish-profile:` remains available as a compatibility
path.

## What `roam` owns vs. consumes vs. emits

| Concern                   | File                           | `roam`'s relationship          |
|---------------------------|--------------------------------|--------------------------------|
| Host assignments          | `roamfile.yaml`                | **owns**                       |
| Pipeline steps            | `roamfile.yaml`                | **owns**                       |
| Deploy path, run mode     | `roamfile.yaml`                | **owns**                       |
| Sync tool choice          | `roamfile.yaml`                | **owns**                       |
| RID / Configuration / AOT | `*.pubxml`                     | **consumes** (by profile name) |
| Env vars / CLI args       | `launchSettings.json`          | **consumes** (by profile name) |
| Target framework / SDK    | `.csproj` / `global.json`      | **consumes** (implicitly)      |
| VSCode debug attach       | `.vscode/launch.json`          | **emits** (generated artifact; see [`debugger.md`](debugger.md)) |
| Rider remote process      | Rider's run-config XML         | **emits** (future; see [`debugger.md`](debugger.md))            |
| Build task                | `.vscode/tasks.json`           | ignores — `roam run` is the task |

The rule of thumb: if a .NET tool or IDE already understands the
concept, `roam` references the existing file by name. If the concept
is inherently multi-host, it lives in `roamfile.yaml`. If the concept
is editor-specific, `roam` generates it.

## The `deploy.uninstall` block

`deploy.uninstall` is the symmetric reverse of `deploy.start`. It's a free-form
shell command that `roam uninstall <profile>` executes on the target. The
project owns the tear-down: stop the service, unregister the scheduled task,
remove the firewall rule, delete `deploy.path/`, etc. Roam wipes the local
warm-deploy manifest after the block succeeds (use `--keep-manifest` to keep
it).

```yaml
deploy:
  path: /opt/kiosk-ui
  start: systemctl --user start kiosk-ui
  uninstall: |
    systemctl --user stop kiosk-ui
    systemctl --user disable kiosk-ui
    rm -rf /opt/kiosk-ui
```

When `deploy.uninstall:` is unset, `roam uninstall` falls back to: run the
stop command, then recursively remove `deploy.path/`, then wipe the manifest,
with a warning. Fine for synthetic / one-off profiles where nothing else was
registered on the target; misleading for production profiles that registered
services or scheduled tasks the fallback can't see. Set `deploy.uninstall:`
explicitly for anything that isn't pure-files-in-a-directory.

The pair-model — `deploy.start` and `deploy.uninstall` edited in lockstep —
is the current convention. The receipt-based protocol where the install
script emits a structured side-effect manifest and roam reverses each entry
generically is backlogged for after 3+ workspace projects need paired
install/uninstall; see the issue tracker.

## The `deploy.provenance` block

`deploy.provenance` is a list of globs naming which deployed managed assemblies
roam reports versions for after a deploy. After `sync-artifacts`, roam reads each
matching assembly's `AssemblyInformationalVersion` (falling back to file then
assembly version) out of its PE/CLI metadata and prints a one-line diff against
the previous deploy:

```
  deployed versions:
    Contoso.Widgets.dll   1.5.1-alpha.1   ->  (unchanged)
    Fabrikam.Ui.dll       0.9.0           ->  0.9.1
```

```yaml
deploy:
  path: /opt/kiosk-ui
  provenance: ["Contoso.*", "Fabrikam.*", "Fabrikam.Ui.dll"]
```

Globs match the **file name** only and accept `*` / `?`. When omitted, roam
reports just the project's own primary output assembly (`<ProjectName>.dll/.exe`).
A `(unchanged)` line — same version *and* same bytes as the prior deploy — is the
red flag for a stale local-feed package: you rebuilt a library but the deployed
bytes didn't move. roam can only *surface* this (it never knows the version you
expected); it reads metadata without `Assembly.LoadFrom`, so it works on a foreign
win-x64 publish from any controller. The record persists at
`.roam/manifests/<profile>/deployed-versions.json` (see
[state.md](state.md)). The structural counterpart — forcing a republish when a
local-feed package is re-packed at the same version — is the schema-3 publish
fingerprint, also described in [state.md](state.md).

## Interactive desktop sessions and reboot durability

On a Windows target, `deploy.interactive-session: true` (or `run.interactive-session:
true`) makes `roam` wrap the start command in a scheduled task with `-LogonType
Interactive`, so the process runs in the logged-on user's desktop session (Session 1)
instead of the SSH services session (Session 0). Avalonia / WPF / Direct3D GUIs need
this; without it the process starts but cannot acquire a display. The task is named
`Roam_<profile>` and re-registered with `-Force` on every deploy.

By default that task is registered with an **action and a principal but no trigger**. It
is started immediately, so the workload comes up at deploy time, but nothing relaunches
it after a **reboot** — it stays down until the next `roam run`. On an unattended station
that autologons and is expected to recover on its own, that is a gap.

`interactive-session-trigger: at-logon` closes it. It is **opt-in** (unset = no trigger =
today's behavior) and valid on both `deploy:` and `run:`:

```yaml
deploy:
  path: C:/agent/service
  flatten-publish: true
  interactive-session: true
  interactive-session-trigger: at-logon   # relaunch on logon after a reboot
```

When set to `at-logon` and `interactive-session: true` on a Windows service-mode profile,
`roam` adds `New-ScheduledTaskTrigger -AtLogOn -User <target user>` to the scheduled task.
The trigger user is the same as the task principal (`hosts.<target>.user`), so the
workload relaunches the next time that user logs on. `at-logon` is the only value today;
the key is a string enum so a future `at-startup` can be added without a breaking change.

This relies on the target user actually logging on for the workload to return, so pair it
with **autologon** on a headless station. With the key unset the registration is
byte-for-byte unchanged from earlier roam versions, so existing interactive-session
profiles (launcher-managed profiles) are unaffected.

### Elevated interactive-session tasks: `run-level`

By default the interactive-session scheduled task is registered with a `-RunLevel Limited`
principal, so the workload runs **non-elevated** (standard-user / Medium IL token). That is
the right level for supervision, C2, and IPC, and it is what every existing
interactive-session profile gets. `run-level: highest` opts the task up to run **elevated
(High IL)** in the interactive desktop session. It is valid on both `deploy:` and `run:`:

```yaml
deploy:
  path: C:/agent/service
  flatten-publish: true
  interactive-session: true
  run-level: highest          # register the task elevated (High IL)
```

When set to `highest` and `interactive-session: true` on a Windows service-mode profile,
`roam` registers the task principal with `-RunLevel Highest`. Task Scheduler launches a
`-RunLevel Highest` **interactive** task without a UAC prompt when the principal user
(`hosts.<target>.user`) is a local administrator, so the elevated workload comes up in the
desktop session unattended. `limited` is the back-compat default; with `run-level` unset or
`limited` the principal line is byte-for-byte `-RunLevel Limited` as in earlier roam
versions. `run-level` is only consulted when `interactive-session: true`.

This supports an **elevated-supervisor + limited-workload** posture: an elevated
supervisor (deployed `run-level: highest`) launches the workload as the
workload's *own* `Limited` interactive task and triggers it with `schtasks /Run`. Lay that
workload task down without racing the supervisor to start it via `roam deploy` (below), which
**registers but does not start** the interactive-session task.

### Unix-target durability: `detach`

`interactive-session` and `run-level` are Windows-only. The Unix analog -- keeping a service
alive past the deploy -- is `detach`. `roam run` starts the process over an SSH channel that
closes the moment the `start` step returns; an inline foreground command dies with it.
`detach: true` on a **non-Windows** target wraps the start so it survives:

```yaml
deploy:
  path: /opt/app
  detach: true                # nohup + background so the service outlives the SSH channel
  start: dotnet App.dll
```

roam emits `nohup sh -c '<env> <start>' < /dev/null > /opt/app/roam-<profile>.out 2>&1 &`:
`nohup` ignores SIGHUP, redirecting all three std streams frees the SSH channel so it does not
block, and `&` backgrounds the job. stdout/stderr land in `<deploy.path>/roam-<profile>.out`
for diagnostics. It is **opt-in** (default `false` runs the start inline -- byte-for-byte
today's behavior, where the author's `start:` is expected to daemonize itself, e.g. via
`systemd-run`). Valid on `deploy:` and `run:`; **ignored on Windows targets** (which use the
interactive-session scheduled task) and for `roam deploy` register-without-start. It gives
**disconnect** durability, not **reboot** durability -- a rebooted host does not relaunch a
`detach`ed service; a systemd unit is the answer there (tracked in the issue tracker).

## The `run` block

`run:` is optional. When omitted, `roam` preserves the original v0
service behavior by treating `deploy.stop`, `deploy.start`, `deploy.ready`,
`deploy.ready-timeout`, `deploy.ready-interval-ms`,
`deploy.interactive-session`, `deploy.interactive-session-trigger`, and
`deploy.run-level` as service-run settings.

Use `run.mode: one-shot` for bounded console programs, smoke tests, data
migrations, clip-capture examples, and other commands that are supposed to
exit:

```yaml
profiles:
  clip-recorder:
    deploy:
      path: C:/Users/dev/clip-recorder
      flatten-publish: true
    run:
      mode: one-shot
      command: >
        C:/Users/dev/clip-recorder/Example.ClipRecorder.exe
        --log-file C:/Users/dev/clip-recorder/clip-recorder.log
        --output-dir C:/Users/dev/clip-recorder/clips
        --exit-after 20
      timeout: 60
      success-exit-codes: [0]
```

One-shot mode runs `command` on the target host in the foreground, applies
the merged launch-profile and `env:` variables, waits for completion, and
fails the deploy if the command exits with a code not listed in
`success-exit-codes` or exceeds `timeout`.

Use `run.mode: service` when you want new-style keys without legacy
`deploy.start`/`deploy.ready` names:

```yaml
run:
  mode: service
  stop: systemctl --user stop kiosk-ui
  command: systemctl --user start kiosk-ui
  ready: systemctl --user is-active kiosk-ui
  ready-timeout: 20
```

`deploy:` remains the artifact-deployment block. New profiles should put
process lifecycle under `run:`; existing profiles using `deploy.start` and
`deploy.ready` keep working.

## The `debug` block

The `debug` block is an **object**, not a boolean. It is shared by
`roam attach` (which consumes `enabled`, `debugger`, `editor`) and by
readiness (which consumes `process-name` — see
[`readiness.md`](readiness.md)).

| Field               | Type    | Default                | v0 constraint                       |
|---------------------|---------|------------------------|-------------------------------------|
| `enabled`           | bool    | `false`                | required when the block is present  |
| `debugger`          | string  | `vsdbg`                | v0 accepts only `vsdbg`             |
| `editor`            | string  | `vscode`               | v0 accepts only `vscode`            |
| `process-name`      | string  | csproj `AssemblyName`  | required if `enabled: true`         |
| `install-on-target` | bool    | `false`                | v0 accepts only `false`             |

`netcoredbg` and `rider` are legitimate post-v0 values (see
[`debugger.md`](debugger.md)); the v0 parser rejects them with a clear
"post-v0 feature" error rather than ignoring them.

## Emitted `launch.json`: treat it as a build artifact

`roam attach <profile>` writes (or rewrites) a `.vscode/launch.json`
stanza for that profile. The stanza uses `coreclr` in attach mode
with `pipeTransport` pointing at the profile's `target` host:

```jsonc
{
  "name": "roam: kiosk",
  "type": "coreclr",
  "request": "attach",
  "processName": "KioskUi",
  "pipeTransport": {
    "pipeProgram": "ssh",
    "pipeArgs": ["kiosk@kiosk-01.tailnet.ts.net"],
    "debuggerPath": "/home/kiosk/vsdbg/vsdbg",
    "quoteArgs": true
  },
  "sourceFileMap": {
    "/_/": "/home/dev/src/kiosk-ui"
  },
  "justMyCode": true
}
```

The `sourceFileMap` value is the **absolute path** of the source
host's workspace, resolved by `roam attach` at emit time. It is not a
VSCode variable like `${workspaceFolder}` — see
[`paths.md`](paths.md) for why.

Key properties of the emit:

- **Deterministic.** Same `roamfile.yaml` → same `launch.json`. Diffs
  reviewable in git.
- **Namespaced names.** Every emitted entry is prefixed `roam: `, so
  it's obvious which entries are generated and which were
  hand-authored. `roam attach` leaves other entries alone.
- **Idempotent.** Running `roam attach` twice in a row changes
  nothing.
- **Opt-in commit.** Teams can commit the generated `launch.json` for
  zero-config onboarding, or gitignore it and regenerate. Both work.

The same treatment applies to Rider and any future editor target:
`roam attach --editor rider` writes Rider's run-config XML. The
canonical config is always the YAML; editor files are downstream.

## Per-profile environment overlays

The optional `env:` block on a profile layers on top of the named
`launch-profile`'s `environmentVariables` from `launchSettings.json`:

- Any variable set in `env:` overrides the launchSettings value.
- Any variable in launchSettings but not in `env:` is passed through
  unchanged.
- The merged environment is set on the process `start` command, not
  injected into the binary itself.

This is the minimum viable answer to the per-target customization
problem ("the kiosk needs `DISPLAY_MODE=kiosk` that the laptop
doesn't") without introducing profile inheritance.

## Profile inheritance is rejected in v0

Profile inheritance (`extends: dev-local`) is a deliberate v1
feature. The v0 parser rejects `extends` with:

```
profile 'kiosk' uses 'extends: dev-local'; profile inheritance is a
v1 feature (see docs/implementation-contract.md).
```

Users write duplicated profiles in v0. Real duplication pain across
profiles is the signal that tells us whether to ship inheritance in
v1.

## What `roam init` should do

The first-run experience should read the existing project and
bootstrap a working `roamfile.yaml`:

1. Locate the solution or csproj.
2. Enumerate `Properties/PublishProfiles/*.pubxml` and
   `Properties/launchSettings.json` profiles.
3. For each discovered publish profile, create a starter `roam`
   profile that targets `laptop` (the host running `roam init`) as
   all three roles. This gives the user a working `dev-local` baseline
   with zero additional input.
4. Stub out a commented-out `workstation-to-laptop` and `kiosk` block
   so the user can see the shape of multi-host profiles without
   reading the docs first.
5. Infer `debug.process-name` from the csproj `<AssemblyName>` (or
   project name if `AssemblyName` is absent) and write it explicitly.
6. Write `roamfile.yaml` to the repo root.
7. Append `.roam/` to `.gitignore` (create if missing; refuse if
   `.roam/` is already tracked — see [`state.md`](state.md)).
8. Print next steps: "edit `hosts:` to add your workstation, then
   `roam run dev-local` to verify."

`roam init` does **not** modify `Directory.Build.props` or the csproj.
When the project has no `DeterministicSourcePaths` setting, `roam init`
prints a copy-pasteable snippet and the suggested file path; the user
decides whether to apply it. See [`paths.md`](paths.md).

The goal is that a user who runs `roam init` on an existing Avalonia
project can immediately do `roam run dev-local` and get a working
publish + launch, with no configuration other than what .NET already
had. Everything multi-host is then an additive edit.

## Open questions (post-v0)

1. **Profile inheritance.** Deferred to v1. See the section above.
2. **Multiple csprojs in one solution.** Does a `roam` profile
   target one csproj or multiple? v0 answer: one. A multi-project
   solution gets multiple profiles, one per project per scenario.
3. **Config discovery.** v0: walk up from cwd until finding
   `roamfile.yaml`, same as `git`. `--roamfile <path>` overrides.
4. **Secrets in deploy commands.** `roam` does not manage secrets.
   Use SSH agents or external secret tools; see
   [`security.md`](security.md).
5. **Richer schema validation errors.** The v0 parser reports the
   first schema violation; collecting all violations before exiting
   is a post-v0 refinement.

## Boundary refinement: workspace roots vs. deploy roots

The earlier drafts blurred two different concepts:

- **source/build workspace root** — where the repo lives on hosts that
  compile from source,
- **target deploy root** — where the published output is started from.

They should stay separate.

- `hosts.<name>.workspace` is for source/build hosts.
- `profiles.<name>.deploy.path` is the target landing path.
- A target host does not need a repo clone or a workspace just because
  it is a target.

This keeps the config model aligned with the actual job of each role and
avoids overloading one field to mean both "repo root" and "deploy
directory."

## V0 scope note

The schema described here *is* the v0 surface. The machine-readable
form lives at [`roamfile.schema.json`](roamfile.schema.json) and the
canonical example lives at
[`../tests/fixtures/SampleApp/roamfile.yaml`](../tests/fixtures/SampleApp/roamfile.yaml).
The implementation contract in
[`implementation-contract.md`](implementation-contract.md) references
this shape; any disagreement between the two documents should be
resolved in favor of the JSON Schema.

Inheritance, editor fan-out (Rider), richer sync modes, and
multi-project orchestration are intentionally postponed and are
rejected by the v0 parser with a clear "post-v0" error.

## What this document is

This document is the human-readable companion to
`roamfile.schema.json`. It explains *why* each field looks the way it
does; the schema is what the parser actually enforces. When the two
drift, treat the schema as authoritative and open a doc PR to
reconcile.
