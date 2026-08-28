# Debugger strategy

**Status:** load-bearing constraint. This document captures a legal
and practical reality that shapes `roam`'s design: the default
Microsoft .NET debugger is closed-source and license-restricted, and
pretending otherwise would set the project up for a painful unwind
later.

## The constraint

The debugger binary that powers Visual Studio, the VSCode C#
extension, VSCode C# Dev Kit, and (historically) Visual Studio for
Mac is **`vsdbg`** — a Microsoft-owned, closed-source component
distributed under a license that permits its use *only* from
Microsoft-authorized products and services. Specifically:

- Visual Studio (all editions)
- Visual Studio Code **with the official Microsoft C# extension or
  C# Dev Kit**
- Visual Studio for Mac
- Azure services that embed it

Everything outside that list is disallowed by the license. In
concrete terms, `roam` **cannot**:

- Ship or redistribute `vsdbg` as part of its install, NuGet package,
  or any bundle.
- Drive `vsdbg` from a custom CLI or TUI frontend.
- Reuse `vsdbg` from JetBrains Rider, Neovim DAP clients, Emacs
  dap-mode, Helix, or any other third-party editor.
- Copy `vsdbg` from one machine to another outside of the Microsoft
  extension's own bootstrap flow.

JetBrains Rider has its own proprietary debugger, and it is similarly
**not extractable** — it only runs inside Rider. So "run any .NET
debugger from any frontend" is genuinely blocked by the two dominant
vendors.

## What this means in practice

`roam` has to get its remote-debug story from somewhere other than
"ship a debugger." There are two legitimate paths, and `roam` should
support both.

### Path 1 — Lean on Microsoft's blessed remote-attach flow

There is one legitimate way for `roam` to end up with `vsdbg`
debugging a process on a remote host without touching `vsdbg`
ourselves. The flow looks like this:

1. The developer has VSCode on their workstation or laptop, with
   the **official Microsoft C# extension** installed. That's an
   authorized `vsdbg` consumer.
2. `roam attach <profile>` emits a `.vscode/launch.json` stanza of
   type `coreclr`, request `attach`, with a `pipeTransport` block
   pointing at the profile's target host over SSH.
3. When the developer hits F5, the Microsoft C# extension opens the
   SSH pipe, and on first attach runs an **MS-hosted bootstrap
   script** (`GetVsDbg.sh`) from Microsoft's CDN over that pipe.
   The script downloads `vsdbg` onto the target host into
   `~/vsdbg/` (or wherever the launch.json says).
4. The extension then drives that remote `vsdbg` over the SSH pipe
   to attach to the running process.

From Microsoft's license perspective, every step of this is an
authorized use: the extension is an MS product, the bootstrap is an
MS-hosted artifact, and `roam` never touches the debugger binary.
`roam`'s only role is emitting the `launch.json` entry with the
correct host, SSH user, and process name.

**Requirements on the target host** for this path to work:

- Network access to Microsoft's CDN (`vsdbgshim.azureedge.net` and
  related hosts) — at least for the first attach, since the
  bootstrap caches the binary locally.
- `bash`, `curl`, `unzip`, a writable home directory, and enough
  disk for the debugger (~50 MB).
- An architecture Microsoft publishes `vsdbg` for: `linux-x64`,
  `linux-arm`, `linux-arm64`, `osx-x64`, `osx-arm64`,
  `win-x64`. Alpine/musl is not a first-class target.
- SSH reachability from the developer host, which Tailscale makes
  trivial.

**Where this path breaks down:**

- **Airgapped targets.** Industrial PCs, kiosks without internet,
  lab machines on restricted VLANs. The bootstrap can't run.
- **Non-standard architectures.** If you ever want to debug on
  something Microsoft doesn't ship `vsdbg` for, you're stuck.
- **Non-VSCode editors.** The entire flow hinges on the Microsoft
  C# extension being the thing driving the debugger. Rider,
  Neovim, CLI-only users get nothing.
- **Audit-sensitive environments.** Some organizations restrict
  downloading binaries from Microsoft CDNs at runtime. This is a
  build-vs-buy question that `roam` can't solve.

For the **motivating use cases** — an Avalonia app built on a
workstation and debugged on a laptop, or on a kiosk with Tailscale
internet — this path works cleanly. It should be `roam`'s default.

### Path 2 — Samsung's `netcoredbg`

**`netcoredbg`** is an MIT-licensed, open-source .NET debugger
developed by Samsung, originally for Tizen. It is:

- The debugger JetBrains Rider used *before* JetBrains wrote their
  own. Battle-tested on real .NET workloads for years.
- What Unity's VSCode integration uses.
- A DAP-speaking (Debug Adapter Protocol) debugger, which means
  VSCode-family editors can drive it through a compatible
  extension.
- Available as prebuilt binaries for Linux (x64, arm, arm64),
  macOS (x64, arm64), and Windows (x64). Easy to compile for
  other targets.
- Freely redistributable. `roam` can ship it, install it, drive it,
  without any licensing concerns.

The catch — and it is a real catch — is that **`netcoredbg` is not
wire-compatible with the Microsoft C# extension's `coreclr` debug
type.** The MS extension hardcodes `vsdbg` as its backend and does
not expose a configuration switch to point at a different debugger.
To use `netcoredbg` from VSCode, you install a **separate**
community/Samsung VSCode extension that registers a different debug
type (typically still called `coreclr` in its launch.json, but
provided by a different extension ID).

Implications:

- A `roam` profile using `netcoredbg` requires the developer to
  have the community extension installed in addition to, or
  instead of, the Microsoft C# extension.
- `roam attach --debugger netcoredbg` emits a launch.json
  configured for the community extension, not the MS one.
- `roam` can install `netcoredbg` on the target during the deploy
  stage — it's one statically-linked binary plus a handful of
  shared-object files. This is legal (MIT), and it works
  airgapped because `roam` provides the binary.
- `netcoredbg` works with JetBrains Rider historically, though
  Rider now uses its own proprietary debugger and does not expose
  a config path to swap it out. So "netcoredbg in Rider" is not a
  current path.

**Where this path wins:**

- Airgapped and bandwidth-constrained targets.
- Exotic architectures where Microsoft doesn't publish `vsdbg`.
- Audit-sensitive environments that ban downloading MS binaries at
  runtime.
- Open-source-purist teams who don't want closed-source debuggers
  anywhere in their pipeline.
- Future-proofing against Microsoft license changes.

**Where this path loses:**

- It requires a VSCode extension swap. Friction on first setup.
  Some developers will have the MS C# extension installed for
  other reasons and won't want to uninstall it.
- The community extensions are less maintained than the MS one.
  Expect occasional rough edges around newer .NET features
  (hot-reload, function breakpoints with exotic conditions,
  async-context inspection).

## The tradeoff, as a table

| Dimension                          | `vsdbg` via MS bootstrap | `netcoredbg`                      |
|------------------------------------|--------------------------|-----------------------------------|
| Licensing safe for `roam` itself   | Yes (we never touch it)  | Yes (MIT, redistributable)        |
| Works airgapped                    | No (needs MS CDN once)   | Yes                               |
| Works on exotic architectures      | No (MS-published RIDs)   | Yes (compile it)                  |
| Works with MS C# extension         | Yes                      | No — needs community extension    |
| Works with Rider                   | No (Rider uses its own)  | No (same)                         |
| Works with CLI / Neovim / Emacs    | No                       | Yes                               |
| Drop-in on a fresh target          | Yes (auto-bootstrap)     | Requires install (roam can do it) |
| Hot-reload / edit-and-continue     | Yes                      | Partial / varies by version       |
| Maturity                           | Very high                | High, Samsung-maintained          |
| Risk of license changes            | Non-zero                 | Zero                              |

## `roam`'s stance

Based on the above, `roam`'s position is:

1. **Never bundle, redistribute, or drive `vsdbg` directly.** Not
   in NuGet, not in the `dotnet tool` package, not copied between
   hosts by `roam`'s sync steps. The only entity that ever installs
   `vsdbg` on any machine is the official Microsoft extension, via
   its own bootstrap. This is a hard line.

2. **Default to the MS-bootstrap path.** `roam attach <profile>`
   without a `--debugger` flag emits a `launch.json` entry that
   uses `coreclr` with `pipeTransport` and assumes the Microsoft
   C# extension is the consumer. This is the path of least
   friction for the overwhelming majority of users, and it is
   legal because `roam` is only generating a config file.

3. **Support `netcoredbg` as a first-class alternative.**
   `roam attach --debugger netcoredbg` emits launch.json entries
   for the community extension. `roam` optionally installs
   `netcoredbg` on the target as part of the deploy stage (opt-in
   via a flag in the profile, since not every target wants an
   extra binary sitting in `~/bin`). `netcoredbg` is the
   recommended choice for airgapped, exotic-arch, or
   non-VSCode setups.

4. **Document the choice loudly.** The first time a user runs
   `roam attach`, print a one-line notice about which debugger
   path is being used and link to this document. The first time
   a bootstrap fails (no internet, unsupported arch), the error
   should recommend `--debugger netcoredbg` by name.

5. **Never write a custom debugger frontend.** `roam`'s job ends
   at emitting the right config file for an existing editor. The
   debugger UI is the editor's problem. This keeps `roam` on the
   right side of both debuggers' realities and also keeps the
   tool small.

6. **Treat Rider as a separate, later emitter.** Rider uses its
   own proprietary debugger that `roam` can neither ship nor
   drive. For Rider users, `roam` will eventually emit a **Rider
   "Remote Process" run-configuration XML** that points Rider's
   own debugger at the target host over SSH. That's a future
   feature; v0 ships VSCode only. Rider's debugger and the "Rider
   remote" flow have their own licensing terms, but since `roam`
   is emitting config and Rider is doing the debugging, the same
   "config-only" stance keeps `roam` clean.

## What a profile's debugger choice looks like in `roamfile.yaml`

The `debug` block is the single source of truth for a profile's
debugger choice. Its shape is pinned in
[`configuration.md`](configuration.md) and enforced by
[`roamfile.schema.json`](roamfile.schema.json):

```yaml
profiles:
  kiosk:
    source: laptop
    build:  workstation
    target: kiosk-01
    publish-profile: ReleaseKioskArm64
    launch-profile:  Production
    deploy:
      path: /opt/kiosk-ui
      stop:  systemctl --user stop kiosk-ui
      start: systemctl --user start kiosk-ui
    debug:
      enabled: true
      debugger: vsdbg               # v0 accepts only vsdbg
      editor: vscode                # v0 accepts only vscode
      process-name: KioskUi         # used for attach + readiness
      install-on-target: false      # v0 accepts only false (vsdbg self-bootstraps)
```

v0 locks this block to the MS-bootstrap path described above:

- `debugger: vsdbg` is the only accepted value; `netcoredbg` is a
  v2 feature (see [`implementation-contract.md`](implementation-contract.md)).
- `editor: vscode` is the only accepted value; `rider` is a v2
  feature.
- `install-on-target: true` is rejected because v0 never pushes a
  debugger binary — the MS extension's own bootstrap handles it.

`process-name` is shared with [`readiness.md`](readiness.md): the
default `pgrep` probe uses the same name the emitted `launch.json`
writes into `processName`. Keeping them in one field prevents drift
between "what we're attaching to" and "what we're polling for."

Per-profile granularity remains the intended shape — a `dev-local`
profile on a laptop and a `kiosk` profile on an airgapped industrial
PC can eventually pick different debuggers — but v0 defers the
choice to the MS-bootstrap path exclusively. `netcoredbg` reappears
in v2.

## Open questions

1. **Auto-detection.** Should `roam` try to auto-pick the debugger
   based on target properties (airgap hint? non-x64 arch?) or
   leave the choice entirely explicit? Leaning explicit — magic
   here will surprise users.
2. **`netcoredbg` version pinning.** Do we pin a specific
   `netcoredbg` release and test against it, or follow upstream?
   Pin for v0; revisit when upstream cadence becomes a problem.
3. **What to do when the MS extension updates and breaks
   `pipeTransport`.** Rare but has happened historically.
   Mitigation: the emitted launch.json carries a comment with the
   `roam` version that generated it, so `roam attach --fix` can
   regenerate against a newer schema.
4. **Fallback to `dotnet-dump` + `dotnet-trace` for post-mortem
   analysis.** Not a live debugger, but worth documenting as the
   escape hatch when even `netcoredbg` isn't an option (e.g.
   airgapped + Windows + musl). These tools are open-source,
   redistributable, and often sufficient to diagnose crashes
   without a live debugger session.
   **Decided (2026-06): this is now more than a fallback** — it is
   the **agent-facing** diagnostic path. An agent driving roam has no
   DAP client and cannot use the attach flow above; its "debugger" is
   a fetchable bundle of logs, crash dumps (runtime-native
   `createdump`, already in every self-contained publish), and traces,
   indexed for machine consumption. See
   [ADR-0002 (agent-first usability)](adr/0002-agent-first-usability.md)
   for the `roam diag` design.
5. **Hot reload across `roam watch`.** Orthogonal to the debugger
   choice but related: `dotnet watch` has hot-reload built in, and
   it's unclear how cleanly it survives being piped through a
   three-host `roam` pipeline. Expect this to be a feature users
   ask for; plan for the design but don't over-spec it until
   there's a real request.

## Summary

`roam` is constrained by the reality that .NET's two production
debuggers (`vsdbg` and Rider's) are closed, locked to their vendor's
tools, and not available for third-party orchestration. The tool
works around this by **never touching a debugger directly** and
instead **emitting config files** that let authorized editor
extensions do the driving. The default path uses Microsoft's own
remote-attach flow (legal, friction-free, but connected and
x64/arm-only); the escape hatch uses Samsung's MIT-licensed
`netcoredbg` (airgap-friendly, redistributable, but requires a
different editor extension). Per-profile configuration picks which
path each dev scenario uses.

This is a constraint, not a problem. Keeping `roam` out of the
debugger-binary business is also what keeps it *small*: the tool
can focus entirely on orchestration and leave the rendering of
call stacks and variable panes to editors that already do it well.
