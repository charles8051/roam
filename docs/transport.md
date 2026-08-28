# Transport and topology

**Status:** exploratory. This document captures how bytes move
between the three hosts of a `roam` pipeline, what SSH reachability
is required where, and which sync tool actually performs the
transfers. The recommendations here are strong defaults, not hard
commitments.

## SSH strategy: SSH.NET for everything, `ssh -G` for config

`roam` uses **SSH.NET** — a pure-managed C# SSH library — for all
remote operations: command execution, SFTP file transfer, and port
forwarding for ProxyJump chains. The system `ssh` binary is **not**
a runtime dependency. This gives `roam`:

- **One connection model.** A single SSH.NET connection to a host
  serves command execution, SFTP, and port forwarding. No juggling
  two SSH stacks or two auth contexts.
- **Identical behavior on Windows, macOS, and Linux.** No
  platform-specific SSH quirks (different default ciphers, different
  agent socket paths, different escape handling).
- **No Cygwin / MSYS2 dependency on Windows.** This is the
  load-bearing property. Windows OpenSSH works fine for basic
  commands, but it cannot interop with Cygwin-flavored tools like
  rsync. By using SSH.NET for file transfer (SFTP) instead of
  rsync-over-SSH, the entire Cygwin problem disappears.
- **Clean error model.** Connection failures, auth failures, and
  timeouts all surface through a single API. No parsing subprocess
  stderr. See "Error surfacing" below for how those exceptions are
  mapped to exit codes and log output.

### The `~/.ssh/config` gap and how `ssh -G` fills it

SSH.NET's one real weakness is that it does not parse
`~/.ssh/config`. It knows nothing about `Host` aliases,
`ProxyJump`, `Match` blocks, `IdentityFile` directives, or
`Include` files. Left alone, this would force `roam` to either
reimplement ssh_config parsing (a project in itself, always lagging
behind OpenSSH) or ignore config entirely (breaking every user who
relies on `Host` aliases or ProxyJump).

The solution is **`ssh -G <hostname>`** — a subcommand available on
all platforms (including Windows OpenSSH) that outputs the fully
resolved configuration for a host as flat key-value pairs:

```
$ ssh -G kiosk-01
user kiosk
hostname 10.100.0.42
port 22
identityfile ~/.ssh/id_ed25519
proxyjump workstation
```

Every directive is resolved: `Match` blocks evaluated, `ProxyJump`
chains expanded, `Include` files processed, `IdentityFile` paths
resolved. `roam` runs `ssh -G <host>` once per host at pipeline
startup, parses the flat output, and configures SSH.NET connections
with the resolved values.

This makes the system `ssh` binary a **config-resolution oracle** —
a one-shot subprocess call at startup, not an ongoing transport
dependency.

### Fallback when `ssh -G` is unavailable

If the `ssh` binary is not on `PATH` (unusual but possible on a
minimal Windows install or a containerized runner), `roam` falls
back to the explicit host fields in `roamfile.yaml`. The fallback
contract is:

- `ssh:` (hostname) is **required**. Preflight fails (exit `4`)
  if missing.
- `user:` is **required**. Preflight fails if missing.
- `port:` defaults to `22`.
- `identity-file:` defaults to the first of `~/.ssh/id_ed25519`,
  `~/.ssh/id_rsa` that exists.
- `ProxyJump` is **not available** in fallback mode. A host that
  requires `ProxyJump` must have `ssh -G` resolution available
  (i.e., OpenSSH installed on the source host). Preflight rejects
  a roamfile that names ProxyJump chains without `ssh` on `PATH`.

The fallback is intentionally minimal: it exists so `roam` does not
hard-fail on a Windows box without OpenSSH, not so users can bypass
`~/.ssh/config` for convenience. Teams with real SSH configuration
needs should install OpenSSH (it ships inbox on Windows 10 1809+ and
with every Unix distribution) rather than lean on the fallback.

### ProxyJump through SSH.NET

When `ssh -G` reports a `proxyjump` directive, `roam` implements
the chain programmatically:

1. SSH.NET connects to the jump host.
2. Opens a forwarded channel to the final target's host:port through
   that connection.
3. Establishes a second SSH session over the forwarded channel.

This is ~20 lines of plumbing using SSH.NET's port-forwarding API,
not a research problem — but it is unproven in the Compose lab as of
the v0 doc freeze. The implementation plan gates ProxyJump behind a
dedicated prototype spike; see
[`implementation-plan.md`](implementation-plan.md). For the common
case (Tailscale direct connectivity), there is no proxy and SSH.NET
connects directly.

### Error surfacing

SSH.NET's typed exceptions map to the exit-code taxonomy in
[`exit-codes.md`](exit-codes.md) as follows:

| SSH.NET exception                      | Exit code | Step                 |
|----------------------------------------|-----------|----------------------|
| `SshConnectionException` (during preflight)   | `4` (`preflight`) | `preflight` |
| `SshAuthenticationException` (during preflight) | `4` (`preflight`) | `preflight` |
| `SshConnectionException` (during publish)     | `5` (`publish`)   | `publish`   |
| `SshConnectionException` (during sync)        | `6` (`sync`)      | `sync-*`    |
| `SshOperationTimeoutException` (during sync)  | `6` (`sync`)      | `sync-*`    |
| `SshConnectionException` (during stop/start)  | `7` (`deploy`)    | `stop` or `start` |
| Non-zero remote exit (readiness probe)         | `8` (`ready`)     | `ready`     |

Every error line `roam` emits for an SSH auth failure includes the host
alias, resolved hostname, user, port, candidate identity-file paths,
and per-candidate status (`loadable`, `file not found`, encrypted or
unsupported, etc.). Private key **contents** and other secret material
are never included in error output. Key paths are intentionally shown
because they are the actionable part of SSH.NET/OpenSSH mismatch
diagnostics.

### Key and agent authentication

SSH.NET supports private-key-file authentication. `roam` feeds SSH.NET
all candidate `identityfile` entries resolved by `ssh -G`, preserving
an explicit `identity-file:` from `roamfile.yaml` first, then every
OpenSSH-resolved `IdentityFile`, then existing default keys under
`~/.ssh` as a fallback. Candidate keys are loaded non-interactively in
that deterministic order. v0 does not yet rely on ssh-agent/Pageant for
SFTP authentication; encrypted keys that require a prompt are reported
as non-loadable with corrective diagnostics.

### What the emitted `launch.json` uses

The one place system `ssh` is genuinely invoked at runtime is in
VSCode's `pipeTransport` — but that's **VSCode** shelling out to
`ssh`, not `roam`. `roam` only emits the JSON config and is out
of the picture by then. The developer's system `ssh` needs to reach
the target host, but `roam` itself doesn't touch it.

## The pipeline, step by step

Every `roam` invocation walks a fixed pipeline of six steps, with
some collapsing into no-ops when roles coincide:

```
[1] source  →  build   : sync source code (git-tracked files via SSH.NET SFTP)
[2]            build   : SSH.NET — dotnet publish -r <rid> --self-contained
[3]            target  : SSH.NET — stop process (profile's stop command)
[4] build   →  target  : sync published artifacts (relay through source by default)
[5]            target  : SSH.NET — start process (profile's start command)
[6] source  →  target  : emitted launch.json pipeTransport — debugger attach (coreclr)
```

The ordering is deliberate: **stop [3] runs before sync [4]**. The
process on the target must be stopped before artifacts are replaced,
because Windows file locking prevents overwriting a running binary
and Unix shared-library replacement under a running process is racy.
See [`design.md`](design.md) section 2 for the full rationale.

Steps [1] and [6] are unambiguously source-originated. Step [4] is
the interesting one, and the rest of this document is mostly about
how to do it without requiring build and target to know about each
other.

## Step [4]: source-as-relay is the default

The default mode for artifact transfer is **the source host acts as
a relay**: the laptop (or whatever is playing the source role)
pulls bytes from build and streams them onward to target, in a
single operation, without persisting a copy. Concretely, `roam`
downloads via SFTP from build and uploads via SFTP to target, using
its SSH.NET connections to both hosts.

### Why this is the default

Three reasons, in order of weight:

1. **Trust topology.** The source host is the only machine a human
   is actually touching — it's the dev's laptop sitting on a
   Tailscale network. It is legitimately the control plane. Making
   it the relay means:
   - Only `source` needs outbound SSH reachability to both `build`
     and `target`.
   - `build` never needs to know `target` exists.
   - `target` never needs to know `build` exists.
   - A compromise of the kiosk gives an attacker no foothold on
     the workstation, and vice versa.

2. **Key-management cost.** In the relay model, setting up a new
   target is one operation: put `source`'s public key into
   `target`'s `authorized_keys`. With bilateral trust between
   build and target, every (build, target) pair needs its own
   key exchange — the operational cost grows as O(build × target)
   and is the dominant tax on the workflow once the fleet has
   more than one target host.

3. **Zero-setup new targets.** Adding a kiosk is one-line config
   change in `roamfile.yaml` plus a key drop, with no touches to
   any existing host. That's the ergonomic property that makes
   `roam` scale gracefully as targets multiply.

### The honest cost

Bytes transit the source host once in each direction: laptop
downloads from build, re-uploads to target. For a self-contained
.NET publish, the first deploy is ~80–150 MB. On a coffee-shop
wifi or a phone hotspot, that hurts.

**Delta transfers save us on every subsequent deploy.** After the
first sync, unchanged framework DLLs contribute zero bytes — the
sync tool (whichever one we use) only moves the files that actually
changed, which for an Avalonia iteration is typically your app's
main DLLs and PDBs, measured in tens of kilobytes to a few
megabytes. The relay cost is "big one-time transfer, cheap
thereafter." For most dev loops that's acceptable.

## Escape hatches for direct build→target transfer

Two alternative modes for users who want the efficiency of a direct
build→target connection and are willing to pay the setup cost:

### Mode B — agent forwarding

`roam` opens an SSH.NET connection to build and uses SSH.NET's
agent-forwarding support so that build can authenticate to target
using source's agent credentials. **Data flows directly
build→target** (no relay), but the authentication is borrowed from
source's agent.

- **Keys needed:** only source holds keys. Target trusts source's
  public key via `authorized_keys`. Build never holds anything on
  disk.
- **Security cost:** while the forwarding socket exists, anyone
  with root on build can impersonate source to any host source
  has keys for. For a home workstation the dev owns, this is
  fine. For a shared build host, it's a real footgun.
- **Implementation note:** in this mode, the build→target file
  transfer is a remote command on build (e.g. an rsync or scp
  invocation between build and target using the forwarded agent),
  not an SFTP operation driven from source. This requires the
  build host to have a suitable file-transfer tool installed.

### Mode C — direct mesh

Build has its own SSH keypair that target trusts. Most efficient at
runtime, highest operational cost.

- **Keys needed:** both source and build need keys; target needs
  both public keys in `authorized_keys`.
- **When to choose it:** you already manage keys centrally (SSH
  CA, Vault SSH secrets engine, Ansible/Packer-baked authorized
  keys) and the incremental cost of one more keypair is zero.
- **When not to:** anywhere the dev would be manually copy-pasting
  public keys around. That's a rathole and source-relay avoids it.

### How the user opts in

Per-profile knob in `roamfile.yaml`:

```yaml
profiles:
  kiosk:
    source: laptop
    build:  workstation
    target: kiosk-01
    transport:
      artifact-relay: source       # source | agent-forward | direct
    ...
```

Default is `source`. Users who hit real bandwidth pain opt to
`agent-forward`. Users with real central key infrastructure opt to
`direct`. Nobody configures anything on day one.

## SSH topology, tabulated

The matrix of required SSH reachability under each mode:

| Connection           | Default (`source` relay)                | Agent-forward                         | Direct mesh                   |
|----------------------|-----------------------------------------|---------------------------------------|-------------------------------|
| source → build       | **required** (sync source, relay-in)    | **required**                          | **required**                  |
| source → target      | **required** (relay-out + debug attach) | **required** (debug attach)           | **required** (debug attach)   |
| build → target       | not required                            | via forwarded agent (runtime only)    | **required** (persistent key) |
| target → outbound    | not required                            | not required                          | not required                  |

Two things worth naming explicitly:

- **`source → target` is required in every mode.** The debugger
  attach at step [6] is always a direct source→target SSH
  connection, because VSCode's `pipeTransport` is a single SSH
  hop by design. There is no mode where source gets away without
  reaching target directly.
- **`target → outbound` is never required.** Target hosts are
  passive receivers of work. They never initiate connections to
  source or build. This matters for firewalled environments where
  outbound rules are strict.

## Unusual topologies: `ProxyJump` in `~/.ssh/config`

Some target hosts are unreachable from source except via another
host — a kiosk behind a NAT that only the workstation can reach, a
lab machine on a management VLAN only accessible from a bastion.
The correct answer for these is **SSH's own `ProxyJump` in the
user's `~/.ssh/config`**, not a mirror of the feature inside
`roamfile.yaml`:

```ssh-config
Host kiosk-01
  HostName 10.100.0.42
  User kiosk
  ProxyJump workstation
```

`roam` discovers this via `ssh -G kiosk-01`, which resolves the
`ProxyJump` directive into a concrete chain. SSH.NET then implements
the chain programmatically (connect to workstation, forward a
channel to `10.100.0.42:22`, establish a second SSH session over
the channel). The developer configures the proxy in `~/.ssh/config`
once; `roam` picks it up automatically.

The lesson is that `roam` should **use** ssh_config, not
**duplicate** it. `ssh -G` is the bridge that makes this possible
without parsing ssh_config directly. Document the pattern, don't
re-implement it in YAML.

## Role-coincidence collapse

When two or three roles land on the same host, pipeline steps drop
out automatically. `roam` should detect this and skip the redundant
work rather than requiring special configuration:

- **source == build** (edit and compile on the same machine):
  step [1] is a no-op. Step [4] is source → target directly; no
  relay distinction exists because there's no separate build host.
- **source == target** (the motivating Avalonia case — laptop
  edits, workstation builds, laptop runs): step [1] pushes source
  up to the workstation. Step [4] pulls artifacts back to the
  laptop. Both legs are source↔build and use the SSH connection
  source already set up for step [1].
- **build == target** (publish and run on the same remote): step
  [4] is a no-op; the artifact never leaves build.
- **source == build == target** (everything on one host): `roam`
  degenerates into a fancy `dotnet publish && run`. Still works,
  but there's no reason to use `roam` in this case.

## The sync tool itself

Transport (who talks to whom) is one question; **which tool
actually moves the bytes** is another. The two are independent —
any of the sync tools below can implement any of the three
transport modes above — but they have very different properties
around Windows support, dependencies, and ergonomics.

### Default — SSH.NET SFTP with content-hash diffing

The default sync mechanism is built into `roam` itself: SFTP
operations over the SSH.NET connections `roam` already holds. No
external tools, no platform-specific dependencies, no Cygwin.

The algorithm for a one-shot deploy (`roam run <profile>`):

1. Hash every local file (XxHash64) and record the digests in a
   per-profile manifest under `.roam/`. The manifest is the content
   baseline.
2. A file is skipped only when its hash matches the manifest from the
   last deploy **and** an SFTP `ReadDir` on the remote confirms it
   still exists at the expected size (a cheap existence/size guard, so
   an out-of-band delete on the target forces a re-upload).
3. Transfer only files whose content changed — whole files via SFTP
   `put`, or, with `deploy.transfer: archive`, one tar.gz for the
   entire changed set (see "Archive transport" below).
4. Delete remote files that no longer exist in the source
   (sync with delete semantics to prevent stale artifacts).

The diff keys on content, not mtime: deterministic rebuilds re-stamp
the mtime of byte-identical assemblies, which an mtime-based diff would
mistake for a change and re-send.

On subsequent deploys, step 3 transfers 1–10 changed files
(your app DLLs and PDBs). The first deploy transfers everything
once (~80–150 MB for a self-contained publish), which is the same
cost any tool would have on a cold start.

- **Pros:** zero external dependencies; identical behavior on
  Windows, macOS, and Linux; ships entirely inside the `dotnet
  tool` package; uses the SSH.NET connection already established
  for command execution.
- **Cons:** no byte-level delta within files (whole-file transfer
  when a file changes); no built-in FS watching for live
  iteration.
- **Verdict:** the right default for `roam run <profile>`. Simple,
  cross-platform, no-install.

### Opt-in — archive transport for high-latency links

`deploy.transfer: archive` changes step 3 of the default sync. Instead
of one SFTP `put` per changed file, `roam` packs the entire changed set
into a single gzip-compressed tar, transfers that one stream, and
extracts it on the target with `tar -xpzf`. The content-hash diff still
decides *what* goes in the archive; deletions still happen per-file
(there are few of them).

Why it helps: on a high-latency link the wall is per-file round-trip
latency, not bandwidth. A cold deploy of a self-contained publish is
~250 files; at ~100+ ms RTT the per-file SFTP handshakes dominate. One
archive collapses those handshakes into a single transfer, and gzip
shrinks the payload too. `tar` natively restores mtimes and Unix modes,
so the per-file timestamp/permission round-trips disappear as well.

Cost and requirements:

- The target needs `tar` on `PATH`. It ships inbox on Windows 10
  1803+/Server 2019+ (bsdtar) and on every Linux/macOS.
- A failed extraction surfaces as a `sync` error (exit `6`); the local
  archive is always cleaned up, the remote archive on success.
- The win is largest on cold deploys and large changesets. A warm
  deploy of a handful of files sees little difference — the
  content-hash diff already makes that case cheap.

Default remains `per-file`. Archive mode is opt-in per profile until it
has proven out on real targets.

### Future: Mutagen for watch mode (post-v0)

This is a forward-looking note, not a v0 feature. `roam watch` is
deferred to v1 per [`implementation-contract.md`](implementation-contract.md),
and the Mutagen integration below arrives with it — not before.

**Mutagen** is a Go binary, cross-platform (native Windows, macOS,
Linux; no Cygwin required), purpose-built for dev-loop file sync.
Daemon-based, watches FS events, handles reconnects, supports
one-way and bidirectional modes, maps permissions sensibly across
OS boundaries.

- **Pros:** solves Windows cleanly with a single native binary;
  watch mode is exactly what `roam watch` wants; survives laptop
  suspend/resume and network flaps; status CLI is easy to wrap.
- **Cons:** an external dependency `roam` has to launch and manage;
  daemon lifecycle is another thing that can go wrong; not a
  NuGet package, so distribution is out-of-band relative to
  `dotnet tool install -g Roam.Cli`.
- **Verdict (post-v0):** when `roam watch` ships, Mutagen is the
  intended sync tier, with periodic SFTP polling as a fallback.

### FastRsync as a v1 optimization

**FastRsync** is a pure-C# NuGet package that implements the rsync
*algorithm* (signature → delta → patch) entirely in-process. Its
lineage is Octopus Deploy's **Octodiff**, which the Octopus folks
wrote originally to ship delta updates of deployment packages and
later factored out. Both are MIT-licensed and stable.

If the v0 metadata-diffing approach proves too slow for specific
workflows (large files that change frequently, bandwidth-
constrained links), FastRsync can be layered on top of the existing
SFTP transport to provide byte-level delta transfers within files.
This requires a remote-side helper to compute signatures (see
"Remote-side helpers" in [`packaging.md`](packaging.md)), so it's
deferred to v1. The v0 metadata diff is the right starting point
because it's simple, has no remote-side dependencies, and is fast
enough for the expected file counts.

### rsync as an explicit escape hatch

`rsync` over SSH remains available as a manual `--sync=rsync`
override for users who know they want it (Linux/macOS-only
pipelines, existing rsync expertise, specific flags they care
about). It is **never a default**, because rsync has no native
Windows build — every "rsync on Windows" solution requires
Cygwin or MSYS2, which drags in a second SSH stack that doesn't
share config, keys, or agent with the system OpenSSH. That
interop failure is the exact problem `roam`'s SSH.NET-based
transport is designed to avoid.

## Open questions

1. **FastRsync remote helper bootstrapping (v1).** If metadata-
   diffing proves insufficient and byte-level delta is needed, the
   FastRsync library has to run on both ends. On target, that means
   either (a) shipping a tiny self-contained .NET helper as part of
   the deploy and invoking it over SSH.NET, (b) using
   `dotnet-script` or a similar runner, or (c) implementing the
   algorithm twice so the target side can be any language/runtime.
   Leaning (a) — the target already has .NET because it's about to
   run the .NET app we just deployed — but deferred until the v0
   SFTP approach proves too slow.
2. **Watch-mode debouncing policy.** How long to wait after a file
   save before triggering a publish. 500 ms is the usual default;
   this wants to be configurable per profile.
3. **Partial failure handling.** What happens when step [4]
   half-succeeds — some files transferred, some not. For deploy-root
   targets, the stage-and-swap strategy in [`paths.md`](paths.md)
   mitigates this: the symlink still points at the old (working)
   directory until the new one is fully staged. For source-tree
   targets, the process is already stopped (step [3]) so partial
   state is visible but not running.
4. **Windows targets in particular.** A Windows kiosk is a
   legitimate target, and it breaks several assumptions
   (`systemctl` not existing for restart, `pgrep` not existing for
   readiness, file-lock semantics while replacing a running
   binary). v0 rejects Windows targets at preflight (see
   [`preflight.md`](preflight.md)). The transport layer is already
   clean — SSH.NET and SFTP handle Windows paths correctly — so
   the restriction is a deploy/readiness concern, not a transport
   one. Concrete plan: introduce an `ITargetShell` seam in v0 so
   the v2 Windows work can add a PowerShell-backed implementation
   without touching the sync or transport code.
5. **Whether Mutagen's bidirectional mode has a use case in
   `roam`.** Unlikely — `roam`'s pipeline steps are directional by
   design — but worth keeping in mind if a real bidirectional
   workflow appears (edit on the target, sync back to source?).
6. **`ssh -G` availability on minimal hosts.** `ssh -G` requires
   the OpenSSH client to be installed on the source host. On
   macOS and most Linux distributions this is always present. On
   Windows, OpenSSH ships inbox since Windows 10 1809 but may be
   absent on older or stripped-down installations. `roam` should
   fall back gracefully to explicit `roamfile.yaml` host config
   when `ssh -G` is unavailable.

## Summary

- **All remote operations go through SSH.NET** — command execution,
  SFTP file transfer, and port forwarding. The system `ssh` binary
  is not a runtime transport dependency. `ssh -G` is used as a
  one-shot config-resolution oracle at startup to bridge SSH.NET's
  lack of `~/.ssh/config` parsing.
- **Bytes flow source → build → target, with source acting as the
  relay for the build → target step by default.** This keeps the
  trust topology centered on the one host a human is touching, and
  eliminates key sprawl as targets multiply.
- **Agent forwarding** and **direct mesh** exist as opt-in escape
  hatches for users who have the infrastructure and want the
  efficiency of a direct build → target transfer.
- **`source → target` is always required**, because that's how the
  debugger attach works (via the emitted `launch.json`
  `pipeTransport`, which is VSCode's own SSH, not `roam`'s).
- **Sync tool is a layered choice.** Default to **SFTP metadata
  diffing** in-process for one-shot deploys (cross-platform,
  zero-install, no Cygwin); **Mutagen** opportunistically for
  `roam watch` (FS events, reconnects, native Windows support);
  **FastRsync** as a v1 optimization for byte-level delta if
  needed; **rsync** as an explicit override for Linux/macOS-only
  users who want it.
- **`ProxyJump` in `~/.ssh/config`** handles unusual topologies
  where target is only reachable via an intermediate. `ssh -G`
  resolves the chain; SSH.NET implements it as programmatic port
  forwarding. `roam` uses ssh_config; it does not duplicate
  ssh_config in YAML.
