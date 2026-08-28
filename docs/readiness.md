# Process readiness after deploy

**Status:** design decision. This document describes how `roam`
verifies that the target process actually started after deploy,
and what it does when it didn't.

## The problem

The fixed pipeline runs: sync source → publish → stop → sync
artifacts → start → attach debugger. Between "start" and "attach,"
there's an implicit assumption: the process is running and ready
to accept a debugger connection.

During active development, this assumption fails regularly. The
app crashes on startup because of a missing config file, a bad
migration, a null reference in initialization, or a runtime the
self-contained publish didn't bundle. The debugger attach then
fails with an unhelpful error ("could not find process"), and the
developer has to SSH into the target manually to figure out what
happened.

`roam` should close this gap by verifying readiness and surfacing
failure information directly.

## The solution: poll for process, surface stderr on failure

After executing the profile's `start` command, `roam` runs a
readiness check before proceeding to debugger attach (or reporting
success for non-debug profiles).

### Default behavior (no explicit `ready` command)

Readiness uses the profile's `debug.process-name`, which is the same
value the emitted `launch.json` writes into `processName` (see
[`debugger.md`](debugger.md)). `roam init` populates it explicitly
from the csproj `<AssemblyName>` (falling back to the project name);
`roam` never tries to infer it at run time.

If `debug.process-name` is set, `roam` polls for the process on the
target:

1. Wait 500 ms after `start` returns.
2. Run `pgrep -x <process-name>` on the target via SSH.NET.
3. If found, report success and proceed to attach.
4. If not found, retry every `ready-interval-ms` (default: 500 ms) up
   to `ready-timeout` (default: 15 seconds).
5. If still not found after the timeout, report failure and
   surface diagnostic output (see "Failure diagnostics" below).

If the profile has no `debug` block and no `process-name`, `roam`
skips the readiness check entirely — the user doesn't care about
attach and just wants a fire-and-forget deploy.

### Explicit `ready` command

For cases where `pgrep` isn't sufficient — the process starts but
isn't ready to accept connections, or the service name doesn't
match the binary name — the profile can specify a custom readiness
command:

```yaml
profiles:
  kiosk:
    deploy:
      stop:  systemctl --user stop kiosk-ui
      start: systemctl --user start kiosk-ui
      ready: systemctl --user is-active kiosk-ui
      ready-timeout: 20          # seconds, default 15
      ready-interval-ms: 500     # default 500
```

`roam` runs `ready` the same way it runs the default `pgrep`
check: poll on an interval until the command exits 0 or the
timeout expires. The `ready` command runs on the target host
via SSH.NET.

### Failure diagnostics

When the readiness check times out, `roam` needs to tell the
developer *why* the process didn't start, not just that it didn't.
The diagnostic output depends on what's available:

1. **If `stop`/`start` use systemd** (detected by the presence of
   `systemctl` in the commands): run
   `journalctl --user -u <service> -n 30 --no-pager` on the target
   and print the output. This captures stdout, stderr, and crash
   information from the most recent service start attempt.
2. **Otherwise:** print a message suggesting the developer SSH into
   the target and check the process's logs manually. `roam` does
   not know where arbitrary processes write their output.

The diagnostic output is printed directly to the developer's
terminal. If `roam` is in watch mode, it also prints a separator
line so the failure is visible in the stream of repeated deploys.

## What this looks like in practice

### Happy path

```
$ roam run kiosk
  [1/6] sync-source     laptop → workstation       0.8s
  [2/6] publish          workstation                12.4s
  [3/6] stop             kiosk-01                   0.3s
  [4/6] sync-artifacts   workstation → kiosk-01     1.2s
  [5/6] start            kiosk-01                   0.2s
  [✓]   ready            kiosk-01  KioskUi (pid 4821)  1.1s
  Done.
```

### Failure path

```
$ roam run kiosk
  [1/6] sync-source     laptop → workstation       0.8s
  [2/6] publish          workstation                12.4s
  [3/6] stop             kiosk-01                   0.3s
  [4/6] sync-artifacts   workstation → kiosk-01     1.2s
  [5/6] start            kiosk-01                   0.2s
  [✗]   ready            kiosk-01  timed out after 15s

  Process KioskUi did not start. Last 30 lines from journalctl:

  Apr 16 14:23:01 kiosk-01 KioskUi[4821]: Unhandled exception.
  Apr 16 14:23:01 kiosk-01 KioskUi[4821]: System.IO.FileNotFoundException:
    Could not find file '/opt/kiosk-ui/appsettings.json'.
  Apr 16 14:23:01 kiosk-01 systemd[1100]: kiosk-ui.service: Main process exited, code=dumped, status=6/ABRT

roam: exit=8 step=ready host=kiosk-01
```

The developer sees the crash reason immediately, without SSHing
into the target or reading systemd's journal manually.

## What this is not

- **Not a health check system.** `roam` checks "is the process
  alive?" once, at deploy time. It does not monitor the process
  after that. Monitoring is the job of systemd, monit, or whatever
  process supervisor the target runs.
- **Not an HTTP readiness probe.** For web apps that expose a
  `/health` endpoint, a custom `ready` command like
  `curl -sf http://localhost:5000/health` works. But `roam` doesn't
  have built-in HTTP probing — the `ready` command is the extension
  point.
- **Not a retry mechanism.** If the process crashes on startup,
  `roam` reports the failure. It does not attempt to restart the
  process. That's the developer's job (fix the bug, `roam run`
  again).

## Timing defaults and how they were chosen

The v0 defaults are:

- `ready-timeout`: **15 seconds**
- `ready-interval-ms`: **500 ms** (also the initial post-`start` wait)

These are calibrated for the Compose fixture (SampleApp in
`delayed-start` mode with a 3-second delay comfortably fits) and the
motivating Avalonia cold-start path (measured at ~5–8 s on a
virtual machine during early validation). Both are per-profile configurable via
`ready-timeout` and `ready-interval-ms` in `deploy:`.

Before the first public release, the Compose integration suite must
include a step that records p95 cold-start time for SampleApp in
each fixture mode; if the measurement falls outside the defaults
here, the defaults are adjusted and this section is updated.

## Open questions

1. **Interaction with `roam watch`.** In watch mode, a startup
   failure should not kill the watch session — the developer will
   fix the bug and save again, triggering a new deploy cycle. The
   failure should be reported loudly but the watcher should keep
   watching. (`roam watch` itself is post-v0.)
2. **Processes that background themselves.** Some processes
   daemonize (fork and exit the parent). `pgrep` will find them,
   but the `start` command may return before the process is ready.
   The `ready` command is the escape hatch for these cases.
3. **Windows-target readiness.** `pgrep` and `journalctl` don't
   exist on Windows. The current implementation uses `Get-Process`
   for the happy-path process check on Windows targets, but Windows
   failure diagnostics are not yet equivalent to the planned Linux
   `journalctl` path. A future hardening pass should add
   `Get-WinEvent`-based diagnostics or document a narrower v0
   support contract.
