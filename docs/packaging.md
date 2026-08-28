# Packaging and distribution

**Status:** load-bearing decision. This document records why `roam`
is distributed as a normal framework-dependent `dotnet tool`, what
the package does and doesn't contain, and how the debugger
constraints in [`debugger.md`](debugger.md) interact with packaging.
Spoiler: they don't.

## The goal

One command to install:

```bash
dotnet tool install -g Roam.Cli
```

One command to upgrade:

```bash
dotnet tool update -g Roam.Cli
```

One command to remove:

```bash
dotnet tool uninstall -g Roam.Cli
```

Works on any host with a compatible .NET SDK, regardless of OS or
architecture. The NuGet package id is `Roam`; the installed command is
`roam`. No native installers, no per-platform packages, no
curl-pipe-bash bootstrap scripts. The install story should feel
exactly like `dotnet-ef` or `dotnet-format`: boring, predictable,
and identical on every machine.

## Why a `dotnet tool`, specifically

Alternatives and why each is worse:

- **Standalone single-file binary per OS/arch.** Would require
  publishing `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`,
  `win-x64` artifacts on every release, plus a curl-style install
  script, plus signed notarization for macOS, plus a Windows code
  signing story. All of that is work we don't need to do if the
  user already has the .NET SDK — and the user will almost
  certainly have it, because `roam`'s whole reason for existing is
  .NET dev loops.
- **Homebrew / apt / winget packages.** Three separate packaging
  ecosystems, three separate update cadences, three separate
  approval processes. Worthwhile for tools that target users
  *without* the .NET SDK. Not worthwhile here.
- **Docker image.** Great for CI, useless for a dev-loop tool the
  user runs interactively against their laptop. The whole point is
  "run `roam watch kiosk` in a terminal and have it feel native."
- **Script + dotnet SDK invocation (`dotnet run`).** Works for
  local development of `roam` itself; not a distribution story.

The `dotnet tool` mechanism gives us cross-platform install, global
or project-local install, automatic NuGet-based updates, stable
uninstall, and a well-understood upgrade path for everyone in the
target audience. It's the path of least resistance and the one that
aligns best with users who already think in NuGet terms.

## What the NuGet package contains

The short answer: **nothing but managed .NET assemblies.** The
concrete dependency shape for v0:

| Concern                    | Package                                | Why                                   |
|----------------------------|----------------------------------------|---------------------------------------|
| YAML config parsing        | `YamlDotNet`                           | Most stable YAML lib on NuGet         |
| SSH / SFTP client          | `SSH.NET`                              | Pure managed, works on Windows; single library for command execution, file transfer, and port forwarding — see [`transport.md`](transport.md) |
| File system watching       | `System.IO.FileSystemWatcher` (BCL)    | Built in                              |
| CLI parsing                | `System.CommandLine`                   | Microsoft's supported modern CLI lib  |
| JSON emission              | `System.Text.Json` (BCL)               | Built in; for `launch.json` output    |
| Logging abstraction        | `Microsoft.Extensions.Logging.Abstractions` | Carries `ILogger<T>`, `NullLogger<T>`, and the `[LoggerMessage]` source generator that ADR 0001 §4 mandates for hot paths |
| Logging implementation     | `Microsoft.Extensions.Logging`, `Microsoft.Extensions.Logging.Console` | CLI host wires the factory and console formatter; subsystems still see only `ILogger<T>` |
| Metrics (deferred)         | `System.Diagnostics.DiagnosticSource` (BCL) | Carries `Meter` / `Counter<T>` / `Histogram<T>`. **Deferred for v0** (ADR 0001 §6 — a one-shot CLI has no collector); listed for the future, not a v0 dependency |

Every line in that table is a pure-managed NuGet package. No P/Invoke,
no native binary dependencies, no platform-specific runtime assets.
The package runs identically on every platform the .NET runtime
supports, and nothing in the install flow cares what OS you're on.

**Notable absence: `FastRsync`.** The v0 sync strategy uses SFTP
metadata diffing (size + mtime comparison, whole-file transfer of
changed files) over the SSH.NET connection. This is sufficient for
the expected file counts in a `dotnet publish` output and avoids the
complexity of a remote-side helper. FastRsync is a v1 candidate for
byte-level delta sync if metadata diffing proves too slow — see
[`transport.md`](transport.md) for the full rationale.

**What's explicitly *not* in the package:**

- **`vsdbg`** — Microsoft's closed-source debugger. License-restricted,
  not redistributable, and never touched by `roam` at runtime. See
  [`debugger.md`](debugger.md) for the full rationale. Bundling it is
  illegal; driving it is restricted to Microsoft's own tooling.
- **`netcoredbg`** — Samsung's MIT-licensed open-source debugger.
  *Could* be bundled, but shouldn't be. See "Why netcoredbg is
  fetched, not bundled" below.
- **Native `rsync` / Cygwin binaries** — SFTP metadata diffing via
  SSH.NET replaces this entirely; we don't need a native rsync.
  See [`transport.md`](transport.md) for why rsync's Windows
  incompatibility (Cygwin/MSYS2 dependency, SSH stack mismatch)
  makes it unsuitable as a default.
- **Helper binaries on per-RID NuGet subfolders** — see the
  "Remote-side helpers" section; v0 doesn't need them, and v1's
  answer is TBD.

The result is a small package (expected <10 MB), portable to any
.NET runtime, installable in a single command on any developer
machine.

## Why the debugger constraints don't affect packaging

This is the key observation, because it's where the concern comes
up: "we can't ship `vsdbg`, does that mean we can't be a dotnet
tool?" The answer is no, because `roam` doesn't ship *any*
debugger — it only emits config files that tell existing editors
how to reach a debugger that already exists (or will be bootstrapped
by the editor's own mechanism).

Concretely:

- In the **`vsdbg` path**, the Microsoft C# extension bootstraps
  `vsdbg` onto the target host via its own CDN-hosted script. `roam`
  writes a `launch.json` entry with `pipeTransport` and steps out
  of the way. The entire transaction is (1) `roam` emitting JSON
  and (2) an MS-published script running on the target. Neither
  step involves `roam`'s NuGet package touching a debugger binary.
- In the **`netcoredbg` path**, `roam` fetches the upstream
  Samsung/GitHub release for the target's RID *at first attach*,
  transferring it to the target via SSH.NET SFTP over the
  connection `roam` already holds. The NuGet package carries no
  binaries. This is the same pattern Microsoft uses for `vsdbg`,
  but against an open-source upstream where `roam` is allowed to
  be the party fetching it.

In neither path does the NuGet package carry a debugger, and in
neither path does `roam`'s process drive a debugger directly.
Packaging is pure C# assemblies all the way down.

## Why `netcoredbg` is fetched, not bundled

`netcoredbg` *could* legally be bundled (MIT license), and there's
an argument for doing so — it eliminates the first-attach network
dependency. But the cost is real:

- `netcoredbg` binaries are per-RID. At minimum we'd need
  `linux-x64`, `linux-arm64`, `linux-arm`, `osx-x64`, `osx-arm64`,
  `win-x64`. Each is ~10–20 MB, so bundling all of them adds
  ~80–120 MB to `roam`'s NuGet package — an order of magnitude
  larger than the tool itself.
- Users who never leave the `vsdbg` path would pay that cost for
  nothing.
- Users who only target one RID would pay for all the others.
- Upgrading `netcoredbg` would require a `roam` release.

The fetched-at-first-attach pattern is strictly better in almost
every dimension:

- Package stays small (<10 MB).
- Only the right binary is ever downloaded, to the host that needs
  it.
- Upgrades decouple: `roam install-debugger --update kiosk-01`
  pulls a newer `netcoredbg` without a `roam` release.
- Airgapped users can pre-stage the binary on target and skip the
  fetch entirely, because `roam` will use an existing install if
  present.

The one case where bundling would be worth it is a deeply
airgapped default where no host has internet — and for that, a
separate `roam-debuggers` NuGet side-package or a manual install
step is a better answer than bloating the core tool.

## Remote-side helpers

There's one open question that interacts with packaging: **does
`roam` need a small helper process running on the remote side for
delta sync?**

The v0 answer is **no**. The sync runs entirely in-process on
source using SSH.NET + SFTP to walk the remote tree, stat each
file, compare size and mtime against the local tree, and transfer
only the files that changed. This is the "manual diffing based on
file metadata" fast path — it's what Mutagen does before engaging
its delta algorithm, and it's what every file-sync tool does as
its cheap first pass. No helper, no bundled binaries, no remote
bootstrap.

SFTP's `ReadDir` returns full file metadata (size, mtime) for an
entire directory in one call, so the diffing phase is a handful of
round trips for the whole tree — not one stat per file. This keeps
the approach viable even over higher-latency links.

The cost is that the *first* deploy transfers whole files (not
blocks), so a 100 MB publish output is 100 MB on the wire once.
Subsequent deploys only transfer files that actually changed,
which for a normal `dotnet publish` iteration means 1–10 files
(your app DLLs and PDBs) out of a few hundred. That's already
fast enough that going further — to byte-level delta within
files — is an optimization worth deferring.

**If** the v1 or v2 implementation wants byte-level delta sync
(via FastRsync or similar), the options, in order of packaging
impact:

1. **Ship a tiny .NET helper as an embedded resource.** The main
   assembly carries a self-contained single-file .NET binary per
   RID, extracted and pushed to `/tmp/roam-helper` on first
   contact with a new host via the existing SSH.NET connection.
   Package bloats to ~50–80 MB because the helpers are per-RID,
   but the install UX is unchanged.
2. **Require .NET SDK on build and target.** Ship the helper as
   IL, run it as `dotnet roam-helper.dll` on the remote. Zero
   package bloat; requires SDK on every host `roam` touches.
   Reasonable for `build` hosts (they're already running
   `dotnet publish`); unreasonable for minimal targets like
   kiosks.
3. **Publish a sibling `roam-helper` `dotnet tool` for remote
   hosts.** Users install both on the source host and on each
   build host as part of setup. Clean, but two installs.
4. **Keep all logic on source.** SFTP-read remote file chunks,
   compute delta locally, SFTP-write patches back. No helper at
   all; performance depends on file sizes and link latency.

Option 1 is the most likely v1 answer. Option 4 is the cleanest
if performance allows. Either way, v0 ships with neither — the
SFTP metadata-diff fast path is enough to prove the shape works.

## Versioning and upgrade story

`roam` follows SemVer for package versions and derives those versions from git tags with MinVer. Release tags use the `v` prefix (`v0.1.0`, `v0.2.0`, etc.). Untagged builds use a `dev` prerelease with commit height, starting from a minimum `0.1` line before the first release tag.

Examples:

```bash
# untagged internal/dev build
dotnet pack src/Roam/Roam.csproj -c Release -o artifacts/packages
# => Roam.Cli.0.1.0-dev.<height>.nupkg

# tagged release build
git tag v0.1.0
dotnet pack src/Roam/Roam.csproj -c Release -o artifacts/packages
# => Roam.Cli.0.1.0.nupkg
```

For emergency/internal override builds, prefer MinVer's explicit override rather than reintroducing a hardcoded `<Version>` property:

```bash
dotnet pack src/Roam/Roam.csproj -c Release -o artifacts/packages /p:MinVerVersionOverride=0.1.0-internal.1
```

The schema version of `roamfile.yaml` is tracked separately so config can evolve without breaking old installations:

```yaml
# roamfile.yaml
version: 1
project: KioskUi
...
```

When `roam` encounters a config written for a newer schema than it
understands, it refuses to run and prints a helpful error pointing
at `dotnet tool update -g Roam.Cli`. When it encounters a config
written for an older schema, it reads it anyway and prints a
one-line deprecation note. Schema upgrades are rare and always
come with a `roam migrate` command that rewrites the file in
place.

Emitted artifacts (like `.vscode/launch.json` entries) carry a
comment tagging the `roam` version that generated them, so
`roam attach --regenerate` can rewrite stale entries without
disturbing hand-authored config.

## Open questions

1. **Multi-target framework.** Should `roam` target `net8.0`
   only, or `net8.0;net10.0`? Multi-targeting keeps older SDKs
   viable but doubles package size and CI time. Leaning
   single-target on the current LTS until someone asks for
   multi-target.
2. **Self-contained single-file escape hatch.** Some users (CI
   runners, minimal boxes) may not want to install the .NET SDK
   just to run `roam`. A published single-file self-contained
   build per RID, available alongside the NuGet package, is a
   reasonable future option. Not v0.
3. **Source-link + symbol server for the tool itself.** Worth
   enabling so users who hit a bug can step into `roam`'s own
   source from their IDE. Cheap to set up.
4. **Local-tool mode.** `dotnet tool install Roam.Cli` (without
   `-g`) drops it into `.config/dotnet-tools.json`, which is
   sometimes what teams want for reproducibility. `roam` should
   Just Work in both modes; no extra work required from us as
   long as we don't assume a global install path.
5. **Signing.** The NuGet package should be signed with a real
   certificate once the project is ready to share. `SignClient`
   and NuGet's own signing flow are the usual paths. Non-blocking
   for v0 but a todo for first public release.

## Summary

- `roam` ships as a normal framework-dependent `dotnet tool`.
- The NuGet package contains only managed assemblies and managed
  NuGet dependencies. No native binaries, no per-RID folders in v0.
- Debugger constraints do not affect packaging, because `roam`
  never bundles or drives a debugger — it emits config files and
  lets editors and the debuggers' own bootstrap mechanisms do the
  actual work.
- `netcoredbg` is fetched at first use, not bundled, for package
  size and upgrade-cadence reasons.
- Remote-side helpers are a v1 optimization; v0 uses in-process
  `SSH.NET` + SFTP with size/mtime diffing and is small enough
  that the question doesn't arise.
- One install command, one upgrade command, one uninstall command,
  same on every platform.
