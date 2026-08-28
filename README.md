# roam

**Build .NET on any host, run on any host, debug from anywhere.**

`roam` is a dev-loop orchestrator for .NET workflows where the machine that edits
the code, the machine that compiles it, and the machine that runs it are three
different computers — and you want the inner loop to feel local.

You hit this when the app needs a real GUI it can't run headlessly on the
workstation you'd rather compile on, or has to run on a constrained edge device
that can't host the SDK, or when you want your laptop to stop hosting a
five-minute `dotnet publish` every time you save. The usual answer is a pile of
`just` recipes, hand-rolled `rsync`, and an SSH `pipeTransport` block someone got
working once. `roam` is one tool and one config file instead.

## Install

```bash
dotnet tool install -g Roam.Cli
roam --version
```

The package id is `Roam.Cli`; the command it installs is `roam`.

## Quickstart

```bash
cd /path/to/my-dotnet-app
roam init --csproj src/MyApp/MyApp.csproj
```

That writes `roamfile.yaml` and adds `.roam/` to `.gitignore`. A minimal
single-host profile is four lines — the schema version, the project, the local
host, the three host roles, the publish block, and the deploy path are all
derived:

```yaml
profiles:
  dev-local:
    deploy:
      start: ./MyApp
```

Spelling out the parts you care about is additive. The same profile with a
restart command, a readiness probe, and debugger attach:

```yaml
profiles:
  dev-local:
    deploy:
      path: /home/myuser/apps/myapp
      flatten-publish: true
      stop: pkill -f '[M]yApp' || true
      start: nohup /home/myuser/apps/myapp/MyApp >/tmp/myapp.log 2>&1 &
      ready: pgrep -f MyApp >/dev/null
    debug:
      enabled: true
      debugger: vsdbg
      editor: vscode
      process-name: MyApp
```

Every default is listed in
[`docs/configuration.md`](docs/configuration.md#defaults).

```bash
roam run dev-local        # sync source -> publish -> stop -> sync artifacts -> start -> ready
roam attach dev-local     # write a VS Code launch.json entry for the profile
roam diag dev-local       # fetch logs and crash dumps from the target, read-only
roam uninstall dev-local  # run the profile's uninstall block and clear local state
```

Copy-paste profiles for remote builds, Windows targets and deploy-only setups are
in [`examples/`](examples/README.md). Full walkthrough in
[`docs/getting-started.md`](docs/getting-started.md).

## The mental model: source / build / target

Every loop `roam` cares about is three roles:

| Role | What it does | Typical host |
|---|---|---|
| **source** | Where the code lives and is edited | Laptop, or a remote workstation |
| **build** | Where `dotnet publish` runs | Workstation, CI runner, laptop |
| **target** | Where the binary runs, and is debugged | Laptop, kiosk, single-board computer, server |

Any two can be the same machine, or all three can differ. The build host
cross-compiles for the target's RID with `--self-contained`, so the target needs
no SDK — only what the publish output carries.

The roles are declarative, so moving the build from your workstation to your
laptop is a one-line config change rather than a rewritten script.

## Non-goals

`roam` is not a CI/CD system, a polyglot dev-loop tool, a Kubernetes dev tool, a
container orchestrator, a general-purpose file sync, or a package manager. It
targets raw hosts over SSH, for one shape of problem: looping on a .NET desktop
or edge app whose dev machines are not its run machine.

The narrowness is the pitch. Waypoint tried to be any-build-any-deploy for every
stack and was archived without finding an audience.

## Status

Pre-1.0, and in daily use. Working `init`, `run`, `deploy`, `attach`, `uninstall`
and `diag`; SSH.NET/SFTP transport with metadata-diffed sync and manifest-scoped
artifact ownership; `.roam/` state; VS Code `launch.json` emission.

Covered by unit tests, integration smoke tests that need no Docker, an opt-in
Compose E2E lane across separate source/build/target hosts, and a hand-driven
cross-platform matrix. [`docs/platform-readiness.md`](docs/platform-readiness.md)
records what is actually proven per platform combination, and what is not.

## Documentation

Start with [`docs/design.md`](docs/design.md) for the architecture and
[`docs/paths.md`](docs/paths.md) for how paths resolve across three hosts — the
part with the most edge cases.

Reference: [`docs/cli.md`](docs/cli.md) ·
[`docs/configuration.md`](docs/configuration.md) ·
[`docs/roamfile.schema.json`](docs/roamfile.schema.json) ·
[`docs/exit-codes.md`](docs/exit-codes.md) ·
[`docs/state.md`](docs/state.md) ·
[`docs/transport.md`](docs/transport.md) ·
[`docs/security.md`](docs/security.md)

Targeting Windows brings its own hazards; see
[`docs/powershell-5.1-over-ssh.md`](docs/powershell-5.1-over-ssh.md) before
writing a `start:` block for one. Decision records are in
[`docs/adr/`](docs/adr/).

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Security issues have their own path —
see [SECURITY.md](SECURITY.md).

## Licence

[MIT](LICENSE) © Charles Lee
