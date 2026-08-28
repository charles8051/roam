# Compose lab

This directory contains the fast multi-host lab described in
[`docs/test-architecture.md`](../../../docs/test-architecture.md).

The lab models four SSH-reachable hosts:

- `source`: runs `roam` and owns the fixture workspace
- `build`: Linux build host with the .NET SDK
- `target`: Linux deploy target without the .NET SDK
- `bastion`: optional SSH jump host for later ProxyJump coverage

## What this proves today

`run-lab.sh` exercises the core Linux source/build/target product shape:

1. build the local Roam CLI in Release mode on the host running the script,
2. start the Compose topology,
3. verify source-to-build, source-to-target, and source-to-bastion SSH reachability,
4. verify the build host has the .NET SDK and the target does not,
5. run `roam run kiosk` from the source container,
6. assert the build workspace receives the source tree,
7. assert the target receives self-contained publish artifacts,
8. assert the target process starts and readiness passes,
9. run a warm deploy,
10. assert nested publish output survives the remote-build SFTP materialization relay,
11. assert the target mtime for that nested file matches the build host's publish mtime,
12. assert `/tmp/roam-publish-*` relay materialization directories are cleaned,
13. seed a manifest-owned stale file plus an unmanaged sentinel under the target deploy root,
14. run another deploy,
15. assert the stale manifest-owned file is deleted,
16. assert the unmanaged sentinel survives.

This is intentionally the first automated confidence lane. It does not yet prove
ProxyJump support, systemd integration, Windows targets, or full-VM behavior.

## Run

From the repository root, run the script directly:

```bash
tests/labs/compose/run-lab.sh
```

Or run it through the opt-in xUnit integration lane:

```bash
ROAM_RUN_COMPOSE_LAB=1 dotnet test tests/Roam.IntegrationTests/Roam.IntegrationTests.csproj --filter ComposeLabRunnerPassesWhenExplicitlyEnabled
```

Without `ROAM_RUN_COMPOSE_LAB=1`, the xUnit test records a no-op pass so normal
unit/integration runs do not require Docker.

By default the script tears the lab down after completion. To keep containers
running for debugging:

```bash
ROAM_KEEP_COMPOSE_LAB=1 tests/labs/compose/run-lab.sh
```

To run a different profile from the fixture roamfile:

```bash
ROAM_LAB_PROFILE=kiosk tests/labs/compose/run-lab.sh
```

## Requirements

The host running the script needs:

- Docker with the Compose plugin (`docker compose`)
- .NET SDK 10.0 to build `src/Roam/Roam.csproj`
- network access to pull the Microsoft .NET container images

This lab needs a host with Docker available; run it anywhere the repo can be
synced and Docker is installed.

## Intended paths inside the lab

- source workspace: `/work/source/repo`
- mounted Roam repo: `/work/roam`
- build workspace: `/work/build/repo`
- target deploy root: `/opt/roam-fixture`

## SSH bootstrap

All services share a `lab-state` volume. On first boot, the common entrypoint
generates a source keypair under `/lab-state/source/`. The public key is copied
into each host's `authorized_keys`, and the source host copies the private key
into `/home/roam/.ssh/id_ed25519`.

This keeps the lab self-contained without committing test private keys to the
repository.

## Current gaps

- ProxyJump is checked for raw SSH reachability only; Roam's SSH.NET transport
  does not yet support jump hosts.
- Sync stats are not exposed yet, so warm deploy assertions are behavioral rather
  than count-based.
