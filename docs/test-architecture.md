# Test architecture

**Status:** proposed and intentionally concrete. This document turns the
design docs into an automated verification plan for `roam` itself.

The goal is not "have some tests." The goal is to prove, repeatedly and
without manual SSH spelunking, that `roam` can execute its v0 contract:

- load and validate `roamfile.yaml`,
- resolve source / build / target hosts,
- sync source to build,
- run `dotnet publish` on build,
- stop the target process,
- sync artifacts to target with delete semantics,
- start the target process,
- verify readiness,
- emit deterministic VSCode attach config.

The repo is still early enough that the test architecture should shape
the implementation boundaries rather than be retrofitted afterward.

## 1. Testing principles

The testing strategy for `roam` follows these rules:

1. **Automate the real topology.** The core product claim is a
   three-host pipeline over SSH. The automated tests must exercise that
   shape directly, not just mocks around helper methods.
2. **Prefer code-as-infrastructure.** The test environment itself is a
   versioned artifact in the repo: Docker Compose for the fast tier,
   Infrastructure-as-code plus cloud-init for the realistic tier.
3. **Keep the fast loop fast.** Pull-request validation should run on a
   laptop or CI runner in minutes. Full-VM acceptance belongs in a
   slower lane.
4. **Test at subsystem boundaries.** The implementation contract already
   names the subsystems. Each one should have direct tests and should be
   usable in end-to-end runs without giant command-handler classes.
5. **Bias toward deterministic fixtures.** The test suite should own the
   source tree, SSH keys, host config, and remote process behavior. Do
   not depend on a developer's existing machines for routine tests.
6. **Treat failures as product output.** A failing readiness check,
   missing toolchain, or bad deploy path is not just a test failure; it
   is a user-facing behavior that should be asserted explicitly.

## 2. Test pyramid

`roam` should use four layers of automated verification.

### Layer A: unit tests

Purpose: prove pure logic without SSH or filesystems outside a temp
directory.

Targets:

- YAML parsing and schema validation
- profile and host reference resolution
- role-coincidence collapse
- pipeline planning and step ordering
- command construction and escaping rules
- path mapping between source, build, and target
- launch.json emission and idempotent merge behavior
- state-store manifest bookkeeping

These tests should be cheap, numerous, and run on every `dotnet test`.

### Layer B: subsystem integration tests

Purpose: prove each major subsystem against a realistic dependency but
without the whole pipeline.

Targets:

- host resolution using real `ssh -G` output fixtures
- sync engine against temp directories or an in-process SFTP target
- transport error handling for auth failure, timeout, and unreachable host
- readiness polling behavior with fake process and command adapters
- debugger emitter against real `.vscode/launch.json` files in temp repos

These tests should still run in-process and avoid a full Compose lab
unless the subsystem genuinely depends on SSH behavior.

### Layer C: containerized end-to-end tests

Purpose: prove the v0 story in a reproducible, PR-friendly lab that
models multiple hosts reachable over SSH.

This is the primary automated confidence layer for v0.

Targets:

- `roam init`
- `roam run <profile>`
- `roam attach <profile>`
- source/build/target permutations
- source sync, publish, stop, deploy, start, ready, attach emission
- failure reporting with real remote commands and filesystems
- bastion and `ProxyJump` scenarios

### Layer D: full-VM acceptance tests

Purpose: prove the claims that containers cannot model honestly.

Targets:

- `systemd`-managed stop/start/ready behavior
- `journalctl` readiness diagnostics
- airgapped or restricted-egress targets
- more realistic host bootstrapping and SSH trust setup
- future Windows-target or GUI-adjacent scenarios

This tier should run nightly, on demand, or before tagged releases. It
should not block normal inner-loop development.

## 3. Recommendation: two infrastructure tiers

The test environment should be split into two code-defined labs.

### Tier 1: Docker Compose lab

This is the default environment for end-to-end automation.

Why Compose is the right first move:

- `roam` speaks SSH, SFTP, and remote commands; containers can model all
  of that accurately enough for v0.
- It gives fast, cheap, disposable host topologies.
- It lets the repo check in a complete test network with no dependency
  on personal machines.
- It is enough to verify the three-role mental model and most of the
  transport design.

Limits of Compose:

- It does not represent real `systemd` behavior well.
- It does not prove VM boot/init behavior.
- It is a poor substitute for future Windows-target coverage.
- It cannot fully model "target has no network access to Microsoft's
  debugger bootstrap endpoints" without extra network controls.

Conclusion: Compose should be the default PR gate, not the only gate.

### Tier 2: full-VM acceptance

This is the realism tier.

Why it exists:

- The docs explicitly lean on service-managed targets and journal-based
  diagnostics for failed starts.
- Some of the design value comes from behavior on real hosts, not just
  process containers.
- A full-VM lab can represent network policy, init systems,
  storage layout, and SSH trust more faithfully.

Why it should not come first:

- The repo does not yet have a stable CLI or implementation surface.
- VM provisioning is slower and operationally heavier.
- Running it on every PR would make the project expensive to change at
  the exact stage where the design is still settling.

Conclusion: build this tier after the Compose harness exists and the
first end-to-end path is working.

## 4. Compose lab design

The repository contains a runnable lab under `tests/labs/compose` with these services:

### Required hosts

- `source`: the authoritative workspace and the place from which tests
  invoke `roam`
- `build`: SSH host with the .NET SDK installed
- `target`: SSH host without the SDK, used to prove self-contained deploy
- `bastion`: optional SSH jump host for `ProxyJump` scenarios

### Current runner

Run the lab directly with:

```bash
./tests/labs/compose/run-lab.sh
```

Or through the opt-in xUnit integration lane with:

```bash
ROAM_RUN_COMPOSE_LAB=1 dotnet test tests/Roam.IntegrationTests/Roam.IntegrationTests.csproj --filter ComposeLabRunnerPassesWhenExplicitlyEnabled
```

The xUnit test is a no-op unless `ROAM_RUN_COMPOSE_LAB=1` is set, so normal integration runs do not require Docker.

The current runner builds the host `Roam` binary, starts the Compose network, runs the `kiosk` profile cold and warm, verifies target process/readiness state, verifies manifest files, verifies nested remote publish output and mtime preservation through the SFTP relay, verifies relay temp directories are cleaned, verifies a manifest-owned stale target file is deleted, verifies an unmanaged target sentinel survives redeploy, and tears the lab down. The verified run was executed from a synced checkout on a Docker-capable host.

### Host behavior

- Every host runs `sshd` with known test keys and deterministic users.
- `source` contains the fixture repo and test SSH config.
- `build` contains a workspace root that can be overwritten by source sync.
- `target` contains only the minimal runtime dependencies needed to run
  the published fixture app.
- `bastion` can reach `target`; `source` reaches `target` directly in
  some scenarios and only via bastion in others.

### Network shape

The Compose network should allow these scenarios:

1. direct `source -> build` and `source -> target`
2. direct `source -> build` plus `source -> bastion -> target`
3. optional isolation where `build` cannot talk to `target`, matching
   the default source-relay artifact design

### Remote file layout

The lab should use fixed workspace and deploy paths so assertions are
stable across runs:

- source workspace: `/work/source/repo`
- build workspace: `/work/build/repo`
- target deploy root: `/opt/roam-fixture`

These paths should mirror the relative-layout assumptions described in
`docs/paths.md`.

## 5. Fixture applications

Do not make the first automated harness depend on the real motivating
Avalonia project. The harness needs a tiny, deterministic fixture app
purpose-built for orchestration tests.

The repo should eventually carry at least one .NET fixture app with
runtime modes controlled by environment variables or arguments.

Required modes:

- `healthy`: process starts and stays alive
- `crash-on-start`: process exits non-zero immediately with clear stderr
- `delayed-start`: process takes a few seconds before becoming ready
- `stale-output-check`: emits a predictable file set so delete semantics
  can be asserted across deploys

For readiness tests, the fixture should support both:

- process-name based readiness
- explicit custom `ready` command

If service-manager tests are needed before the VM tier exists, a simple
supervisor wrapper script can approximate stop/start semantics inside
containers. That is a stopgap, not the final systemd test story.

## 6. End-to-end scenarios to automate first

The first wave of containerized end-to-end tests should cover the v0
contract directly.

### Core happy paths

1. `source != build != target`
2. `source == build`
3. `source == target`
4. `build == target`

Assertions:

- expected steps run or collapse correctly
- source files appear on build
- `dotnet publish` runs on build, not source
- artifacts appear on target
- target process starts
- readiness reports success

### Preflight failures

1. missing profile
2. unresolved host
3. SSH auth failure
4. build host missing `dotnet`
5. invalid or unwritable target deploy path
6. missing publish profile
7. missing launch profile for `attach`

Assertions:

- failure happens before destructive work
- output identifies the failing host and step

### Sync correctness

1. git-tracked files sync to build
2. untracked files are excluded by default
3. deleted source files are removed on build
4. stale published files are removed on target
5. role-coincidence cases do not perform identity syncs

### Readiness behavior

1. default process-name polling succeeds
2. default polling times out cleanly
3. explicit `ready` command succeeds
4. explicit `ready` command times out cleanly
5. failed starts surface diagnostic output available in that environment

### Attach emission

1. `roam attach` emits deterministic `launch.json`
2. generated entries are namespaced
3. non-`roam` entries are preserved
4. re-running `attach` is idempotent

### SSH topology

1. explicit host fields without `ssh -G`
2. host aliases resolved through `ssh -G`
3. `ProxyJump` path through bastion

## 7. Full-VM acceptance scenarios

The full-VM tier should start narrow and justify itself with the cases
containers miss.

### Initial VM topology

- one source VM
- one build VM
- one target VM
- one optional bastion VM

All should be provisioned from infrastructure-as-code plus cloud-init so the entire
lab is reproducible.

### First acceptance cases

1. target managed by `systemd --user`
2. readiness failure surfaces `journalctl` output
3. target egress restricted to simulate debugger bootstrap limits
4. SSH through bastion with real host keys and known_hosts handling

### Later acceptance cases

1. ARM target host
2. slower network or induced packet loss
3. future Windows target coverage

Do not expand this tier casually. Every added VM scenario should cover a
product claim that Compose cannot verify with enough honesty.

## 8. Test harness structure in the repo

The exact layout can move, but the responsibilities should be explicit.

Proposed shape:

```text
tests/
  Roam.UnitTests/
  Roam.IntegrationTests/
  fixtures/
    SampleApp/
  labs/
    compose/
      docker-compose.yml
      ssh_config
      host-keys/
      scripts/
```

### Test runner responsibilities

- `Roam.UnitTests`: pure logic and temp-directory tests
- `Roam.IntegrationTests`: orchestrates Compose lab lifecycle, invokes
  the built `roam` CLI, and asserts on remote outcomes
- `tests/fixtures/SampleApp`: deterministic publish/run target
- `tests/labs/compose`: multi-host container topology

The integration tests should own lab startup and teardown so a single
test command can provision the environment, run assertions, and clean up.

## 9. CI and execution lanes

The automation should be split by cost.

### Pull requests

Run:

- build
- unit tests
- subsystem integration tests
- selected Compose end-to-end tests

Time budget target: low single-digit minutes once the image layers are warm.

### Main branch or nightly

Run:

- full Compose suite
- full-VM acceptance suite

### Release validation

Run:

- full Compose suite
- the full VM suite
- packaging/install smoke tests for the `dotnet tool`

The gating rule should be simple: PRs prove correctness cheaply; nightly
and release lanes prove realism.

## 10. Design constraints this imposes on the implementation

The test architecture is only practical if the product code stays
modular. Concretely:

1. the CLI layer should be thin and call injectable services
2. transport should be abstracted behind an interface or boundary that
   can be exercised with real SSH or a test double
3. sync planning should be separable from sync execution
4. readiness logic should be testable without forking the whole CLI
5. debugger emission should be independent of deploy execution
6. host resolution should accept raw `ssh -G` output in tests

If the implementation collapses into one large command handler, the
tests will either be fragile or too slow. The subsystem boundaries in
`docs/implementation-contract.md` are therefore also testability
boundaries.

## 11. Rollout sequence

The project should build the test system in this order.

### Phase 1

- create the unit-test project
- create the fixture app
- implement pure tests around config, pipeline planning, and emitter logic

### Phase 2

- create the Compose lab
- add one `source != build != target` end-to-end happy-path test
- add one failing readiness test
- add one `attach` determinism test

### Phase 3

- fill out role-collision and preflight-failure coverage
- add bastion and `ssh -G` scenarios
- make Compose part of the normal CI gate

### Phase 4

- provision the VM lab with infrastructure-as-code and cloud-init
- add a small number of `systemd` and restricted-network acceptance tests
- run that suite nightly or before releases

This order optimizes for feedback speed and prevents the infrastructure
story from outrunning the product.

## 12. Final recommendation

Build the Compose tier first and treat it as the main automated proof of
v0. Add the full-VM tier as a realism layer once the first end-to-end
path exists.

That gives `roam` three things the project needs immediately:

- a fast, repeatable way to validate the three-host contract,
- an infrastructure-as-code test lab that lives with the repo,
- a clear path to stronger acceptance coverage without dragging VM
  complexity into every PR.

The mistake to avoid is choosing between containers and VMs as if only
one can exist. `roam` needs both; they simply belong at different speeds
and different levels of confidence.