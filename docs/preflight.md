# Preflight validation

**Status:** load-bearing for v0. Preflight is the contract that no
destructive work happens when the workflow is misconfigured. This
document turns the bullet list in
[`implementation-contract.md`](implementation-contract.md) into a
concrete, itemized spec so the implementation can be reviewed against a
checklist rather than against prose.

## When preflight runs

Preflight runs at the start of `roam run <profile>` and `roam attach
<profile>`, before any remote command that could modify state on any
host. If any check fails, `roam` exits with code `4` (`preflight`) — see
[`exit-codes.md`](exit-codes.md) — and prints the failing check's
message.

Preflight does **not** run for `roam init`. `init` has no remote
state to protect; it only writes `roamfile.yaml` locally.

## Order

Checks run in the order listed below. Failure stops preflight
immediately and reports only the first failing check. This keeps the
output focused and avoids printing a wall of cascading failures for
one root cause.

## The checks

### 1. `profile-exists`

**What:** the profile name requested on the command line exists in the
loaded `roamfile.yaml` under `profiles:`.

**Pass:** profile key is present.

**Failure message:** `profile 'kiosk' is not defined in roamfile.yaml
(known profiles: dev-local, workstation-to-laptop)`.

### 2. `hosts-defined`

**What:** every host the profile names (`source`, `build`, `target`) is
present in the `hosts:` map.

**Pass:** all three roles resolve to known host entries.

**Failure message:** `profile 'kiosk' references host 'kiosk-01' which
is not defined in roamfile.yaml`.

### 3. `ssh-config-resolved`

**What:** for each named host, `ssh -G <alias>` returns successfully
and produces a non-empty `hostname` value. If `ssh` is not on `PATH`,
`roam` instead requires the host to carry explicit `ssh:` and `user:`
fields in `roamfile.yaml`.

**Pass:** every host resolves to a concrete `{hostname, user, port,
identityfile}` tuple, from either `ssh -G` or explicit config.

**Failure messages:**
- `ssh -G kiosk-01 failed: No such host` (propagate the underlying
  error).
- `ssh not found on PATH; host 'kiosk-01' requires explicit 'ssh:' and
  'user:' in roamfile.yaml`.

### 4. `ssh-auth-works`

**What:** SSH.NET opens an authenticated session to each host and runs
the trivial command `true` (which exits 0 on all Unix targets; on
Windows targets the equivalent is `rem`). The session is held for the
remainder of the run to avoid duplicate handshakes.

**Pass:** every host returns a 0 exit code from the probe command
within a 10-second timeout.

**Failure messages:**
- `ssh to workstation failed: authentication rejected`
- `ssh to kiosk-01 failed: connection timed out after 10s`
- `ssh to kiosk-01 failed: host key verification failed`

### 5. `build-has-dotnet`

**What:** on the build host only, run `dotnet --version` over SSH.NET.
Parse the version and require `>=` the target framework's minimum SDK
(v0 target: `net10.0`, minimum SDK `10.0.100`).

**Pass:** `dotnet` responds and its version meets the minimum.

**Failure messages:**
- `build host 'workstation' has no dotnet on PATH`
- `build host 'workstation' has dotnet 8.0.402; roam requires >= 10.0.100`

### 6. `workspaces-usable`

**What:** for each of the source and build hosts that declares a
`workspace:`, verify the path exists and is a directory. If it does
not exist, `roam` creates it (recursive `mkdir -p`) only when its
parent exists and is writable. `roam` never creates a workspace
outside an existing parent.

**Pass:** each workspace is a directory the session user can read
and write.

**Failure messages:**
- `workspace '/home/dev/src/kiosk-ui' on laptop is not a directory`
- `workspace '/srv/build' on workstation is not writable by user
  'dev'`
- `cannot create workspace '/nonexistent/path/repo' on workstation:
  parent '/nonexistent/path' does not exist`

### 7. `deploy-path-writable`

**What:** on the target host, create and remove a marker file
(`.roam-preflight-<pid>-<ts>`) inside `deploy.path`. If `deploy.path`
does not exist, `roam` creates it (recursive) when its parent exists
and is writable.

**Pass:** marker file is created and removed without error.

**Failure messages:**
- `deploy path '/opt/kiosk-ui' on kiosk-01 is not writable by user
  'kiosk'`
- `cannot create deploy path '/opt/kiosk-ui' on kiosk-01: parent
  '/opt' does not exist`

### 8. `target-has-runtime` (framework-dependent publishes only)

**What:** when `publish.self-contained: false` and the target is a
remote host, run `dotnet --list-runtimes` on the target and confirm a
`Microsoft.NETCore.App` matching the project's target-framework major
with an equal-or-higher minor is installed (the default host
roll-forward policy: same major, minor `>=` requested).

This check is deliberately **lenient**. A confident mismatch — the
target has `dotnet` but no compatible major — is a hard `preflight`
failure, because shipping a framework-dependent build to a host without
the runtime guarantees a `start` failure. Anything `roam` cannot
determine (the target framework can't be parsed, `dotnet` is not on the
target's `PATH`, the output is unparseable) only warns and proceeds: an
apphost can still locate a runtime the `dotnet` muxer can't, so a hard
block there would create false negatives. Self-contained publishes and
local targets skip the check entirely.

**Pass:** a compatible `Microsoft.NETCore.App` is present, or
compatibility cannot be determined (warn-and-proceed).

**Failure message:** `target 'kiosk-01' has no .NET 10.0 runtime for
this framework-dependent publish (Microsoft.NETCore.App found: 8.0.11,
9.0.2); install the runtime on the target or set publish.self-contained:
true`.

### 9. `publish-profile-exists`

**What:** on the source host, verify that
`Properties/PublishProfiles/<publish-profile>.pubxml` exists relative
to the solution/csproj root.

**Pass:** file exists and is readable.

**Failure message:** `publish profile 'ReleaseKioskArm64' not found at
'src/KioskUi/Properties/PublishProfiles/ReleaseKioskArm64.pubxml'`.

### 10. `launch-profile-exists`

**What:** on the source host, verify that the `launch-profile` named
in the roam profile exists inside `Properties/launchSettings.json`.

**Pass:** the named key is present in `profiles` inside
`launchSettings.json`.

**Failure message:** `launch profile 'Development' not found in
'src/KioskUi/Properties/launchSettings.json' (available: Production,
Staging)`.

### 11. `debug-prerequisites` (attach only)

**What:** for `roam attach`, verify:

- `debug.enabled: true` in the profile,
- `debug.editor` is `vscode` (v0's only supported editor),
- `debug.debugger` is `vsdbg` (v0's only supported debugger path),
- a writable `.vscode/` directory exists on the source host (created
  if missing, same rule as workspaces).

**Pass:** all four conditions hold.

**Failure messages:**
- `profile 'kiosk' has debug.enabled: false; roam attach has nothing
  to emit`
- `profile 'kiosk' uses debug.editor='rider'; v0 only supports 'vscode'`
- `profile 'kiosk' uses debug.debugger='netcoredbg'; v0 only supports
  'vsdbg'`

## Platform-support check

`roam` runs Windows and Linux hosts in the `source`, `build`, and `target`
roles (the earlier "Windows target-only" restriction was lifted once the
Windows source -> build -> target path was validated end-to-end). macOS is
design-intended but untested — see [platform-readiness.md](platform-readiness.md)
for the proven matrix. Windows target hosts go through the same SSH,
deploy-path, publish-profile, and launch-profile checks as Linux; their probe,
deploy, and default readiness commands are translated to PowerShell, and
`deploy.interactive-session` wraps the start in a desktop-session scheduled
task.

One platform-consistency check runs first, before any remote probe: the publish
RID must name the same OS as the target host. A profile that ships, say, a
`win-x64` publish to an `os: linux` target produces an OS-specific apphost that
can only fail at `start` — after a full publish + sync — so `roam` fails fast
at preflight instead:

```
publish RID 'win-x64' targets windows, but the target host is os=linux. Set publish.rid (or the pubxml RuntimeIdentifier) to a linux RID (e.g. linux-x64).
```

The check is fail-open: a portable framework-dependent publish (no RID), an
unrecognized RID OS family (e.g. `freebsd-x64`), or an unset target `os` all
pass — `roam` only blocks a confident mismatch.

## Idempotency

Preflight has two state-touching effects:

- creating missing workspaces and deploy paths, and
- creating and removing the `.roam-preflight-*` marker file on the
  target.

Both are idempotent: running preflight twice against the same
environment leaves it in the same state. The marker file lifetime is
one preflight run; if `roam` is killed mid-preflight, the file may be
left behind — the next run will overwrite it.

## Cost budget

Preflight for a three-host profile should finish in under **2 seconds**
on a warm Tailscale network. SSH sessions opened during preflight are
reused by the main pipeline, so the real cost of preflight is the
command probes (`true`, `dotnet --version`), not the TCP/TLS handshake.

If preflight exceeds 10 seconds, that is itself a `preflight` failure
with message `preflight timed out; network latency to
<host> exceeds budget`.

## Not in v0

- Probing for `netcoredbg` on the target (only `vsdbg` path is in v0).
- Probing for `systemctl` (the diagnostic path degrades gracefully per
  [`readiness.md`](readiness.md)).
- Ping-style reachability checks that bypass SSH. If SSH can't reach
  the host, that is the useful failure.
- `--skip-preflight` or `--force`. Preflight is load-bearing; there is
  no bypass flag in v0.
