# roam

**Build .NET on any host, run on any host, debug from anywhere.**

`roam` is a dev-loop orchestrator for .NET workflows where the machine
that edits the code, the machine that compiles it, and the machine that
runs it are three different computers — and you want the inner loop
(edit → `dotnet publish` → deploy → attach `coreclr` debugger) to feel
like you're working locally.

## Quickstart

### 1. Install

```bash
dotnet tool install -g Roam.Cli
roam --version
roam --help
```

The package id is `Roam.Cli`; the command it installs is `roam`.

To install a locally built package instead:

```bash
dotnet tool install -g Roam.Cli --add-source /path/to/packages --version <version>
```

### 2. Scaffold config in a .NET repo

```bash
cd /path/to/my-dotnet-app
roam init --csproj src/MyApp/MyApp.csproj
```

This creates `roamfile.yaml` and adds `.roam/` to `.gitignore`.

### 3. Edit `roamfile.yaml`

A minimal single-host Linux profile looks like this:

```yaml
version: 1
project: MyApp
csproj: src/MyApp/MyApp.csproj

hosts:
  local:
    ssh: localhost
    user: myuser
    workspace: /home/myuser/src/myapp
    os: linux

profiles:
  dev-local:
    description: Build and run on this machine.
    source: local
    build: local
    target: local
    publish:
      rid: linux-x64
      self-contained: true
      configuration: Release
    launch-profile: Development
    deploy:
      path: /home/myuser/apps/myapp
      flatten-publish: true
      stop: pkill -f '[M]yApp' || true
      start: nohup /home/myuser/apps/myapp/MyApp >/tmp/myapp.log 2>&1 &
      ready: pgrep -f MyApp >/dev/null
      ready-timeout: 20
    debug:
      enabled: true
      debugger: vsdbg
      editor: vscode
      process-name: MyApp
```

For copy/paste examples covering remote Linux builds, Linux targets,
Windows targets, and deploy-only profiles, see [`examples/`](examples/README.md).

### 4. Run and attach

```bash
roam run dev-local
roam attach dev-local
```

`roam run` executes the fixed pipeline: sync source → publish → stop →
sync artifacts → start → ready. `roam attach` writes a generated VSCode
`launch.json` entry for the profile. `roam diag` fetches a read-only
diagnostics bundle (logs, crash dumps) from the target — see
[Diagnostics](#diagnostics-roam-diag).

When you're done with a deployment, `roam uninstall <profile>` runs the
profile's `deploy.uninstall:` block on the target and wipes the local
warm-deploy manifest. The default fallback (stop process + remove
`deploy.path/` + wipe manifest) handles synthetic profiles; production
profiles that registered services or scheduled tasks should set
`deploy.uninstall:` explicitly.

For the full setup guide, see [`docs/getting-started.md`](docs/getting-started.md).

## Diagnostics: `roam diag`

`roam attach` is the *human* debug path — it emits a VS Code `launch.json` so the
Microsoft C# extension can drive `vsdbg` against the remote process. `roam diag`
is the *agent* (and headless) path: a **read-only** verb that fetches a
diagnostics bundle from the target into `.roam/diag/<profile>/<run-id>/`, plus a
machine-readable `diag.json` index.

```bash
roam run kiosk              # deploy + start
roam diag kiosk             # fetch logs into a bundle; print a summary
roam diag kiosk --json      # ...and print the diag.json index to stdout
roam diag kiosk --dump      # also fetch crash minidumps
```

Two tiers, both **inside the [provisioning boundary](docs/provisioning-boundary.md)**
(read-only on the target's host state — roam installs nothing to capture them):

- **Logs** (default): the roam-redirected process output
  (`roam-<profile>.out`, for `detach` / service-mode), any operator-named
  `deploy.diag.logs:` files — literal names **or `*`/`?` glob patterns**
  (matched in the entry's final path segment, e.g. `app-*.log` for
  timestamped logs; case-insensitive against a Windows target), and
  `journalctl -u <unit>` when a unit is named — pulled over SFTP/SSH.
- **Crash dumps** (`--dump`): with `deploy.diag.crash-dumps: true`, roam sets
  `DOTNET_DbgEnableMiniDump` at start so the runtime's built-in `createdump`
  (already in every self-contained publish — no extra tooling) writes a minidump
  on an unhandled crash; `--dump` fetches it.

Configure it under `deploy.diag` in `roamfile.yaml`:

```yaml
    deploy:
      path: /opt/kiosk
      detach: true
      diag:
        logs: [kiosk.log, "kiosk-*.log"]  # literal names and/or *,? globs, relative to deploy.path
        crash-dumps: true                 # runtime minidumps on crash
```

The `diag.json` index gives an agent a structured map of every artifact (`kind`,
`target_path`, `local_path`, `bytes`, `sha256`), so one `roam diag --json` is the
read-only analogue of a human hitting F5. The design — and why an agent gets a
fetchable bundle instead of an interactive debugger — is
[ADR-0002 (agent-first usability)](docs/adr/0002-agent-first-usability.md).
Verified end-to-end across the controller × target matrix (Windows and Linux
controllers → Windows and Linux targets).

## Catching stale local-feed packages

The cross-repo inner loop — rebuild a library (`Contoso.*`, `Fabrikam.*`),
`dotnet pack` it to a local folder feed, let a consumer float to it, `roam run`
the consumer — has a footgun: the deployed bytes can be **stale** even though
every version coordinate looks right. roam guards it from two angles.

**1. Deploy provenance (surfaces it).** After each deploy, roam reads the
`AssemblyInformationalVersion` of every synced managed assembly matching
`deploy.provenance:` and prints a one-line diff against the previous deploy,
flagging any whose version *and* bytes did not change:

```
  deployed versions:
    Contoso.Widgets.dll   1.5.1-alpha.1   ->  (unchanged)
    Fabrikam.Ui.dll       0.9.0           ->  0.9.1
```

That `(unchanged)` is the tell: you expected a rebuilt library to change and it
didn't. roam reads the version straight out of PE/CLI metadata (never
`Assembly.LoadFrom`), so it works on a foreign win-x64 publish from any
controller. It can only *surface* — it never knows the version you expected — but
that's the win: an invisible non-event becomes a visible line. Configure the
scope (defaults to the project's own assembly):

```yaml
    deploy:
      path: /opt/kiosk
      provenance: ["Contoso.*", "Fabrikam.*", "Fabrikam.Ui.dll"]
```

The full record is persisted at `.roam/manifests/<profile>/deployed-versions.json`
(see [`docs/state.md`](docs/state.md)).

**2. Content-keyed local-feed packages (prevents one half structurally).** A
package re-packed at the *same* version is invisible to the publish-skip
fingerprint, because NuGet's cache keeps the old extraction and
`project.assets.json` records the old sha512 — so roam can skip publish *and* sync
stale bytes. The schema-3 fingerprint folds the actual `.nupkg` **file** hash of
every resolved package that lives in a local **folder** feed, so a same-version
re-pack forces a republish. HTTP-feed packages (nuget.org, GitHub Packages) are
untouched — their version is immutable. This fixes the *skip-publish* half; it
does not bypass NuGet's global-cache extraction itself (a forced/clean restore is
the separate cure, tracked in the issue tracker).

## Scope: .NET, end to end

`roam` is deliberately narrow:

- **Built for .NET.** The only build verb it understands is
  `dotnet publish` (with its RID, configuration, and self-contained
  flags). The only debugger it wires up is `coreclr` / `vsdbg`. The
  motivating workload is Avalonia desktop apps; other .NET workloads
  (ASP.NET services on an edge box, console tools on a kiosk, WPF on
  a remote Windows target) fall out for free because they all share
  the same publish + attach shape.
- **Built *using* .NET.** The tool itself is a .NET application. The
  audience already has the SDK installed on at least one host, and
  writing the tool in the same stack it targets means the team
  dogfoods its own publish/deploy loop from day one.
- **Shipped as a `dotnet tool`.** The intended distribution is
  `dotnet tool install -g Roam.Cli`, so installation on any host with a
  .NET SDK is one command and upgrades ride the normal NuGet
  channel. No separate package manager, no per-OS installer matrix.

The non-.NET adjacent spaces (Go + `delve`, Rust + `gdbserver`, Python
+ `debugpy`, generic shell build steps) are **explicitly out of scope**.
If someone wants those, they can fork or build a sibling tool; keeping
`roam` single-language is how it stays small and opinionated.

## The problem

Most dev tooling assumes edit, build, and run happen on the same box.
That assumption breaks the moment any of these is true for a .NET app:

- The code needs a **real GUI** (Avalonia, WPF) and can't be run
  headlessly on the beefy remote workstation where you'd rather
  compile it.
- The code has to run on a **constrained edge device** (kiosk, Jetson,
  Raspberry Pi, industrial PC) that can't host the .NET SDK you build
  with — but *can* run a self-contained publish output.
- Your **laptop battery and fans** don't want to host a 5-minute
  `dotnet publish` every time you hit save, but your workstation at
  home is happy to cross-compile `--self-contained` for your
  laptop's RID.
- You want to **attach the `coreclr` debugger** from VSCode or Rider
  on host A to a process running on host C, where the binary was
  published on host B.

Today you solve this with an ugly pile of `just` recipes, hand-rolled
`rsync` invocations, SSH `pipeTransport` blocks in `launch.json`, and a
lot of tribal knowledge about which host is which. `roam` is the
hypothesis that this pile deserves to be one .NET tool with one config
file.

## The mental model: source / build / target

Every dev loop `roam` cares about is a three-tuple of roles:

| Role       | What it does                                       | Typical host                             |
|------------|----------------------------------------------------|------------------------------------------|
| **source** | Where the code lives and is edited                 | Laptop, or a remote workstation          |
| **build**  | Where `dotnet publish` runs                        | Beefy workstation, CI runner, laptop     |
| **target** | Where the published binary runs (and is debugged)  | Laptop, kiosk, Jetson, remote server     |

Any of the three can be the same machine, or all three can be
different. The build host cross-compiles for the target host's RID
(`linux-arm64`, `osx-arm64`, `win-x64`, etc.) with
`--self-contained`, so the target doesn't need the SDK at all — only
the CLR the publish output carries with it.

Today these roles are hardcoded into whichever script you wrote last;
`roam` makes them declarative and swappable, so switching "build on my
workstation" to "build on my laptop" is a one-line config change.

## Non-goals

`roam` is explicitly **not**:

- A CI/CD system. It doesn't replace GitHub Actions, Jenkins, or
  Argo. Its scope is the .NET inner dev loop, not production releases.
- A polyglot dev-loop tool. One language, one debugger, one publish
  verb. If you want Go or Rust, use something else.
- A Kubernetes dev tool. Tilt, Skaffold, DevSpace, and Garden exist
  and are great at what they do. `roam` targets raw hosts reachable
  over SSH, not pods.
- A container orchestrator. If your target is a container, fine, but
  `roam` treats it as an opaque host and doesn't know about images,
  registries, or pods.
- A general-purpose file-sync tool. Mutagen and rsync already do that
  well; `roam` uses SSH.NET/SFTP for its own manifest-scoped deploy
  pipeline, not as a standalone sync product.
- A package manager or SDK installer. The target must already have
  whatever runtime the self-contained publish doesn't bundle (which
  for a `--self-contained` publish is basically nothing — but Avalonia
  still needs native GUI libs on Linux, and that's on the operator).

The narrowness is the pitch. HashiCorp Waypoint tried to be a similar
any-build-any-deploy tool for *every* stack and got archived in 2024
without finding an audience; `roam` is for one specific shape: "I am
looping on a .NET desktop or edge app and the dev machines are
physically not the same as the run machine."

## Status

Pre-1.0, and in daily use. `roam` is a .NET 10 console app in `src/Roam/`
with working `init`, `run`, `deploy`, `attach`, `uninstall`, and `diag`
command paths,
strict `roamfile.yaml` loading, project metadata resolution, SSH host
resolution, SSH.NET/SFTP transport, metadata-diffed sync, `.roam/`
state persistence, and VS Code `launch.json` emission.

Verification has been exercised at multiple tiers:

- unit tests for config loading, sync ownership, SSH identity
  diagnostics, and version reporting
- default integration smoke tests that do not require Docker
- opt-in xUnit Compose E2E lane for Linux source → remote Linux build →
  remote Linux target, including stale-owned deletes, unmanaged file
  preservation, nested publish artifacts, mtime preservation, and temp
  cleanup
- live Windows target dogfood from a Linux source/build path

See:


### Design and architecture

- [`docs/design.md`](docs/design.md) — full design sketch (fixed
  pipeline, profile-based config, recommended subsystem boundaries).
- [`docs/getting-started.md`](docs/getting-started.md) — install,
  scaffold, edit `roamfile.yaml`, run, attach, and troubleshoot the
  first profile.
- [`examples/`](examples/README.md) — copy/paste `roamfile.yaml`
  starting points for common host topologies.
- [`docs/implementation-contract.md`](docs/implementation-contract.md) —
  frozen v0 feature set and planned v1/v2 expansion.
- [`docs/configuration.md`](docs/configuration.md) — the `roamfile.yaml`
  config model and how it relates to `publish:` / legacy `.pubxml` /
  `launchSettings.json` / `launch.json`.
- [`docs/roamfile.schema.json`](docs/roamfile.schema.json) — the
  machine-readable v0 schema. Canonical example at
  [`tests/fixtures/SampleApp/roamfile.yaml`](tests/fixtures/SampleApp/roamfile.yaml).
- [`docs/paths.md`](docs/paths.md) — workspace roots, deploy roots,
  git-tracked sync scope, delete semantics, and PDB source paths.
- [`docs/transport.md`](docs/transport.md) — SSH.NET everywhere,
  `ssh -G` for config, SFTP metadata diffing for sync.
- [`docs/platform-readiness.md`](docs/platform-readiness.md) — what
  platform combinations have actually been verified and what still
  needs hardening.

### v0 surface contracts

- [`docs/cli.md`](docs/cli.md) — subcommands, flags, and golden
  `--help` output.
- [`docs/preflight.md`](docs/preflight.md) — the itemized preflight
  check list with pass/fail conditions.
- [`docs/exit-codes.md`](docs/exit-codes.md) — the failure taxonomy
  and stderr exit suffix.
- [`docs/logging.md`](docs/logging.md) — stdout/stderr formats,
  verbosity flags, and the JSONL log-file shape.
- [`docs/state.md`](docs/state.md) — the `.roam/` directory layout,
  manifests, and gitignore handling.
- [`docs/readiness.md`](docs/readiness.md) — how `roam` verifies the
  target process started after deploy.
- [`docs/debugger.md`](docs/debugger.md) — `vsdbg` vs. `netcoredbg`
  and the licensing reality that shapes v0's choice.

### Delivery, testing, and operations

- [`docs/packaging.md`](docs/packaging.md) — why `roam` is a `dotnet
  tool` and what does (and doesn't) ship in the NuGet package.
- [`docs/test-architecture.md`](docs/test-architecture.md) — unit and
  Compose integration test tiers.
- [`docs/security.md`](docs/security.md) — trust model and boundaries
  around SSH, remote commands, and generated artifacts.
- [`docs/provisioning-boundary.md`](docs/provisioning-boundary.md) — why
  `roam` diagnoses target environment assumptions but does not become
  Terraform/Ansible/package management.
- [`docs/prior-art.md`](docs/prior-art.md) — adjacent tools and why
  none of them quite fit.

### Architecture decision records

- [`docs/adr/0001-logging-and-diagnostics-strategy.md`](docs/adr/0001-logging-and-diagnostics-strategy.md) —
  `ILogger<T>` discipline and `[LoggerMessage]` for hot paths (the target for
  the in-progress migration off the `RoamLog` façade; metrics deferred). The
  code-level companion to [`docs/logging.md`](docs/logging.md).
- [`docs/adr/0002-agent-first-usability.md`](docs/adr/0002-agent-first-usability.md) —
  treat agents as first-class consumers; prioritize machine-consumable
  diagnostics (the `roam diag` log/dump/trace bundle, `--json` output) over
  interactive debugger attach for them. The design behind `roam diag`.

### Progress tracking

- [`docs/implementation-plan.md`](docs/implementation-plan.md) — the
  slice-by-slice v0 delivery plan with current status.
- [`docs/roadmap.md`](docs/roadmap.md) — prioritized engineering
  backlog for cross-platform confidence, transport hardening, and
  release readiness.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for how to build, test, and what fits the
scope. Security issues have their own path — see [SECURITY.md](SECURITY.md).

## Licence

[MIT](LICENSE) © Charles Lee
