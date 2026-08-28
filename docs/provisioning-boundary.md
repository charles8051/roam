# Provisioning boundary

**Status:** explicit product-boundary decision. `roam` should validate that a
profile can run on the named hosts and should surface actionable failures, but
it should not become Terraform, Ansible, cloud-init, Chocolatey, Homebrew, apt,
or a general host state manager.

## Decision

For v0, `roam` does **not** install target OS dependencies or converge machine
state.

`roam` owns:

- declaring which host is source/build/target,
- syncing source and publish artifacts,
- running `dotnet publish` on the build host,
- running explicit stop/start/ready commands on the target,
- checking enough preflight/readiness state to fail early and explain what the
  operator should fix.

`roam` does not own:

- package-manager state (`apt`, `dnf`, `brew`, `winget`, Chocolatey),
- OS roles/features, drivers, fonts, GPU libraries, GUI stacks, ICU packages,
- systemd unit installation, Windows service registration, firewall policy,
- secrets, certificates, users, groups, sudoers, or SSH key distribution,
- Terraform/Ansible/Packer/cloud-init orchestration.

The operator should provision those with the normal infrastructure tool for the
environment, then use `roam` for the .NET inner loop.

## Why not add Ansible-style hooks?

It is tempting because the target can fail on native dependencies even when the
publish output is self-contained. The acceptance lab already hit this with ICU:
the app binary was valid, but the target OS lacked a native package.

But an automatic `install if missing` hook changes the tool class:

- it expands from a dev-loop deploy tool into a privileged host mutation tool,
- it requires distro/package-manager detection and version policy,
- it creates hard questions around sudo, secrets, prompts, rollback, idempotency,
  and auditability,
- it makes `roam run` less predictable: a deploy may suddenly mutate base OS
  state instead of just replacing the app under the deploy root,
- it overlaps directly with tools that are much better at converging host state.

The current narrowness is valuable: `roam run` should be boring and repeatable.

## Where dependency checks do belong

There is still a place for environment assumptions in `roam`: diagnostics.

Good v0/v1 shape:

```yaml
profiles:
  kiosk:
    deploy:
      path: /opt/kiosk-ui
      start: systemctl --user start kiosk-ui
      ready: systemctl --user is-active --quiet kiosk-ui
```

`roam` can preflight and fail with messages like:

- build host has no compatible .NET SDK,
- target deploy path is not writable,
- target command interpreter is missing or unsupported,
- target start command exited nonzero,
- target readiness command timed out,
- target process crashed and stderr/journal evidence points at missing ICU or GUI
  libraries.

A future `roam doctor <profile>` could be read-only or mostly read-only and run
operator-authored probes:

```yaml
profiles:
  kiosk:
    checks:
      - name: icu-present
        target: target
        command: ldconfig -p | grep -q libicu
      - name: display-present
        target: target
        command: test -n "$DISPLAY" || test -n "$WAYLAND_DISPLAY"
```

That is materially different from provisioning: the check reports drift; it does
not mutate the host.

## If hooks ever exist

If real users need a bridge, prefer an explicit external hook that calls a real
provisioner, not a built-in package-management DSL.

Possible post-v0 shape:

```yaml
profiles:
  kiosk:
    provision:
      mode: external
      check: ansible-playbook --check infra/kiosk.yml --limit kiosk-01
      apply: ansible-playbook infra/kiosk.yml --limit kiosk-01
      auto-apply: false
```

Guardrails if this ever lands:

1. It must be opt-in and visually distinct from `roam run`'s normal deploy
   pipeline.
2. Default is check-only. Mutating `apply` requires an explicit command such as
   `roam provision <profile>` or a scary flag like `roam run --provision`.
3. `roam` passes profile/host context to the external tool, but does not parse or
   manage package state itself.
4. No implicit sudo prompts, password prompts, or secret capture.
5. Provisioning logs and exit codes stay separate from sync/deploy/readiness
   failures.

## Practical recommendation

Keep target environment state in repo-adjacent infrastructure:

- Terraform/OpenTofu for machines, networks, volumes, DNS, and high-level
  resources,
- cloud-init/Packer/images for base packages and users,
- Ansible/Chef/Puppet/Salt for OS package/service convergence where needed,
- `roamfile.yaml` for the dev-loop mapping and commands once hosts already meet
  that contract.

For each `roamfile.yaml` profile, document the expected target contract next to
infrastructure code or in the app README. Example:

```text
Target contract for profile kiosk:
- OpenSSH reachable as user kiosk
- /opt/kiosk-ui writable by kiosk
- systemd --user enabled for kiosk
- native packages: libicu, fontconfig, libx11, libxcb, libxkbcommon
- GPU/display stack configured before roam runs
```

That keeps `roam` honest: it can prove and explain the inner loop, while the
host lifecycle remains owned by tools designed for host lifecycle.
