# Prior art

Tools that live somewhere near `roam` in design space, and why none
of them fit the exact shape of "three-host dev loop for GUI and
edge-device apps." The point of this document is to be honest about
what already exists, so that `roam` either fills a real gap or gets
abandoned in favor of something that does.

## Distributed dev-loop orchestrators

### HashiCorp Waypoint (archived 2024)

The closest prior art by philosophy. Waypoint offered a declarative
"build → deploy → release" pipeline abstraction across arbitrary
targets (Docker, Kubernetes, Nomad, AWS, etc.) with one config file.
The vision was exactly "make any build-anywhere-deploy-anywhere
workflow one tool."

**Why it didn't land:** HashiCorp archived Waypoint in 2024 after it
failed to find an audience. The postmortem analysis (community +
HashiCorp blog posts) points at a few issues:

- It tried to be *both* a dev-loop tool *and* a CI/CD tool, and was
  never the best option at either. Teams with CI/CD already had
  GitHub Actions; teams needing a dev loop already had Tilt.
- It was too abstract — the config didn't obviously save work over
  just writing the shell scripts you'd otherwise write.
- It didn't have a crisp "this is the user who needs this" story.

**Lesson for `roam`:** stay narrow. `roam` is explicitly a *dev
loop* tool, not a CI/CD replacement, and it has a crisp user: "I am
iterating on a GUI or edge app where the build host and the run
host are physically different machines." Waypoint's failure mode is
the warning: do not let scope creep turn this into a
general-purpose deployment tool.

### Tilt

By far the best-designed dev-loop tool in adjacent space. Tilt
provides a `Tiltfile` (Starlark config) that declares services,
builds, deployments, and — critically — *live updates*, which sync
changed files into running containers without a full rebuild.

**Why it doesn't fit here:** Tilt assumes Kubernetes. Everything
from the service graph to the live-update mechanism to the UI
presumes that "deployment" means "pod in a cluster." For targets
that are raw hosts (a laptop, a Jetson, a kiosk), Tilt has no
story. Tilt is worth studying for its UX — the Tiltfile DSL, the
live-update primitive, the web UI — even though its substrate is
wrong for `roam`'s use cases.

### Skaffold / DevSpace / Garden

Same category, same k8s assumption, different philosophies:

- **Skaffold** (Google) — simpler than Tilt, more build-focused,
  same k8s scope.
- **DevSpace** — very Tilt-like, file-sync into pods, strong
  "it feels like local development" pitch.
- **Garden** — heavier, enterprise-flavored, test-graph-aware.

All three are off-target for the same reason as Tilt: pods, not
hosts.

### mirrord

Different axis: instead of deploying code to the remote target,
mirrord runs the code *locally* but intercepts its network traffic,
filesystem, and environment to make it *behave* as if it were
running on the remote. Clever, useful for a specific cluster-debug
use case, and entirely orthogonal to `roam`'s problem — `roam`
needs the code to actually run on the target hardware (for GPU,
display, sensors), not simulate running there.

## Build-side tools

### Bazel remote execution (RBE)

Solves "compile anywhere" at the build-graph level: Bazel sends
actions to a farm of remote workers, caches aggressively, and
composes outputs locally. Massively powerful, massively heavy. A
good fit if you already live in Bazel; comically oversized for
"build my Avalonia app on my workstation instead of my laptop."

`roam` could in principle use Bazel's remote execution as one of
its build-stage backends. Probably never will, because the
operator cost of setting up RBE dwarfs the cost of just running
`dotnet publish` over SSH.

### buildbarn / buck2

Same shape as Bazel RBE, different ecosystem. Same mismatch.

## File-sync tools

### Mutagen

Purpose-built for dev file sync: watches FS events, handles
bidirectional and one-way modes, survives reconnects, maps
permissions across platforms, reports state. `roam` delegates to
Mutagen for `roam watch` (long-lived watch sessions where FS-event
handling and reconnect machinery earn their weight), falling back
to periodic SFTP polling when Mutagen isn't installed. Its daemon
model and status CLI are almost ideal for being wrapped by a
higher-level orchestrator.

### rsync

The eternal baseline. Content-hash-based delta, everywhere, no
daemon, trivially scriptable. Available as an explicit
`--sync=rsync` override in `roam` for Linux/macOS-only pipelines,
but not a default — rsync has no native Windows build, and every
Windows workaround (Cygwin, MSYS2) drags in a second SSH stack
that doesn't share config or agent with the system OpenSSH. `roam`
uses SSH.NET SFTP for its default one-shot sync instead.

### Syncthing

Peer-to-peer eventual-consistency sync. Great as a Dropbox
replacement, wrong mental model for "deploy on save" — it doesn't
surface "the deploy is done, now trigger the next stage" cleanly.

### Unison / lsyncd

Older, still functional, worse UX than Mutagen. Not worth using as
a default in 2026.

## Editor-side remoting

### VSCode Remote-SSH / Remote-Tunnels

The editor side of the problem. Lets you edit files on a remote
host as if they were local. Solves the "where does the editor
run" question cleanly and is probably the right choice for the
source-and-build-are-the-same-remote case.

`roam` is downstream of this: once your editor is pointed at the
right source host, `roam` handles everything from build onward.

### JetBrains Gateway / Remote Development

Same category, JetBrains flavor. Same relationship to `roam`.

### Dev Containers

Another orthogonal axis — standardizes the *toolchain* via a
container. Useful for "every dev has the same build environment,"
orthogonal to "the build runs on a different machine than the
run."

## Local orchestrators

### just / task / make

These are what `roam` replaces in practice. Today, the three-host
dev loop is a pile of `just` recipes with `BUILD_HOST` variables
and hand-written `ssh "$BUILD_HOST" dotnet publish` commands. That
pile works, up to a point — the point being when:

- you have more than one project doing this and want to share the
  pattern;
- you want the editor to know which host a process is on so the
  debugger can attach;
- you want `watch` mode with file-change debouncing that survives
  network hiccups;
- a teammate has to figure out which host is which from reading
  the recipes cold.

Until at least a couple of those pains show up, writing a thick
`justfile` is the *right* answer. `roam` only earns its existence
when the justfile starts fighting back.

## Adjacent but not comparable

- **Ansible / Chef / Puppet** — configuration management, not dev
  loop. `roam` assumes the hosts are already set up.
- **Nomad / Kubernetes / systemd** — process supervisors. `roam`
  tells them "restart this service after deploy" and otherwise
  stays out of their way.
- **scp / sftp** — one-shot file copy, no change detection.
  Fine as an escape hatch, not a daily-driver sync.
- **Warp (warp.dev terminal)** — unrelated product, shares no
  design space, mentioned only because of the name collision risk
  we avoided by not picking the name "warp."

## Summary

No existing tool does the exact thing `roam` proposes: a dev-loop
orchestrator for raw hosts (not pods) where the build machine and
the run machine are deliberately different and a debugger has to
attach across the pipeline. Tilt is closest in philosophy but wrong
on substrate; Waypoint was closest on scope but too broad and now
archived; justfile is what everyone actually uses but doesn't scale
across projects or teammates.

`roam`'s job is to be the smallest possible tool that fills that
specific gap without drifting into any of the adjacent spaces
above.
