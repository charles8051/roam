# Example roamfiles

These examples are intentionally copy/paste friendly. They are not tests; the executable fixture used by the Compose lab lives at [`../tests/fixtures/SampleApp/roamfile.yaml`](../tests/fixtures/SampleApp/roamfile.yaml).

Pick the closest shape, copy `roamfile.yaml` into your repository root, then change:

- project name
- `solution:` or `csproj:`
- host aliases, SSH names, users, ports, and identity files
- workspace paths
- publish settings (`publish.rid`, `publish.self-contained`, `publish.configuration`) or legacy publish profile names
- launch profile names
- deploy paths and stop/start/ready commands

## Examples

| Directory | Use when |
|-----------|----------|
| [`minimal-local`](minimal-local/roamfile.yaml) | Source, build, and target are the same Linux host. Good first smoke test. |
| [`linux-remote-build-linux-target`](linux-remote-build-linux-target/roamfile.yaml) | You edit on a laptop/source host, publish on a Linux workstation, and deploy to a Linux kiosk/server. |
| [`linux-remote-build-windows-target`](linux-remote-build-windows-target/roamfile.yaml) | You publish on Linux but run the self-contained app on a Windows target over OpenSSH. |
| [`deploy-only`](deploy-only/roamfile.yaml) | Same pipeline, but debugger attach generation is disabled. |

## Validate after copying

Run:

```bash
roam run <profile> --verbose --log-file .roam/last-run.jsonl
```

If the profile should only generate debugger config first:

```bash
roam attach <profile>
```

`roam` is intentionally strict: unknown keys are rejected instead of ignored. If you need to model something not shown here, check [`../docs/configuration.md`](../docs/configuration.md) before inventing a field.
