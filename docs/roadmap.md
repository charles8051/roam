# Roadmap

**Status:** prioritized engineering backlog. This is not a release-date promise.

The immediate goal is to turn the working v0 prototype into a tool we can trust across the source/build/target combinations described in the design docs. Items are ordered by priority and dependency: do the earliest P0 items first unless new evidence changes the risk ranking.

## Shipped since this roadmap was written (2026-06)

Cross-platform hardening landed on `main` after this roadmap was drafted. These
closed correctness gaps the roadmap had assumed away — it treated the
cross-platform code as working and only needing test coverage, when in fact the
Linux-controller and Windows-to-Linux paths were broken:

- **Unix exec-bit from any controller.** A self-contained Linux publish produced
  on a Windows controller now lands its apphost / `createdump` / `*.sh`
  executable (`0755`); data files stay `0644`. Previously a Windows controller
  skipped `chmod` entirely, so a Linux `start` failed permission-denied. Covered
  by `SyncPermissionsTests`.
- **RID to target.os preflight.** `roam` fails fast when `publish.rid` targets a
  different OS than the resolved target host (e.g. `linux-x64` to a Windows
  target), with a suggested RID.
- **Opt-in Unix `detach`.** `deploy.detach: true` backgrounds a service-mode
  start under `nohup` so it survives the SSH channel close (the Unix analogue of
  the Windows interactive-session task). Reboot-durability via systemd is still
  open (see the issue tracker).
- **Controller PATH-resolved `bash`** and the **`ssh -G` quoting fix** for Linux
  controllers (no more "hostname contains invalid characters").
- **CI now actually runs the tests** on both `windows-latest` and
  `ubuntu-latest` (test discovery was silently matching zero tests before).
- **The 2x2 deploy matrix is proven on real VMs** (W-to-L, W-to-W,
  L-to-L, L-to-W) — the manual half of item 15. Evidence in
  [`platform-readiness.md`](platform-readiness.md).

The items below are annotated where this work advanced or closed them.

## P0 — prove the cross-platform product shape

### 1. Make the Compose lab the default E2E confidence lane

**Goal:** Run `roam run` against separate SSH hosts without depending on personal machines.

**Why first:** Almost every remaining confidence gap depends on having repeatable multi-host tests.

**Tasks:**

- [x] Inventory current `tests/labs/compose` state and identify what already works.
- [x] Ensure Compose starts deterministic `source`, `build`, and `target` SSH hosts.
- [x] Check in deterministic SSH keys/config for the lab only.
- [x] Put the SampleApp workspace on the source host.
- [x] Ensure the build host has the .NET SDK.
- [x] Ensure the target host can run self-contained publish output without the SDK.
- [x] Add a single command/script to bring the lab up, run tests, and tear it down.
- [x] Add an opt-in xUnit integration test that runs the Compose lab when `ROAM_RUN_COMPOSE_LAB=1` is set.
- [x] Document the command in `tests/labs/compose/README.md` and `docs/test-architecture.md`.

**Acceptance:** one command runs an E2E `roam run` through source sync, publish, stop, artifact sync, start, and readiness.

### 2. Add Linux remote-build -> Linux remote-target E2E

**Goal:** Prove the most automatable real topology.

**Tasks:**

- [x] Add a roamfile profile for `source=source`, `build=build`, `target=target` in the Compose fixture.
- [x] Assert build workspace receives only git-tracked source files.
- [x] Assert target deploy root receives publish artifacts.
- [x] Assert the target process starts and readiness passes.
- [x] Run the scenario twice and assert warm sync succeeds.

**Acceptance:** test fails if source sync, remote publish, SFTP artifact relay, target start, or readiness breaks.

### 3. Add deploy ownership E2E assertions

**Goal:** Promote the manifest-scoped delete contract from unit coverage to real host coverage.

**Tasks:**

- [x] Before second deploy, create an unmanaged sentinel file under the target deploy root.
- [x] Seed the previous artifact manifest and target deploy root so one manifest-owned file is stale.
- [x] Run deploy again.
- [x] Assert stale manifest-owned file is gone.
- [x] Assert unmanaged sentinel still exists and contents are unchanged.
- [ ] Assert unchanged files were not re-uploaded when metadata matches.

**Acceptance:** E2E proves `artifacts.json` is the ownership boundary on a real SSH/SFTP target.

### 4. Harden SSH.NET authentication and diagnostics

**Goal:** Make auth failures understandable and reduce mismatch between `ssh` CLI behavior and SSH.NET behavior.

**Tasks:**

- [x] Parse all usable `ssh -G identityfile` candidates instead of relying on a single configured path or fallback list.
- [x] Preserve explicit `identity-file` precedence from `roamfile.yaml`.
- [x] Try candidate keys in deterministic order.
- [x] Add tests for explicit key, multiple keys, missing key, and unsupported key diagnostics.
- [x] Ensure errors include host alias, resolved hostname, user, port, and candidate key paths.
- [x] Ensure errors do not print private-key contents or secrets.
- [ ] Add tests for encrypted key, unreadable key, and wrong-but-loadable key failures.
- [ ] Decide whether ssh-agent support is required for v0. If not, document that limitation.

**Acceptance:** a failed SFTP connection tells the user exactly what to fix without leaking credentials.

### 5. Cover remote build artifact materialization through SFTP

**Goal:** Verify the new SFTP download path from build host publish output to source relay.

**Tasks:**

- [ ] Add an E2E assertion that publish output exists only on the remote build host before artifact sync.
- [ ] Run artifact sync through `SftpDirectoryDownloader`.
- [x] Assert nested publish directories and file mtimes survive materialization.
- [x] Assert temp materialization directories are cleaned after deploy.

**Acceptance:** remote publish output is retrieved and deployed without SCP or shell copy assumptions.

## P1 — make the tool pleasant and debuggable

**Direction — agent-first usability ([ADR-0002](adr/0002-agent-first-usability.md)).**
roam has two consumers: humans (who debug with an interactive IDE debugger) and
agents (who debug by reading logs, dumps, traces, and `--json` output over a
shell). The agent-facing cluster below — sync observability (#6), the read-only
doctor (#9), and a new `roam diag` log/dump/trace bundle verb — is the priority.
The interactive debugger-attach loop (`roam attach`) is a **human-only** DX
feature at its current maturity; proving or expanding it E2E is **deferred**, not
on the agent-value path. See ADR-0002 for the decision and the `roam diag` design.

### 6. Add sync observability

**Goal:** Make `roam run --verbose` explain sync decisions.

**Tasks:**

- [ ] Extend the sync engine to return stats: remote scanned, local scanned, uploaded, skipped, deleted, bytes uploaded.
- [ ] Print concise stats in verbose mode for source sync and artifact sync.
- [ ] Include stats in JSONL logs if logging is enabled.
- [ ] Add unit tests for stats on changed/unchanged/stale cases.

**Acceptance:** users can tell whether a slow deploy is scanning, uploading, deleting, or blocked elsewhere.

### 7. Define and test partial failure semantics

**Goal:** Avoid lying in state after failed sync.

**Status: done (2026-06).**

**Tasks:**

- [x] Add fake-target tests for upload failure after some files succeed.
- [x] Add fake-target tests for delete failure.
- [x] Ensure new manifests are not written after failed sync. (Engine throws before building the manifest; both save sites are post-await on the success path; locked by `FailedSyncLeavesPriorManifestOnDiskUnchanged` through the real `StateStore`.)
- [x] Decide whether uploaded temp files need cleanup or whether retry convergence is sufficient. (Decision: `per-file` converges by overwrite — no cleanup needed; `archive` now best-effort removes the orphaned remote tarball on a failed extract, since it is not manifest-owned.)
- [x] Document retry behavior in `docs/state.md` or `docs/transport.md`. (New "Partial failure semantics" section in `docs/state.md`.)

**Acceptance:** failed sync exits clearly and the next run can converge safely. ✓

### 8. Reconcile readiness docs with implementation

**Goal:** Make readiness docs describe current behavior and planned behavior accurately.

**Tasks:**

- [ ] Document current Linux `pgrep` readiness behavior.
- [ ] Document current Windows `Get-Process` readiness behavior.
- [ ] Mark systemd/journal diagnostics as planned until implemented.
- [ ] Add tests for custom `deploy.ready` command.
- [ ] Add tests for readiness timeout and error output.

**Acceptance:** `docs/readiness.md` no longer overclaims, and tests cover claimed behavior.

### 9. Add read-only environment diagnostics / doctor path

**Goal:** Make target environment assumptions explicit without turning `roam` into a provisioner. This is also the agent-facing diagnostic surface ([ADR-0002](adr/0002-agent-first-usability.md)): the read-only `roam diag` log/dump/trace bundle is the same family as `roam doctor` and shares the boundary and `--json` contract.

**Tasks:**

- [ ] Decide whether checks live in `roam doctor <profile>`, preflight, or both.
- [ ] Add read-only target probes for common self-contained .NET failures where they can be made portable.
- [ ] Surface native dependency failures from start/readiness logs with actionable text.
- [ ] Keep mutating dependency installation out of `roam run`; any future external provisioner hook must stay opt-in and explicit.
- [ ] Cross-link diagnostics with `docs/provisioning-boundary.md`.
- [ ] Implement `roam diag` (agent log/dump/trace bundle) and `--json` on read verbs per ADR-0002.

**Acceptance:** users get clear evidence when the target cannot run the app, but `roam` does not silently mutate OS/package-manager state.

### 10. Decide and document ProxyJump support

**Goal:** Remove ambiguity around bastion-host scenarios.

**Tasks:**

- [ ] Inspect SSH.NET support options for jump hosts.
- [ ] Decide whether v0 supports ProxyJump, supports only direct SSH, or shells out for jump scenarios.
- [ ] If supported, add Compose bastion scenario.
- [ ] If unsupported, preflight should fail with a clear message when `ssh -G` reveals proxy configuration.
- [ ] Update `docs/transport.md` and `docs/preflight.md`.

**Acceptance:** users with bastion-based SSH configs get supported behavior or an explicit limitation.

### 11. Confirm source sync ownership policy

**Goal:** Make remote build workspace behavior intentional.

**Tasks:**

- [ ] Re-read `docs/paths.md` and `docs/state.md` source-sync sections.
- [ ] Decide whether `git ls-files` remains the exact source sync scope.
- [ ] Decide whether stale tracked files may be deleted while untracked remote files are preserved.
- [ ] Add tests for generated/untracked files in remote build workspace.
- [ ] Update docs with explicit examples.

**Acceptance:** source sync has the same clarity artifact sync now has.

## P2 — expand platform coverage

### 12. Windows target matrix

**Goal:** Make Windows target support boring.

**Status: partially done (2026-06).** W-to-W and W-to-L are proven E2E on a real
VM; the path-matrix unit tests are still open.

**Tasks:**

- [ ] Add path tests for `C:/deploy`, `C:\deploy`, paths with spaces, and nested stale files.
- [x] Add manual or automated Windows acceptance checklist. (Manual 2x2 proof against a Windows target VM; see `platform-readiness.md`.)
- [ ] Add Windows readiness failure diagnostics using `Get-Process` / `Get-WinEvent` if v0 claims Windows readiness diagnostics.
- [x] Keep the existing live Windows profile as a manual smoke lane until a Windows lab exists.

**Acceptance:** Windows target support is documented as tested, not anecdotal.

### 13. Windows source host decision

**Goal:** Decide whether developers can run `roam` from Windows in v0.

**Status: done (2026-06).** Windows-source is supported and proven E2E.

**Tasks:**

- [x] Audit path assumptions in config loading, state paths, source sync, and launch config emission.
- [x] Run unit tests on Windows if CI is available. (CI runs the suite on `windows-latest`.)
- [x] Either add Windows-source support tests or document Windows source as post-v0. (Proven W-to-L and W-to-W from a Windows controller; recorded in `platform-readiness.md`.)

**Acceptance:** README and preflight docs clearly state Windows-source support level. ✓

### 14. macOS support decision

**Goal:** Avoid accidental claims about macOS.

**Tasks:**

- [ ] Decide whether macOS source/build/target is in v0, v1, or unsupported until demand appears.
- [ ] Add path and RID examples if supported.
- [ ] Add CI or manual acceptance if claimed.

**Acceptance:** macOS appears in docs only at the confidence level we can defend.

### 15. Full-VM acceptance lane

**Goal:** Use real VMs for what containers cannot prove.

**Status: partially done (2026-06).** Real VMs are provisioned and the 2x2 deploy
matrix is proven by hand; the automated nightly lane is blocked on a CI network
decision — the Linux CI runner is network-isolated from the deploy targets.

**Tasks:**

- [x] Provision Linux build and target VMs. (Cloned from hypervisor templates rather than provisioned with Terraform.)
- [ ] Add systemd-managed target scenario. (Depends on the Linux systemd service mode.)
- [ ] Add network-policy scenario where target has restricted egress.
- [ ] Add nightly/on-demand acceptance script. (Blocked: the lane and the deploy key are wired, but the CI runner cannot reach the targets yet.)

**Acceptance:** full-VM acceptance validates service-manager, bootstrapping, and network realism before tagged releases.

## Working rule

When starting a roadmap item:

1. Add or strengthen tests first where practical.
2. Keep changes small enough to checkpoint frequently.
3. Update the relevant design/readiness/platform docs in the same commit as behavior changes.
4. Record new evidence in [`platform-readiness.md`](platform-readiness.md).
