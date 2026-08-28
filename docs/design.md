# roam — design sketch

**Status:** exploratory, with the currently approved implementation
slice frozen in [`implementation-contract.md`](implementation-contract.md).
The goal here is still to capture the shape of the problem clearly
enough that the implementation can falsify the ideas by contact with
reality, but v0 scope should no longer drift casually.

## 1. The three roles

Every `roam` pipeline has three roles. Any of them can coincide on the
same host; all of them can differ.

- **source** — where the canonical copy of the code lives, and where
  the developer edits it. In practice this is usually either (a) a
  laptop with the editor open or (b) a remote workstation accessed
  via VSCode Remote-SSH / JetBrains Gateway.
- **build** — where the compile, publish, or bundle step runs. Chosen
  for CPU, RAM, toolchain availability, or cross-compile target. May
  or may not be the same machine as source.
- **target** — where the built binary actually runs. Chosen because
  of hardware (GPU, display, sensors), location (on the user's desk,
  at a kiosk, at a field site), or environment (specific OS version,
  specific kernel). The debugger attaches *here*.

The mapping from roles to hosts is per-project and per-invocation.
Two canonical examples:

```
# Avalonia GUI dev from a coffee shop
source = laptop
build  = workstation   (fast CPU, plugged in, on Tailscale)
target = laptop        (real display, real GPU, right next to you)
```

```
# Edge device iteration
source = laptop
build  = workstation   (has the ARM64 cross-toolchain)
target = kiosk-01      (the actual hardware the code has to run on)
```

The point of naming the roles is that once they're explicit, you can
swap any one of them without touching the others. "Build on my laptop
today because the workstation is rebooting" is a one-line override.

## 2. The execution model: fixed pipeline, not arbitrary stages

A `roam` invocation is a **fixed pipeline** with a known shape. Every
profile runs the same sequence of steps; what changes between
profiles is which hosts play which roles.

```
sync-source → publish → stop → sync-artifacts → start → attach-debugger
     ↑            ↑        ↑         ↑              ↑          ↑
 source→build   build    target  build→target     target   editor→target
```

The pipeline is always this shape. There is no user-visible "stages"
abstraction, no arbitrary stage graph, no DAG. Each step is either a
file sync or a remote command, and `roam` knows which host to run it
on because the profile assigns the three roles.

**Why a fixed pipeline instead of arbitrary stages:**

- Every example in the docs fits the same shape. No one has produced
  a counterexample where custom stage ordering matters.
- A fixed pipeline eliminates a category of design questions (stage
  ordering, DAG scheduling, dependency tracking) that add complexity
  without serving any known use case.
- A profile is "I want to do this scenario." A stage list is "here's
  how to do it step by step." The profile is the right abstraction
  level for a dev-loop tool where the developer wants to think about
  *what* they're building, not *how* the build is orchestrated.
- If a real use case for custom stages ever appears, the fixed
  pipeline can be loosened. Going the other direction — from flexible
  to fixed — is much harder.

**Why stop runs before sync-artifacts:**

The process on the target must be stopped *before* artifacts are
synced, not after. This is load-bearing:

- **Windows file locking.** You cannot overwrite a running `.exe` on
  Windows. The process must be stopped before the sync writes new
  files.
- **Shared-library races on Unix.** Overwriting a running binary on
  Linux technically works (the kernel keeps the old inode open), but
  replacing shared libraries under a running process can cause
  segfaults if the dynamic linker re-reads them.
- **Atomic deploy compatibility.** If using stage-and-swap deployment
  (see [`paths.md`](paths.md)), the stop must happen before the
  symlink swap.

The correct order is always: stop → sync → start.

## 3. Profiles, not stages

A **profile** is a complete dev scenario — one noun for "what the
developer wants to do right now." The config file is organized
around profiles, not around pipeline steps.

```yaml
# roamfile.yaml
project: kiosk-ui

hosts:
  laptop:      { ssh: laptop.tailnet.ts.net,      user: dev }
  workstation: { ssh: workstation.tailnet.ts.net, user: dev }
  kiosk-01:    { ssh: kiosk-01.tailnet.ts.net,    user: kiosk }

profiles:
  workstation-to-laptop:
    description: Heavy publish on workstation, run on laptop.
    source: laptop
    build:  workstation
    target: laptop
    publish-profile: ReleaseLaptop
    launch-profile:  Development
    deploy:
      path: ~/apps/kiosk-ui
    debug: true

  kiosk:
    description: Push to the real kiosk hardware.
    source: laptop
    build:  workstation
    target: kiosk-01
    publish-profile: ReleaseKioskArm64
    launch-profile:  Production
    deploy:
      path: /opt/kiosk-ui
      stop:  systemctl --user stop kiosk-ui
      start: systemctl --user start kiosk-ui
    debug: true
```

Each profile names:

- The three hosts (source / build / target).
- The publish profile to pass to `dotnet publish`.
- The launchSettings profile to pass to the running binary.
- The deploy path on the target and stop/start commands.
- Whether to emit a debugger-attach config for this profile.

Everything else (RID, Configuration, SelfContained, env vars,
command-line args) lives in the .NET file that already owns it.
See [`configuration.md`](configuration.md) for the full rationale.

`roam run kiosk` runs the fixed pipeline for the `kiosk` profile.
`roam watch kiosk` is the natural follow-on for repeated source-file
changes, but it is intentionally out of v0.
`roam attach kiosk` emits the `launch.json` snippet so that F5 in
VSCode talks to the right host via SSH `pipeTransport`.

`roam run kiosk --build=laptop` overrides the build host for one
invocation without touching the file — this is the "my workstation
is rebooting, build locally today" case.

## 4. What `roam` owns vs. what it delegates

`roam` is a thin orchestrator. It deliberately doesn't reimplement
anything that exists:

- **SSH transport** → SSH.NET (pure managed C#) for all remote
  operations: command execution, SFTP file transfer, and port
  forwarding for ProxyJump chains. `~/.ssh/config` compatibility
  is achieved via `ssh -G <host>` — a one-shot config-resolution
  call at startup that feeds resolved connection parameters into
  SSH.NET. The system `ssh` binary is a config oracle, not a
  runtime transport dependency. See
  [`transport.md`](transport.md) for the full rationale.
- **File sync (one-shot)** → SFTP metadata diffing over the SSH.NET
  connection. Compare size/mtime via `ReadDir`, transfer only
  changed files, delete stale files. No external sync tool
  required. Cross-platform, no Cygwin.
- **File sync (watch mode)** → delegate to Mutagen if installed
  (FS event watching, reconnect handling, native Windows support),
  fall back to periodic SFTP polling if not.
- **Remote command execution** → SSH.NET `RunCommand`. No agent on
  the remote host. Requires SSH reachability (Tailscale makes this
  trivial) and the right toolchain already present on each host.
  `roam` does not install toolchains; that's the operator's job or
  a separate tool's (Packer, Ansible, cloud-init) job.
- **Debugger attach** → emits config for whichever editor you use,
  and for .NET specifically emits a `coreclr` launch block with SSH
  `pipeTransport`. Doesn't run the debugger; VSCode/Rider does.
- **Build caching** → whatever `dotnet` already does (incremental
  compilation, NuGet cache, `obj/` reuse). `roam` does not maintain
  its own cache.
- **Secrets** → not in scope. Use SSH keys, `sops`, or whatever you
  already use.

What `roam` *does* own:

- The YAML schema and config loading.
- The role → host resolution, including per-invocation overrides.
- The fixed pipeline planner/executor (sync source → publish → stop →
  sync artifacts → start → attach).
- SSH connection lifecycle (pooling across pipeline steps, `ssh -G`
  resolution, ProxyJump chain setup via SSH.NET port forwarding).
- The deployment policy for each profile (preserve-layout vs.
  flatten-publish, in-place sync, readiness behavior).
- Preflight validation before any destructive or expensive work starts
  (see [`preflight.md`](preflight.md) for the itemized v0 contract).
- Process readiness verification after start (poll for process,
  surface stderr on failure). See
  [`readiness.md`](readiness.md).
- A per-workspace state directory (`.roam/`) whose layout is frozen in
  [`state.md`](state.md).
- Consistent, readable output that matches the format pinned in
  [`logging.md`](logging.md), and the exit-code taxonomy in
  [`exit-codes.md`](exit-codes.md).
- The downstream editor emitters (`launch.json` in v0; more later).

The implementation should keep those concerns in explicit subsystems
rather than one monolithic orchestrator class. The intended v0 split is:

1. config loading and validation (schema in
   [`roamfile.schema.json`](roamfile.schema.json)),
2. host resolution and preflight (see [`preflight.md`](preflight.md)),
3. SSH transport and command execution (see
   [`transport.md`](transport.md)),
4. source/artifact sync,
5. deployment and readiness (see [`readiness.md`](readiness.md)),
6. state storage under `.roam/` (see [`state.md`](state.md)),
7. editor/debugger emission (see [`debugger.md`](debugger.md)),
8. CLI composition (see [`cli.md`](cli.md)).

## 5. Settled design decisions

1. **Fixed pipeline, not arbitrary stages.** The pipeline shape is
   fixed: sync source → publish → stop → sync artifacts → start →
   attach. No user-visible stage abstraction, no DAG. Every known
   use case fits this shape. If a counterexample appears, the
   pipeline can be loosened; going the other direction is harder.
   See section 2 above.
2. **Implementation language.** `roam` is a .NET console application,
   shipped as a `dotnet tool`. The audience already has the SDK, and
   writing the tool in the same stack it targets means the team
   dogfoods its own publish/deploy loop from day one. SSH.NET and
   SFTP handle all remote operations in-process; no shell wrappers
   or external binaries required. See
   [`packaging.md`](packaging.md) for the distribution story.
3. **Pipeline ordering: stop before sync.** The target process is
   stopped *before* artifacts are synced, not after. Required for
   Windows file-locking, safer on Unix, compatible with atomic
   deploy. See section 2 for the rationale.
4. **Source sync uses git-tracked files.** The source sync transfers
   exactly the files git tracks (`git ls-files`), not "everything
   minus an exclusion list." This is correct by construction:
   `bin/`, `obj/`, `publish/`, `.vs/`, and every other generated
   artifact is untouched on the build host because it was never in
   scope. See [`paths.md`](paths.md) for details.
5. **V0 is intentionally smaller than the full design space.** The
   first implementation slice is limited to a thin but complete path:
   `init`, `run`, and `attach`; one-shot SFTP sync; VSCode config
   emission; readiness checks; and a small state store. `watch`,
   Mutagen, alternate debugger backends, and richer transport modes
   are deferred until the thin slice proves itself. See
   [`implementation-contract.md`](implementation-contract.md).

## 6. Open design questions

1. **Config format.** YAML is the obvious default. TOML is nicer for
   humans but less flexible for nested structure. HCL is tempting
   (familiar from Terraform) but adds a parser dependency. Start
   YAML, reconsider if it gets ugly.
2. **How to handle the "source and build are the same host" case.**
   The sync-source step becomes a no-op. The config shouldn't have
   to special-case it; `roam` should detect role coincidence and
   skip identity syncs automatically.
3. **What to do when a sync target is offline.** For post-v0
   `roam watch`,
   should builds queue up and flush on reconnect, or fail loudly?
   Probably queue for sync steps, fail loudly for command steps —
   but this needs real-use feedback.
4. **.NET-only, forever.** `roam` is scoped to .NET — one build verb
   (`dotnet publish`), one debugger (`coreclr` / `vsdbg`), one
   distribution channel (`dotnet tool install -g Roam.Cli`). Polyglot
   support is explicitly out of scope; a sibling tool can handle
   other stacks if anyone wants one.
5. **Step-level retries and timeouts.** Almost certainly needed for
   flaky wireless networks. Not in the v0 schema yet; add when it
   first hurts.
6. **Who initiates `roam run` for `roam watch`?** If source and build
   are different hosts, does the watcher run on source (watching
   local files) or build (watching the synced copy)? Source is the
   cleaner answer — sync first, then trigger downstream — but this
   wants real testing.

## 7. Non-goals, restated

Because this document is the place design goals drift the fastest,
let's nail them down once more:

- `roam` is **not CI/CD**. It does not replace GitHub Actions,
  GitLab CI, Jenkins, or anything that runs on push-to-main.
- `roam` is **not a build system**. `dotnet publish` is called by
  `roam`, not replaced by it.
- `roam` is **not polyglot**. .NET only — one language, one
  debugger, one publish verb.
- `roam` is **not a package manager or installer**. Toolchains on
  each host are the operator's problem.
- `roam` is **not a container orchestrator**.
- `roam` is **not a secrets manager**.
- `roam` is **not a remote-execution build farm** like Bazel RBE.

It *is* a thin, declarative shim over "SSH + file sync + debugger
attach" that makes the three-host dev loop feel like one host.

## 8. First milestone

**Build the narrowest complete tool directly, against a real project.**

The v0 implementation is a .NET 10 console app (`src/Roam/`) that
ships as a `dotnet tool`. Development proceeds by building `roam`
itself and testing it against a real Avalonia project (published on
a build VM, deployed to a laptop).

The design docs above capture the architecture; the implementation
contract freezes which parts are actually allowed into v0. Anything
outside that slice should stay out until the thin path has been
validated against the motivating Avalonia workflow. Assumptions that
don't survive real use get revised in the docs and the code together,
but they should revise the contract deliberately rather than by drift.

### What to watch during early development

The following observations will most directly shape the tool. Pay
attention to these as the implementation takes shape:

- **Does `git ls-files` capture everything the build needs?** Are
  there untracked files that should sync? Files git tracks that
  shouldn't? Record every false negative and false positive.
- **First deploy vs. delta deploy cost.** How long does the initial
  ~100 MB transfer take on your actual network? How many files
  change after a one-line C# edit, and how long does the delta
  sync take? Record wall-clock times and network type (Tailscale,
  wifi, wired, hotspot).
- **Incremental build survival.** Does the build host's `obj/`
  directory survive the source sync and keep MSBuild fast, or does
  MSBuild rebuild from scratch every time? Record build times with
  warm caches vs. cold.
- **`pipeTransport` reliability.** Does the VSCode debugger attach
  work on first try through SSH? Does it break after laptop
  suspend/resume? Does it need retries? Record failure modes.
- **Stop-before-sync ordering.** Does stopping the process before
  syncing feel right in practice, or is there a case where you want
  to sync first? Record any counterexample.
- **Process startup time.** How long between `systemctl start` and
  the process being ready for debugger attach? How much does it
  vary? Record wall-clock times.
- **Target-unreachable behavior.** What happens when the kiosk is
  off or the network drops mid-pipeline? Is the error clear? Record
  error messages and recovery steps.
- **Watch-mode trigger frequency.** How often do you actually want
  to rebuild? Every save, or is manual `roam run` enough? Record
  whether `roam watch` earns promotion from v1 backlog into a later
  milestone.
- **Stale-file behavior.** After renaming a DLL or removing a
  dependency, does the old file on the target cause problems? Record
  every stale-file incident.
