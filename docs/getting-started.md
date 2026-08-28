# Getting started

This is the shortest path from "I have a .NET app" to a working `roam run`.

`roam` assumes three roles:

- `source`: where the repository is edited
- `build`: where `dotnet publish` runs
- `target`: where the published app is deployed and started

Those roles may all be the same host, or three different hosts.

## 1. Prerequisites

On the machine where you run `roam`:

```bash
dotnet --version
ssh -V
```

For a remote build host:

- SSH login works without an interactive password prompt.
- The build host has the .NET SDK required by the project.
- The build host has `git` if the source sync path needs git-tracked file enumeration.

For a target host:

- SSH login works without an interactive password prompt.
- The deploy user can write the configured deploy path.
- The target can run the publish output. For self-contained .NET apps this usually means native OS dependencies only; GUI apps may still need X11/Wayland, fonts, GPU, ICU, etc.

`roam` diagnoses these assumptions; it does not install target dependencies or converge OS state. Keep that in Terraform/cloud-init/Ansible/Packer or the project README. See [`provisioning-boundary.md`](provisioning-boundary.md).

## 2. Install

```bash
dotnet tool install -g Roam.Cli
roam --version
roam --help
```

For an unpacked local `.nupkg` during dogfood:

```bash
dotnet tool install -g Roam.Cli --add-source /path/to/packages --version <version>
```

The package id is `Roam`; the command is `roam`.

If your SDK is installed in a nonstandard location, the generated apphost may need `DOTNET_ROOT` set. Example from this lab host:

```bash
export DOTNET_ROOT=/root/.dotnet
```

## 3. Make sure the .NET project has a launch profile and decide publish settings

`roam` uses a launch profile from `launchSettings.json` when the project has one,
picking the first profile unless `launch-profile:` names another. A project
without `launchSettings.json` runs with no launch profile. For publish settings,
`roam` has two modes:

1. preferred: declare a small `publish:` block in `roamfile.yaml`, or omit it
   and let `roam` synthesize one
2. legacy/compatibility: reference a `.pubxml` via `publish-profile:`

Recommended minimum project files:

```text
MyApp.csproj
Properties/
  launchSettings.json
```

Example launch profile:

```json
{
  "profiles": {
    "Development": {
      "commandName": "Project",
      "environmentVariables": {
        "DOTNET_ENVIRONMENT": "Development"
      }
    }
  }
}
```

## 4. Scaffold a roamfile

From the repository root:

```bash
roam init --csproj src/MyApp/MyApp.csproj
```

This writes:

- `roamfile.yaml`
- `.gitignore` entry for `.roam/`

Then edit `roamfile.yaml` for your real hosts. For copy/paste starting points, see [`../examples`](../examples/README.md).

## 5. Minimal local-first roamfile

When source, build, and target are the same machine, roam derives all of them.
This is a complete roamfile:

```yaml
profiles:
  dev-local:
    deploy:
      start: ./MyApp
```

`roam` fills in the schema version, the csproj (the single one in the repo), a
`local` host pointing at this machine, the three host roles, a `publish:` block
for this machine's RID, the launch profile, and a deploy path under the
workspace. See [`configuration.md`](configuration.md#defaults) for the full list.

The same profile written out, once you want control over where it lands and how
it restarts:

```yaml
csproj: src/MyApp/MyApp.csproj

profiles:
  dev-local:
    description: Build and run on this machine.
    publish:
      rid: linux-x64
      self-contained: true
      configuration: Release
    launch-profile: Development
    deploy:
      path: /home/myuser/apps/myapp
      flatten-publish: true
      stop: pkill -f '[M]yApp' || true
      start: nohup /home/myuser/apps/myapp/MyApp >/tmp/myapp.log 2>&1 &
      ready: pgrep -f MyApp >/dev/null
      ready-timeout: 20
    debug:
      enabled: true
      debugger: vsdbg
      editor: vscode
      process-name: MyApp
```

## 6. Run a profile

```bash
roam run dev-local
```

Expected shape:

```text
[1/6] sync-source     source → build
[2/6] publish         build
[3/6] stop            target
[4/6] sync-artifacts  build → target
[5/6] start           target
[✓]   ready           target
Done.
```

For more detail:

```bash
roam run dev-local --verbose --log-file .roam/last-run.jsonl
```

## 7. Emit VSCode attach config

```bash
roam attach dev-local
```

This writes or updates `.vscode/launch.json` with a generated `roam: dev-local` attach entry. The editor/debugger still owns debugger installation; `roam` only emits the attach config and deploys the app.

## 8. Common first failures

### `roamfile.yaml not found`

Run from the repo root, or pass:

```bash
roam -f /path/to/roamfile.yaml run dev-local
```

### SSH succeeds in your terminal but SFTP auth fails in `roam`

`roam` uses SSH.NET for SFTP. It resolves candidate keys from explicit `identity-file`, `ssh -G identityfile`, and common default keys. Recent versions print the host/user/port and per-key status. Fix the key path or use an unencrypted key supported by SSH.NET for now.

### Publish configuration not found or invalid

Preferred mode:

```yaml
publish:
  rid: linux-x64
  self-contained: true
  configuration: Release
```

Legacy mode:

```text
Properties/PublishProfiles/<Name>.pubxml
```

with:

```yaml
publish-profile: <Name>
```

Use the `publish:` block when the project has no `.pubxml` yet or when you want
Roam to own the RID/self-contained/configuration shape directly.

### Target starts but readiness fails

Start with an explicit readiness command that is true only when the app is usable:

```yaml
deploy:
  ready: test -f /tmp/myapp-ready
  ready-timeout: 30
```

Then make the app write that file at startup, or use a process/http/systemd check that matches your app.

## 9. Where to go next

- [`configuration.md`](configuration.md): full `roamfile.yaml` model
- [`cli.md`](cli.md): command and flag contract
- [`paths.md`](paths.md): sync scope, deploy roots, manifests, delete semantics
- [`transport.md`](transport.md): SSH/SFTP behavior and auth diagnostics
- [`debugger.md`](debugger.md): VSCode attach and debugger constraints
- [`platform-readiness.md`](platform-readiness.md): what has actually been verified
