# Platform readiness

**Status:** active engineering tracker. This is evidence and risk tracking, not a design contract.

Design intent lives in [`design.md`](design.md), [`implementation-contract.md`](implementation-contract.md), [`paths.md`](paths.md), [`transport.md`](transport.md), and [`state.md`](state.md). This file records what has actually been proven and what must be hardened before `roam` is broadly usable across source/build/target platform combinations.

## Confidence summary

| Area | Confidence | Evidence | Next action |
|------|------------|----------|-------------|
| Metadata-diffed sync architecture | High | `MetadataDiffSyncEngineTests`; opt-in xUnit Compose SFTP E2E including stale-owned delete; live Windows deploy; manifest-scoped stale delete regression | Add CI job that runs the Compose lane on a Docker host |
| Manifest-scoped artifact ownership | High | Unit regression, Compose stale-owned delete/unmanaged sentinel preservation, and live Windows unmanaged sentinel preservation | Keep asserted in every E2E deploy lane |
| Local Linux source/build -> Windows target | Medium-high | Live deploy to a Windows target; warm sync around 0.6s | Keep as manual acceptance lane until automated Windows lab exists |
| Local Linux source/build -> local target | Medium | Unit and integration smoke coverage | Add direct E2E assertion for local deploy layout |
| Local Linux source -> remote Linux build -> remote Linux target | Medium-high | Opt-in xUnit Compose lab on a Docker host, separate `source`/`build`/`target` SSH containers; cold/warm deploys pass | Add CI job on Docker-capable runner |
| Remote Linux build -> Windows target | Medium | Live deploy using a remote Linux build host and a Windows target | Automate or keep as required manual acceptance lane |
| Windows source host (local-only build) | Medium | Live GUI-app deploy from Windows source/build to two separate Windows targets; both processes land in `SessionId 1`; warm sync drops to <2s | Add automated coverage; encrypted/non-standard key paths still need exercise |
| Windows source/build -> Linux target | Medium-high | Live Windows controller -> Linux target VM: self-contained linux-x64 apphost lands `0755` and starts, `detach` keeps it alive past the channel close, marker captured; `tests/labs/xplat` + `SyncPermissionsTests` | Automate via the self-hosted-runner E2E lane |
| macOS target | Unknown | Design-intended but untested | Defer until Linux/Windows matrix is stable |
| SSH config/auth edge cases | Medium | Explicit key, multiple `ssh -G identityfile` entries, fallback key discovery, missing/unsupported key diagnostics, and a live Linux -> Windows deploy | Add tests for encrypted keys, unreadable keys, wrong-but-loadable keys, and ProxyJump |
| Readiness diagnostics | Medium-low | Happy-path ready check works; docs describe more than implementation proves | Implement/test failure diagnostics by platform |

## Currently verified scenarios

| Priority | Scenario | Status | Evidence |
|----------|----------|--------|----------|
| P0 | Manifest-owned stale files may be deleted while unmanaged files are preserved | Verified | `MetadataDiffSyncEngineTests`; `RoamCommandsArtifactSyncTests`; `tests/labs/compose/run-lab.sh` |
| P0 | Warm artifact sync uploads only changed files | Verified at engine level | `MetadataDiffSyncEngineTests` |
| P0 | Local Linux source/build deploys to Windows target | Verified manually | Live run against a Windows target |
| P0 | Local Linux source syncs to a remote Linux build host, publishes there, then deploys to a Windows target | Verified manually | Live run; `sync-source` 1.5s, `publish` 6.8s, `sync-artifacts` 4.2s |
| P0 | Compose Linux source -> remote Linux build -> remote Linux target E2E | Verified through opt-in xUnit lane on Docker host | `/tmp/roam-compose-lab` on a Docker host; `ROAM_RUN_COMPOSE_LAB=1 dotnet test tests/Roam.IntegrationTests/Roam.IntegrationTests.csproj --filter ComposeLabRunnerPassesWhenExplicitlyEnabled`; wraps `tests/labs/compose/run-lab.sh` |
| P0 | Compose remote publish materialization preserves nested files and mtimes | Verified through opt-in xUnit lane on Docker host | `tests/labs/compose/run-lab.sh` verifies `assets/nested/probe.txt` exists on build and target, target mtime equals build publish mtime, and `/tmp/roam-publish-*` relay dirs are cleaned |
| P0 | Compose Linux target ownership boundary survives redeploy | Verified manually on Docker host | `tests/labs/compose/run-lab.sh` seeds `/opt/roam-fixture/stale-owned.txt` as manifest-owned and `/opt/roam-fixture/unmanaged-sentinel.txt` as unmanaged, then verifies stale deletion and sentinel preservation |
| P0 | Windows target unmanaged sentinel survives deploy | Verified manually | the target's unmanaged sentinel file remained untouched |
| P1 | Windows source/build to Windows GUI target (Avalonia) lands the process in the interactive desktop session (`SessionId 1`) via `deploy.interactive-session: true` | Verified manually | Two Windows target profiles, both ending with `[✓] ready (PID) … Done.`; process inspection on each target shows `SessionId 1` |
| P1 | Interactive-session scheduled task restarts after a target reboot via `deploy.interactive-session-trigger: at-logon` (also valid on `run:`), which adds `New-ScheduledTaskTrigger -AtLogOn -User <target user>` to `Roam_<profile>` | Implemented and unit-tested; hardware reboot-verification pending | `StartCommandTests` (AtLogOn trigger emitted when set; byte-for-byte no-trigger wrapper when unset, locking back-compat) and `ConfigLoaderTests` (parse on deploy + run, default None, rejects unknown value). Opt-in (default off); requires the target user logged on, i.e. autologon for a headless station. Motivated by a headless station not recovering after a reboot |
| P1 | Interactive-session scheduled task runs elevated (High IL) in the desktop session via `deploy.run-level: highest` (also valid on `run:`), which registers the principal `-RunLevel Highest`; `roam deploy <profile>` registers the task without starting it | Implemented and unit-tested; hardware elevation-verification pending | `StartCommandTests` (`-RunLevel Highest` emitted when set; byte-for-byte `-RunLevel Limited` when unset, locking back-compat; register-without-`Start-ScheduledTask` when `startTask:false`) and `ConfigLoaderTests` (parse on deploy + run, default Limited, rejects unknown value). Opt-in (default `limited`); Task Scheduler skips the UAC prompt only when the principal user is a local admin. Supports an elevated-supervisor + limited-workload posture |
| P1 | SSH.NET identity selection and diagnostic formatting | Verified | `SshNetConnectionInfoFactoryTests`; `SshHostResolverIdentityTests`; a live Linux -> Windows deploy after changes |
| P1 | Unit suite passes after SFTP sync rewrite | Verified | `dotnet test tests/Roam.UnitTests/Roam.UnitTests.csproj -v minimal` |
| P1 | Integration smoke suite passes after SFTP sync rewrite | Verified | `dotnet test tests/Roam.IntegrationTests/Roam.IntegrationTests.csproj -v minimal` |
| P1 | Release build succeeds after SFTP sync rewrite | Verified | `dotnet build -c Release src/Roam/Roam.csproj` |
| P0 | Windows controller -> Linux target: a self-contained `linux-x64` apphost published from a Windows host (which can't read Unix modes) lands executable (`0755`) and starts; data files stay `0644` | Verified manually + unit | Live Windows controller -> Linux target: apphost `755`, `SampleApp.dll` `644`, process running, marker in the detach log; `SyncPermissionsTests`; `tests/labs/xplat/roamfile.linux.yaml` |
| P1 | Publish RID is validated against the target host OS at preflight, before any remote work (`win-x64` -> an `os: linux` target fails fast) | Verified | `RuntimeCompatibilityTests` (`ValidatePublishOsTargetsHost`, `RidOperatingSystem`); preflight guard in `RunPreflightAsync` |
| P1 | Opt-in Unix `detach` backgrounds a service-mode start (`nohup … < /dev/null &`) so it survives the SSH channel close | Verified manually + unit | Live Windows controller -> Linux target (PID running after `start` returned, marker captured); `StartCommandTests` (nohup wrap, register-only/Windows opt-out) |
| P1 | CI runs the unit suite on Linux (`ubuntu-latest`) as well as Windows, exercising the Unix-controller branches | Verified | `ci.yml` on every push and PR (Linux); `build.yml` dispatch green on both OSes; the Roam.slnx fix so `dotnet test` actually discovers the projects |
| P0 | Full controller x target 2x2 proven end-to-end on real VMs | Verified manually | Windows and Linux controllers x Linux and Windows target VMs: **W->L** apphost `755`, process running; **W->W** running; **L->L** apphost `755` (real source-mode mirror), process running; **L->W** running. Fixtures in `tests/labs/xplat/` |
| P1 | Linux-controller host resolution: `ssh -G <host>` is no longer shell-quoted (RunAsync is not a shell), so a host without an explicit `user:` resolves on a Linux controller instead of failing preflight with "hostname contains invalid characters" | Verified | `SshHostResolver`; L->L and L->W deploys from a Linux controller clear preflight |

## Priority gaps

### P0 — release-blocking confidence gaps

1. **Automated multi-host E2E lab needs CI scheduling.**
   The Compose lab now exercises separate SSH hosts with real `roam run` invocations and has an opt-in xUnit wrapper. It still needs a CI job on a Docker-capable runner.

2. **Remote build artifact materialization needs broader E2E assertions.**
   `roam` now downloads publish output from a remote build host using SFTP before relaying to the target, and the Compose lab proves nested publish output, mtime preservation, and temp cleanup. Add remaining assertions for source/relay isolation and broader directory shapes.

3. **SSH authentication behavior needs remaining edge-case coverage.**
   The live test exposed that CLI SSH success does not automatically imply SSH.NET success. Candidate ordering and actionable diagnostics now have unit coverage, but encrypted/unreadable/wrong loadable keys and ssh-agent policy still need final v0 decisions.

4. **Readiness docs exceed proven behavior.**
   The docs describe systemd/journal diagnostics and Windows-specific readiness concepts. The implementation needs either coverage or clearly scoped documentation.

### P1 — high-value hardening

1. **Windows path matrix.**
   Add coverage for `C:/path`, `C:\path`, nested directories, spaces, and SFTP server path normalization.

2. **ProxyJump / bastion behavior.**
   The docs and test architecture call this out. SSH.NET transport either needs support or an explicit v0 limitation.

3. **Sync observability.**
   Users need to know how many files were scanned, skipped, uploaded, deleted, and how many bytes moved when `--verbose` is set.

4. **Partial failure semantics.**
   The sync engine needs tests for failures during upload/delete and should avoid writing misleading manifests after failed sync.

5. **Source sync ownership policy.**
   Source sync currently uses git-tracked files and manifest-scoped delete semantics. Confirm this is the right contract for remote build workspaces, especially around generated/untracked files.

### P2 — later platform expansion

1. **Windows source host.**
   Decide whether this is in v0. If not, document it explicitly.

2. **macOS target.**
   Add only after Linux and Windows target lanes are stable.

3. **Nightly full-VM acceptance lab.**
   Use the VM tier for systemd, bootstrapping, network policy, and Windows/GUI-adjacent validation once the Compose lane is stable.

## Platform matrix target

| Source | Build | Target | Priority | Expected support | Current confidence |
|--------|-------|--------|----------|------------------|--------------------|
| Linux local | Linux local | Linux local | P0 | v0 | Medium |
| Linux local | Linux remote | Linux remote | P0 | v0 | Medium-high |
| Linux local | Linux local | Windows remote | P0 | v0 for target deploy | Medium-high |
| Linux local | Linux remote | Windows remote | P0 | v0 for target deploy | Medium |
| Linux local | Linux remote | Linux ARM remote | P1 | v0 if publish RID/toolchain configured | Low |
| Windows local | Windows local | Windows remote | P1 | v0 spike (interactive-session ceremony required for GUI targets) | Medium — live GUI-app deploys verified |
| Windows local | Linux remote | Windows/Linux | P2 | undecided | Unknown |
| macOS local | macOS/Linux | macOS/Linux | P2 | intended eventually | Unknown |

## Release gates before claiming broad usability

- [x] Compose E2E lab runs separate `source`, `build`, and `target` SSH hosts.
- [x] E2E covers local Linux source -> remote Linux build -> remote Linux target.
- [x] E2E covers remote build artifact materialization through SFTP.
- [x] E2E asserts unmanaged deploy files survive repeated deploys.
- [x] E2E asserts stale manifest-owned files are deleted.
- [x] Compose E2E lab is wrapped by an opt-in xUnit integration test.
- [ ] E2E or manual acceptance covers Windows target deploy after every sync-engine change.
- [x] SSH.NET auth failure messages identify host, user, key candidates, and next corrective action without exposing secrets.
- [ ] ProxyJump is either supported and tested or documented as out of scope for v0.
- [ ] Readiness failure diagnostics are implemented and tested for the platforms claimed in v0.
- [ ] `--verbose` sync output reports scanned/uploaded/skipped/deleted counts.
