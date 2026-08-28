# Contributing

Thanks for your interest. This is a small tool maintained by one person, so please open an issue before
starting anything substantial — it saves you from building something that does not fit.

## Building

```
dotnet build Roam.slnx
dotnet test Roam.slnx --filter "Category!=ComposeLab"
```

The .NET 10 SDK or later. No hardware and no remote hosts are needed: the default suite runs entirely
in-process.

`Category!=ComposeLab` is the filter CI uses. Without it the Compose lab tests try to stand up a
docker-compose network over SSH, which needs Docker and is not what you want on a first run.

## Running the Compose lab

The one integration lane that needs real machinery. It builds `roam`, brings up separate `source`,
`build` and `target` SSH containers, and runs a real deploy through them:

```
ROAM_RUN_COMPOSE_LAB=1 dotnet test tests/Roam.IntegrationTests/Roam.IntegrationTests.csproj \
  --filter ComposeLabRunnerPassesWhenExplicitlyEnabled
```

Needs Docker. It is opt-in precisely because it is slow and environment-dependent.

`tests/labs/xplat/` holds roamfiles for the cross-platform matrix — Windows and Linux controllers
against Windows and Linux targets. Those are driven by hand against two real machines, not by CI. The
addresses in them are placeholders; substitute your own.

## What fits

roam orchestrates a dev loop across three machines: the one that edits, the one that compiles, and the
one that runs. It is deliberately **not** a provisioning tool — see
[`docs/provisioning-boundary.md`](docs/provisioning-boundary.md). A change that has roam install
packages, manage services it did not create, or mutate host state beyond its own deploy directory is
unlikely to be accepted.

Read [`docs/design.md`](docs/design.md) and [`docs/paths.md`](docs/paths.md) before proposing anything
that touches path resolution — the source/build/target split has more edge cases than it looks.

## Tests

New behaviour needs a test. The suite is xUnit.

Prefer a unit test against the pure layer over an integration test. Most of roam is command *building* —
`PublishCommandBuilder`, `BuildStartCommand`, the sync engine's diff — which is a pure function from
config to a string or a plan, and can be asserted exactly without a network.

Windows targets are where the surprises live. If you are touching that path, read
[`docs/powershell-5.1-over-ssh.md`](docs/powershell-5.1-over-ssh.md) first; it records four footguns
that only appear when PowerShell 5.1 is launched over OpenSSH.

## Architecture decisions

Non-trivial design changes get an ADR in [`docs/adr/`](docs/adr/). Follow the existing format and set an
explicit status. If a change supersedes an earlier record, say so in both files.

## Pull requests

- One logical change per PR.
- Match the surrounding code style; there is no formatter config.
- `dotnet test Roam.slnx --filter "Category!=ComposeLab"` must pass. CI runs the same thing.
- Call out any breaking change to `roamfile.yaml` explicitly — it is a published schema
  ([`docs/roamfile.schema.json`](docs/roamfile.schema.json)).

## Releasing

Releases are cut by pushing a `v*` tag. `MinVer` derives the package version from that tag, so an
untagged build produces a `0.0.0`-shaped version rather than a release one.

`.github/workflows/publish.yml` builds, tests, packs and pushes `Roam.Cli` to nuget.org. It
authenticates with [trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing) —
GitHub issues a short-lived OIDC token and nuget.org exchanges it for a key valid for one hour. No
long-lived key is stored in this repository.

Only the maintainer can cut a release. The job runs in the `nuget.org` environment, and the policy is
bound to this repository and to the filename `publish.yml`, so renaming that file stops publishing until
the policy is updated.

## Licence

By contributing you agree that your contributions are licensed under the [MIT Licence](LICENSE).
