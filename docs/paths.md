# Paths, workspaces, and repo layout across hosts

**Status:** exploratory. This document answers one specific
question: if the same project repo is already cloned on multiple
hosts, how does `roam` arrange things so that build artifacts land
in the same *relative* place on every host — so the IDE, the
debugger, and the developer's muscle memory all keep working as if
the whole pipeline ran on one machine?

## The goal

When a developer opens an Avalonia project on their laptop and
runs `dotnet build`, they know where the output goes without
thinking:

```
~/src/kiosk-ui/
  src/KioskUi/
    KioskUi.csproj
    Program.cs
    ...
    bin/Release/net8.0/linux-arm64/publish/
      KioskUi.dll
      KioskUi              ← the executable
      ...
```

The IDE knows where to find symbols. The debugger knows where to
find source. `launch.json` has relative paths that work. `dotnet
run`, `dotnet test`, `dotnet ef`, every other tool — all of them
expect this layout and none of them need configuration to find it.

When we move the build to a different host, we want *none of that
knowledge to become wrong*. The artifact should still be at
`src/KioskUi/bin/Release/net8.0/linux-arm64/publish/KioskUi.dll`
from the project root. The IDE should still find its symbols
without a remapping step. The developer should still be able to
type the same paths from memory.

This document is the design lever that makes that true.

## The central idea: workspace roots for source/build, deploy roots for target

Source and build hosts in a `roam` pipeline have a **workspace root** —
a single directory that contains (or will contain) the project's source
tree. All source-relative paths `roam` deals with are expressed
**relative to that root**, not as absolute paths. Target hosts are
different: they have a **deploy root**, declared per profile as
`deploy.path`, because they run published output rather than host a repo
clone.

```yaml
# roamfile.yaml
hosts:
  laptop:
    ssh: laptop.tailnet.ts.net
    workspace: ~/src/kiosk-ui
  workstation:
    ssh: workstation.tailnet.ts.net
    workspace: ~/src/kiosk-ui
  kiosk-01:
    ssh: kiosk-01.tailnet.ts.net
    user: kiosk

profiles:
  kiosk:
    target: kiosk-01
    deploy:
      path: /opt/kiosk-ui
```

Everything source-oriented `roam` does thereafter is workspace-relative.
When `roam` tells the build host to run `dotnet publish`, it does so
inside `${host.workspace}`. When it syncs artifacts to a target that
wants to preserve the MSBuild layout, it syncs from
`${build.workspace}/src/KioskUi/bin/Release/net8.0/linux-arm64/publish/`
to the corresponding relative location under `deploy.path`.

The consequence is that **the MSBuild output layout is the same on
every host that carries a workspace, because it's always
`bin/Release/...` under the csproj, and the csproj is always at the
same relative path from the workspace root.** `roam` doesn't invent a
new layout; it mirrors MSBuild's existing layout where preserving that
layout helps, and it only collapses to a deploy root when the profile
explicitly asks for it.

## Why this makes the IDE "just work"

On the source host (the laptop), the IDE was opened against
`~/src/kiosk-ui`. It already knows:

- Where the csproj lives (`src/KioskUi/KioskUi.csproj`).
- Where the build output goes (`src/KioskUi/bin/Release/...`).
- Where the published binary ends up
  (`src/KioskUi/bin/Release/net8.0/linux-arm64/publish/KioskUi`).

When `roam run workstation-to-laptop` finishes, the synced
artifact lands at exactly that path under the laptop's workspace.
The IDE's "Run" button, the debugger's launch config, the
launchSettings.json profiles — all of them are unchanged, because
the artifact is in the same place it would have been if the build
had run locally.

The developer workflow is:

```
edit on laptop  →  roam run workstation-to-laptop  →  F5 in the IDE
```

And the F5 step is exactly the same keyboard shortcut, the same
launch config, and the same file path as it would be in a
laptop-only build. The only thing `roam` changed is *where the
compile happened*; the artifact's final resting place is unchanged.

## Sync-source: one-way by default, source is authoritative

When repos are already cloned to multiple hosts, `roam` has to
decide whether those existing clones are:

- (A) **ephemeral mirrors** that `roam` overwrites from the
  authoritative source host on every run, or
- (B) **independent clones** that each host keeps in sync via git,
  with `roam` only verifying they match before building.

### Default: source-host-authoritative one-way sync

The default is (A). Every `roam run` invocation begins by syncing
the source tree from the source host's workspace to the build
host's workspace, one-way, source wins. Any local edits on the
build host are overwritten.

This one-way mirror mode is the only approved source-sync mode in v0.
The alternative ideas later in this document remain design space for
post-v0 versions.

Why this is the default:

- **Consistency.** The developer edits on one host — their laptop.
  That's the only place with authoritative source. Everywhere else
  is a reflection of that. No "which clone did I edit on?" drift.
- **Matches the mental model.** The developer thinks "I edited
  locally and asked the workstation to build it." That's exactly
  what happens.
- **Survives weird states.** A half-finished `git merge` on the
  workstation doesn't break the build, because the sync blows it
  away.
- **No git dependency on the build host** beyond whatever MSBuild
  wants. The build host doesn't need to know about branches,
  remotes, or credentials.

### Sync scope: git-tracked files only

The source sync transfers exactly the files git tracks — determined
by `git ls-files` (or by reading the git index directly) on the
source host. This is an **include-only** model, not an
exclude-based filter.

Why include-only instead of `.gitignore`-based exclusion:

- **Correct by construction.** If git tracks it, it's source. If
  git doesn't track it, it's generated. There's no exclusion list
  to get wrong — `bin/`, `obj/`, `publish/`, `.vs/`, `.idea/`,
  `node_modules/`, and every other build artifact is untouched on
  the build host because it was never in scope.
- **Preserves incremental builds.** The build host's `obj/`
  directory, NuGet cache, and any other build-side state survive
  every sync automatically. MSBuild's incremental compilation
  works across `roam run` invocations because generated files are
  never disturbed.
- **Predictable.** The developer already knows what git tracks.
  There's no second exclusion list to maintain, no `.roamignore`,
  no "did I remember to exclude the vendor directory?" questions.
- **Fast.** The working tree of a typical .NET repo (only tracked
  source files) is small; the generated output that lives alongside
  it is not. By scoping to tracked files, the sync is always
  proportional to source size, not total workspace size.

Untracked-but-not-ignored files (e.g., a new `.cs` file you haven't
`git add`ed yet) are **not synced** by default. This is a feature:
the build host sees exactly what git sees. If you want the build to
include a new file, stage it (`git add`). For workflows where this
is too strict, `roam` can optionally include untracked-but-not-
ignored files (the equivalent of `git ls-files --others
--exclude-standard`), controlled by a per-profile flag:

```yaml
profiles:
  kiosk:
    source-sync:
      include-untracked: true   # default: false
```

### Deferred to v1: `mode: git`

An alternative mode where both hosts keep their own independent git
working tree is being considered for v1. The sketch looks like:

```yaml
# POST-V0 — rejected by the v0 parser
profiles:
  kiosk:
    source-sync:
      mode: git
      ref:  HEAD
```

In `mode: git`, `roam` would skip the source sync entirely and
instead verify that the build host's workspace is at the same git
commit as the source host's workspace. This loses "edit-and-go" in
exchange for "my git state is sacred." It is **not in v0**: the v0
parser rejects `source-sync.mode` with a clear post-v0 error. Source
is authoritative, period, in v0.

### Not an option: bidirectional

Bidirectional sync between source and build is not a mode `roam`
offers. It turns "which clone is the source of truth?" into a
live question on every save, and that question has no good
answer. If you want to edit on both hosts, use git.

## How `roam` discovers the workspace root on each host

The workspace root is declared per-host in `roamfile.yaml`. If not
declared, `roam` uses a default of `~/roam-workspaces/<project>/`
on the remote host (never on source — source's workspace is
wherever the user already has their repo). The default is a
conservative choice: it creates a fresh directory rather than
touching anything the user already has.

If the user wants `roam` to use an existing clone instead, they
set `workspace:` explicitly. `roam` then treats that directory as
a normal clone and obeys the sync-mode rules above.

**The developer's editor is always pointed at the source host's
workspace.** That's the canonical location; everything else is
downstream.

## The target-host case

Target hosts are different. They're not source trees — they're
*deploy roots*. A kiosk doesn't have a clone of the repo; it has a
directory where the published binary runs from. That path belongs to the
profile because different profiles can legitimately deploy the same
target host to different locations:

```yaml
profiles:
  kiosk:
    target: kiosk-01
    deploy:
      path: /opt/kiosk-ui
```

When `roam` syncs artifacts to the target, it writes them under
`${profile.deploy.path}`, preserving the relative subpath from the
build host's `publish/` output. So an artifact that was at
`src/KioskUi/bin/Release/net8.0/linux-arm64/publish/` on the build
host lands under `/opt/kiosk-ui/...` on the target by default.

For most targets, that's deeper than the user wants — the kiosk's
`systemctl` unit expects the binary at `/opt/kiosk-ui/KioskUi`, not
five subdirectories in. So the profile can override which slice of the
publish output ends up at the deploy root:

```yaml
profiles:
  kiosk:
    deploy:
      # Take the contents of the publish directory and put them
      # directly at the deploy root, not nested under the
      # build-layout subdirectory.
      flatten-publish: true
      path: /opt/kiosk-ui
```

With `flatten-publish: true`, `roam` syncs the *contents* of the
build host's `publish/` directory — not the full `bin/...`
hierarchy — to `/opt/kiosk-ui/`. Result: `/opt/kiosk-ui/KioskUi`,
`/opt/kiosk-ui/KioskUi.dll`, etc. Exactly where `systemd` expects
them.

The `flatten-publish: false` mode (the default for the
source-as-target case, like the Avalonia laptop workflow) is the
"preserve full layout" mode, where the target *does* have a
source tree and wants the artifact to land in its MSBuild-native
location.

### Stale-file cleanup in v0, richer deploys later

When the publish output changes shape — a DLL gets renamed, an
assembly gets removed, a dependency gets dropped — the target must
not accumulate stale files. A leftover dependency DLL can shadow a
newer one, or a renamed executable leaves the old one sitting next
to the new one and `systemctl` restarts the wrong binary.

The approved v0 answer is intentionally simple: sync with delete
semantics after the process is stopped. That applies both to
preserve-layout targets and to `flatten-publish` deploy roots. It is the
smallest behavior that avoids stale outputs without pulling symlink-swap,
rollback retention, and extra state management into the first
implementation.

### Delete semantics, spelled out

"Delete semantics" means precisely this:

1. **Scope.** Deletion is scoped to entries recorded in the last
   `artifacts.json` manifest for this profile (see
   [`state.md`](state.md)). Files present under `deploy.path` but
   not in the manifest are **never** touched — that is the guard-rail
   against a misconfigured `deploy.path` wiping unrelated files.
2. **Trigger.** A file is deleted only when it was in the previous
   manifest and is absent from the new publish output.
3. **Symlinks.** Symlinks are unlinked; `roam` never follows them to
   delete their targets.
4. **Errors.** A failed delete (permission denied, file busy)
   aborts the sync with exit `6`. `roam` does not silently continue
   past a delete error; stale files are a silent-correctness hazard.
5. **Refusal conditions.** `roam` refuses to sync (exit `4`) when
   `deploy.path` is `/`, `$HOME`, the source host's workspace, or any
   path whose parent `roam` itself created during preflight. These
   are belt-and-braces checks on top of the manifest-scoped
   deletion.
6. **Plan mode.** `roam` does not support `--plan` in v0, but the
   sync engine is structured so a dry-run mode can be added in v1
   without changing the manifest format.

The broader design still leaves room for stage-and-swap deploys later
for flat deploy roots, especially where rollback and partial-failure
insulation matter. That belongs to a post-v0 version; see
[`implementation-contract.md`](implementation-contract.md).

## The source-path problem for debugging

There is one last wrinkle, and it's the one that makes "same
relative layout" not quite enough on its own: **PDBs embed
absolute source paths at compile time**, and the debugger uses
those paths to find source files when you step through code.

If the workstation compiles at `/home/dev/src/kiosk-ui/src/KioskUi/Program.cs`,
the PDB contains that path. When the debugger on the laptop attaches
to a process on the kiosk and wants to step into `Program.cs`, it
looks for `/home/dev/src/kiosk-ui/src/KioskUi/Program.cs` on the
*laptop* — and if the laptop is macOS, that path is `/Users/dev/...`
instead, and the debugger fails to find the file.

There are two robust fixes, and `roam` should use both:

### Fix 1 — deterministic source paths in the PDB

MSBuild supports a `<PathMap>` property and the
`<DeterministicSourcePaths>` / `<ContinuousIntegrationBuild>`
properties that rewrite absolute paths in PDBs to a normalized
form like `/_/src/KioskUi/Program.cs`. When set, the PDB no
longer carries machine-specific absolute paths; it carries
project-relative paths with a predictable prefix.

`roam` documents this snippet and recommends adding it to
`Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <DeterministicSourcePaths Condition="'$(ContinuousIntegrationBuild)' == 'true'">true</DeterministicSourcePaths>
  </PropertyGroup>
</Project>
```

`roam` always passes `-p:ContinuousIntegrationBuild=true` to
`dotnet publish` when source and build are on different hosts. This
makes the PDB carry `/_/src/KioskUi/Program.cs` regardless of which
host compiled it, **provided** the project has opted in to
deterministic paths.

`roam init` does **not** modify the csproj or
`Directory.Build.props`. When it detects that neither file declares
`DeterministicSourcePaths`, it prints the snippet above and the
suggested file path, and leaves the edit to the user. The rationale
is that roam has no business rewriting MSBuild files during a
bootstrap command — the costs of getting it wrong (corrupting
in-flight developer work) outweigh the ergonomic win of one skipped
copy-paste.

`roam run` warns once per invocation when the emitted PDB carries a
non-`/_/` prefix ("source paths are host-specific; debugging across
hosts may fail to find source files"). The warning is not an error;
same-host profiles are a perfectly legitimate use of `roam`.

### Fix 2 — sourceMap in the emitted `launch.json`

The emitted VSCode launch config then includes a `sourceFileMap`
entry that maps the normalized `/_/` prefix to the source host's
workspace:

```json
{
  "name": "roam: kiosk",
  "type": "coreclr",
  "request": "attach",
  "processName": "KioskUi",
  "pipeTransport": {
    "pipeProgram": "ssh",
    "pipeArgs": ["kiosk@kiosk-01.tailnet.ts.net"],
    "debuggerPath": "/home/kiosk/vsdbg/vsdbg"
  },
  "sourceFileMap": {
    "/_/": "/home/dev/src/kiosk-ui"
  }
}
```

The value of `sourceFileMap["/_/"]` is the **absolute path** of the
source host's workspace (`hosts.<source>.workspace`), resolved at
emit time. `roam` explicitly does **not** emit `${workspaceFolder}`
here: that VSCode variable is resolved against whichever folder the
editor happens to have open, which is not reliably the source
workspace — especially when the editor is on one machine and the
source workspace is on another (VSCode Remote-SSH, or a laptop
editor against a remote workstation). Hard-coding the absolute path
is boring and correct.

With this pair of fixes in place, the debugger resolves source
files correctly regardless of which host compiled the code or
which OS the editor runs on. The developer doesn't see the
translation; it Just Works.

`roam attach` emits the `sourceFileMap` automatically based on the
source host's workspace path. The user doesn't configure it.

## Putting it all together: a worked example

A concrete, end-to-end walkthrough for the Avalonia case:

### Setup, one time

On the laptop:

```bash
cd ~
git clone git@github.com:example/kiosk-ui.git src/kiosk-ui
cd src/kiosk-ui
dotnet tool install -g Roam.Cli
roam init
```

`roam init` scans the solution, discovers the csproj, and writes a
starter `roamfile.yaml` with `laptop` as source/build/target. The
user then edits the file to add:

```yaml
hosts:
  laptop:      { ssh: laptop.tailnet.ts.net,      workspace: ~/src/kiosk-ui }
  workstation: { ssh: workstation.tailnet.ts.net, workspace: ~/src/kiosk-ui }

profiles:
  workstation-to-laptop:
    source: laptop
    build:  workstation
    target: laptop
    publish-profile: ReleaseLaptop   # Properties/PublishProfiles/ReleaseLaptop.pubxml
    launch-profile:  Development     # launchSettings.json::Development
```

On the workstation, the user clones the repo once:

```bash
git clone git@github.com:example/kiosk-ui.git ~/src/kiosk-ui
```

`~/src/kiosk-ui` now exists on both machines. `roam` doesn't need
it to on the workstation (it would have made one), but using the
existing clone means the first sync only has to mirror the working
tree, not pull everything from scratch.

### The dev loop

```bash
# On the laptop
cd ~/src/kiosk-ui
# ... edit some Avalonia XAML and C# ...
roam run workstation-to-laptop
```

What happens under the hood:

1. **Sync source.** `roam` syncs git-tracked files from
   `~/src/kiosk-ui/` on laptop to `~/src/kiosk-ui/` on workstation
   via SSH.NET SFTP. Only files in `git ls-files` are transferred;
   `bin/`, `obj/`, `.vs/`, and all other generated artifacts on the
   workstation are untouched.
2. **Publish.** `roam` runs on workstation (via SSH.NET):
   ```
   dotnet publish src/KioskUi/KioskUi.csproj \
     -p:PublishProfile=ReleaseLaptop \
     -p:ContinuousIntegrationBuild=true
   ```
   Output lands at
   `~/src/kiosk-ui/src/KioskUi/bin/Release/net8.0/osx-arm64/publish/`.
3. **Stop.** No-op for this profile (source == target == laptop;
   no `stop` command configured because the developer runs the app
   manually via F5).
4. **Sync artifacts.** `roam` pulls
   `~/src/kiosk-ui/src/KioskUi/bin/Release/net8.0/osx-arm64/publish/`
   from workstation to the laptop, writing it to the same relative
   path: `~/src/kiosk-ui/src/KioskUi/bin/Release/net8.0/osx-arm64/publish/`.
   Files that no longer exist in the publish output are deleted from
   the laptop to prevent stale artifacts.
5. **Start.** No-op for this profile (developer starts the app via
   the IDE).
6. **Done.** `roam run` exits.

The developer hits F5 in their IDE. The IDE is already configured
for the local `osx-arm64` publish path (because that's where
`dotnet publish` puts things when you build locally). The binary
is there. It runs. Source-level debugging works because `sourceFileMap`
was set up correctly by `roam attach` (or is a no-op when the
source host and the attach host are the same machine).

At no point did the developer think about paths, configure
anything, or edit `launch.json`. The experience is identical to
"build locally" except the build happened somewhere else.

## `.roam/` state

`roam` reserves `${source-workspace}/.roam/` as a dotdir for sync
manifests, artifact-ownership records, and last-run metadata. `roam
init` appends `.roam/` to the workspace's `.gitignore`, and refuses
to run if `.roam/` is already tracked by git. The full on-disk
layout is spelled out in [`state.md`](state.md). The state directory
exists to make repeated runs and failure diagnostics deterministic;
it is not a second build cache.

## Open questions

1. **What to do when workspaces diverge across hosts.** If the
   user changes `workspace:` in `roamfile.yaml` mid-project,
   stale state in the old location should be cleaned up. A
   `roam doctor` command that verifies the workspace layout on
   every host in the config is a good escape hatch.
2. **Multi-project solutions.** If the solution has several
   csprojs and a profile builds more than one, `roam` needs a
   predictable way to sync several publish outputs. Leaning:
   profile names a set of csprojs, and each one's artifacts sync
   to the same relative path on the target.
3. **Windows path translation.** Workspace roots on Windows are
   `C:\src\kiosk-ui` style; on Linux they're `/home/user/src/kiosk-ui`.
   `roam`'s sync tool has to handle the conversion without
   dropping files or mangling paths. SSH.NET's SFTP handles this
   correctly, but there will be edge cases.
4. **Edge cases in the `git ls-files` sync scope.** The default
   sync scope is git-tracked files only. Open questions: should
   `roam` also sync submodule contents automatically? What about
   files produced by source generators that are checked in? The
   `include-untracked` flag covers the common case of new files
   not yet staged; exotic cases can be addressed per-profile.
5. **`Directory.Build.props` injection vs. recommendation.**
   Resolved for v0: `roam init` prints the snippet and leaves the
   edit to the user. `roam run` warns once per invocation when
   source paths are non-deterministic and source != build. Post-v0
   may revisit auto-injection behind a flag.

## Summary

- Every host in a `roam` pipeline has a **workspace root**,
  declared per-host in `roamfile.yaml`, except the target role,
  which uses the profile's `deploy.path` as its deploy root.
- All file paths `roam` moves or references are **workspace-
  relative**, so the MSBuild output layout is identical on every
  host that has the repo.
- The **source host is authoritative** by default; `roam` mirrors
  its working tree to the build host, one-way, every run. Opt-in
  `mode: git` for teams that prefer per-host git state.
- **Target hosts are deploy roots**, not source trees; the profile
  can `flatten-publish: true` to land the `publish/` contents at
  the deploy root without the full `bin/...` hierarchy.
- **Deterministic source paths** (`/_/` prefix in PDBs) plus
  **`sourceFileMap`** in emitted `launch.json` entries keep the
  debugger's source-file lookup working across hosts regardless
  of absolute-path differences.
- The net effect is that build artifacts end up at the same path
  they would if you'd built locally, on whichever host you care
  about — which is the whole point.
