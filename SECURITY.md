# Security Policy

## Supported versions

Only the latest published version of `Roam.Cli` receives fixes. There are no long-term support branches.

## Reporting a vulnerability

Report privately through GitHub's
[private vulnerability reporting](https://github.com/charles8051/roam/security/advisories/new). Please do
not open a public issue for a security problem.

Include what the issue is, which version, and how to reproduce it. A `roamfile.yaml` that triggers the
behaviour is the most useful thing you can send — redact your hostnames and paths first.

This is a single-maintainer project, so expect an initial response in days rather than hours.

## What roam does, and what that means

roam connects over SSH to machines you name, copies files to them, and runs commands there. Read
[`docs/security.md`](docs/security.md) for the full trust model and
[`docs/provisioning-boundary.md`](docs/provisioning-boundary.md) for the limits it holds itself to.

The short version: **roam trusts your `roamfile.yaml` completely, and trusts the remote host very
little.** A roamfile is executable configuration — its `start:`, `stop:`, `ready:` and `uninstall:`
blocks are shell that roam runs on the target. Treat one from an untrusted source the way you would
treat a shell script from an untrusted source.

## What counts as a vulnerability here

- **Command injection through data.** A hostname, path, profile name, or any other value that roam
  interpolates into a remote command in a way that lets it break out of its quoting. The roamfile's
  shell blocks are intended to be shell; a *path* becoming shell is not.
- **Deleting or overwriting something roam does not own.** The sync engine deletes only files its own
  manifest records. A path that escapes the deploy directory, or that reaches an unmanaged file, is a
  bug of this kind.
- **A malicious or compromised target influencing the controller.** roam parses output from remote
  commands. Anything a hostile server can return that causes a local write outside the expected
  location, or local command execution, is in scope.
- **Credential exposure.** A private key path, key material, or token appearing in logs, in
  `launch.json`, in `.roam/` state, or in an error message.

## What does not

- **Anything the roamfile authorises.** If a profile's `start:` block is destructive, roam running it is
  roam working correctly.
- **Trusting your SSH configuration.** roam resolves hosts through `ssh -G` and honours your
  `~/.ssh/config`, including `ProxyJump`. It does not second-guess what you configured.
- **Access you already have.** roam does not escalate privilege. Running it against a host reaches
  exactly what your SSH credentials reach.
