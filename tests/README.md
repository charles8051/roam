# Tests scaffold

This directory is the initial scaffold for the automated verification
plan in [`docs/test-architecture.md`](../docs/test-architecture.md).

It currently contains:

- `Roam.UnitTests/` for pure logic tests
- `Roam.IntegrationTests/` for end-to-end and lab-backed tests
- `fixtures/SampleApp/` as the deterministic .NET fixture project
- `labs/compose/` as the fast multi-host draft lab

The initial projects are intentionally light. They establish the repo
shape so implementation work can land against stable test boundaries.

## Current handoff state

As of commit `3c84b60`, the Compose lab scaffold has been brought up and
manually verified on a Linux Docker host:

- all four lab containers start successfully,
- SSH works from `source` to `build`, `target`, and `target` via the
	bastion host,
- `source` and `build` have the .NET 10 SDK,
- `target` does not have the SDK,
- the source fixture repo is copied into `/work/source/repo`,
- the shared `lab-state` volume bootstraps the SSH key used by the lab.

This means the infrastructure scaffold is past the "containers start"
phase and is ready for the first real integration test.

## Next step

The next agent should implement the first end-to-end integration test in
`Roam.IntegrationTests/` that:

1. brings the Compose lab up,
2. verifies the lab is healthy,
3. exercises one `source != build != target` happy-path scenario,
4. asserts on remote filesystem and process state.