# Security and trust model

**Status:** load-bearing guidance. `roam` connects to remote hosts,
executes commands, and writes generated artifacts. The project should be
explicit about what it trusts and what it refuses to own.

## Trust boundary

`roam` assumes:

- the operator already trusts the hosts named in `roamfile.yaml`,
- SSH authentication is managed outside `roam`,
- any secrets needed by remote commands are already available through
  the operator's normal SSH and shell setup.

`roam` does **not** become a secret store, certificate authority, or
credential broker.

## Host identity and SSH

- Host-key verification belongs to the underlying SSH configuration and
  library behavior; `roam` should not disable it.
- `ssh -G` is used only to resolve connection settings. It is not a
  license to silently bypass the user's SSH trust policy.
- If `ssh -G` is unavailable, the fallback is explicit host config in
  `roamfile.yaml`, not weaker verification.

## Remote command boundaries

`roam` should treat remote command execution as a high-risk boundary:

- profile data must not be interpolated into ad-hoc shell fragments
  without quoting/escaping rules,
- generated commands should be assembled from validated fields,
- destructive steps should happen only after preflight succeeds,
- error output from remote commands should be surfaced clearly.

v0 should keep the command surface deliberately small: publish, stop,
start, and ready. More hooks mean more quoting and trust complexity. See
[`provisioning-boundary.md`](provisioning-boundary.md) for the explicit
boundary between diagnostics and host state management.

## Source and artifact sync

- Source sync is one-way by default; this avoids the ambiguity and
  conflict surface of bidirectional sync.
- Delete semantics are allowed only in directories that `roam` is
  responsible for.
- Generated artifacts and state should stay namespaced under `.roam/`
  or in clearly owned output paths.

## Generated editor files

- Generated debugger configs should be deterministic and namespaced.
- `roam` should modify only the entries it owns.
- The emitter should remain downstream of deployment logic; a debugger
  misconfiguration must not corrupt deploy state.

## Secrets

`roam` should never:

- store SSH private keys,
- prompt for or cache deployment secrets,
- write secrets into generated config files,
- invent a parallel secret-distribution system.

If a workflow requires secrets, the expectation is that the operator
solves that with SSH agents, environment management, or external secret
tools before `roam` is invoked.

## Future features with extra caution

The following features materially change the trust model and should stay
out of v0:

- agent-forwarded artifact transfer,
- direct build→target mesh transfers,
- arbitrary pre/post hooks,
- debugger installation flows that move binaries around on the user's
  behalf.

They may be worth adding later, but each deserves explicit review rather
than slipping in as a convenience feature.
