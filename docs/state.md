# `.roam/` state store

**Status:** load-bearing for v0. The implementation contract reserves
`.roam/` for sync manifests, generated-artifact ownership, and
last-run metadata, but doesn't pin down the layout. This document
does.

## Scope

`.roam/` lives in the **source host's workspace root** — the same
directory that contains `roamfile.yaml`. It is not replicated to
build or target hosts. Everything under `.roam/` is:

- owned by `roam`,
- safe to delete at any time (deleting it loses caches and
  diagnostic traces but does not break future runs),
- small (expected size is well under 10 MB for a typical project),
- JSON-encoded so a human can read it with `jq` or `cat`.

## Directory layout

```
.roam/
  schema-version          # plain text: the integer schema version
  manifests/
    <profile>/
      source.json            # last sync-source manifest (build host)
      artifacts.json          # last sync-artifacts manifest (target host)
      publish.json            # last publish-input fingerprint (build host)
      deployed-versions.json  # last deploy's managed-assembly versions (provenance)
  runs/
    last.json             # most recent run summary (any profile)
    <profile>.json        # most recent run per profile
  tmp/                    # scratch space; safe to wipe between runs
```

Everything else under `.roam/` is reserved for future use. v0 ignores
unknown files and subdirectories rather than deleting them.

## `schema-version`

A single line containing an integer, currently `1`. Bumped only when
the on-disk format in this document changes in a non-backwards-
compatible way. If `roam` reads a `.roam/` with a schema version it
does not recognize, it refuses to run and instructs the user to
either upgrade `roam` or `rm -rf .roam/` to start fresh.

## `manifests/<profile>/source.json`

The record of what was last synced from source to build for this
profile. Used by the sync engine to decide which files need
re-transfer on the next run.

```json
{
  "schema": 1,
  "profile": "workstation-to-laptop",
  "source_host": "laptop",
  "build_host": "workstation",
  "workspace": "~/src/kiosk-ui",
  "git_head": "a1b2c3d4e5f6...",
  "completed_utc": "2026-04-16T14:23:01.123Z",
  "entries": [
    {"path": "src/KioskUi/Program.cs", "size": 842, "mtime": 1713270181.123, "sha256": "…"},
    {"path": "src/KioskUi/KioskUi.csproj", "size": 345, "mtime": 1713270181.000, "sha256": "…"}
  ]
}
```

Fields:

- `schema` — manifest schema version, independent of the directory
  `schema-version`.
- `profile`, `source_host`, `build_host`, `workspace` — identity.
- `git_head` — the source host's `git rev-parse HEAD` at sync time.
  Purely informational for diagnostics.
- `completed_utc` — ISO-8601 timestamp.
- `entries` — one object per tracked file, sorted by `path`
  ascending. `sha256` is present only if the entry was hashed
  during sync; `null` otherwise.

## `manifests/<profile>/artifacts.json`

Same shape as `source.json` but describes what was last deployed to
`deploy.path` on the target.

```json
{
  "schema": 1,
  "profile": "kiosk",
  "build_host": "workstation",
  "target_host": "kiosk-01",
  "deploy_path": "/opt/kiosk-ui",
  "flatten_publish": true,
  "completed_utc": "2026-04-16T14:23:14.456Z",
  "entries": [
    {"path": "KioskUi", "size": 12345678, "mtime": 1713270194.456, "sha256": "…"},
    {"path": "KioskUi.dll", "size": 234567, "mtime": 1713270194.456, "sha256": "…"}
  ]
}
```

The `entries` array is the authoritative record of which files
`roam` placed under `deploy_path`. Deletion during the next sync is
scoped strictly to this list; files not in the manifest are never
touched, even if they live under `deploy_path`. This is the
guard-rail against a misconfigured `deploy.path` wiping unrelated
files (see [`paths.md`](paths.md), "Delete semantics").

## `manifests/<profile>/publish.json`

The fingerprint of every input that fed the last successful `dotnet publish`
for this profile. On the next run, `roam` recomputes the fingerprint over the
current workspace and skips the publish step when it matches and the publish
output is still on disk. Local-build profiles only — for remote build hosts
the publish step always runs (see [`design.md`](design.md) §2 for why we
verify the output before honouring the cache).

```json
{
  "Schema": 2,
  "Profile": "kiosk",
  "Fingerprint": "9c2a4f7e1b8d6a30",
  "BuildHost": "workstation",
  "PublishDirectory": "obj/roam/kiosk/publish",
  "CompletedUtc": "2026-05-30T14:23:01.123Z",
  "Inputs": [
    "src/KioskUi/KioskUi.csproj",
    "src/KioskUi/Program.cs",
    "src/KioskUi/obj/project.assets.json",
    "Directory.Build.props",
    "Directory.Packages.props",
    "nuget.config",
    "global.json"
  ]
}
```

Fields:

- `Schema` — fingerprint algorithm version. A schema bump invalidates every
  cached manifest; the next publish runs and rewrites at the new schema.
  Schema 2 (2026-06) added the dependency inputs below, closing a hole where a
  package bump or floating-version rebuild — which moves no source file — was
  invisible to the fingerprint and shipped stale binaries on a warm deploy.
  Schema 3 (2026-06) added the **local-feed package file hashes** below, closing
  the remaining hole where a same-version re-pack of a local FOLDER-feed package
  was invisible because NuGet's global cache kept serving the old extraction (so
  `project.assets.json` still recorded the cached sha512). The v2→v3 bump makes
  the first run after a roam upgrade a guaranteed re-publish (old manifests are a
  schema mismatch).
- `Profile`, `BuildHost`, `PublishDirectory` — identity, for diagnostics.
- `Fingerprint` — combined xxhash64 over:
  - the file content of the `<ProjectReference>` closure starting at the
    profile's csproj (excluding `bin/`, `obj/`, `.git/`, `.vs/`, `.idea/`,
    `.vscode/`, `.roam/`, `node_modules/`);
  - `Directory.Build.props`/`.targets` **and `Directory.Packages.props`** at
    every ancestor up to the workspace root (the last pins Central Package
    Management versions);
  - `nuget.config` at every ancestor up to the workspace root (it selects the
    package feeds a version resolves against);
  - **`obj/project.assets.json` for each project in the closure** — the
    resolved dependency graph (every resolved package id, version, and sha512).
    This is the only signal for a dependency change that touches no source
    file; `obj/` is otherwise excluded, this one file is read out of it
    deliberately;
  - `global.json` at the workspace root;
  - the publish-profile `.pubxml` (when `publish-profile:` is in use);
  - **the actual `.nupkg` file hash of every resolved package whose restore
    source is a local FOLDER feed** (schema 3). roam parses the effective
    `nuget.config` sources (the same ancestor walk, honouring
    `packageSourceMapping`), identifies folder / `file://` sources, and for each
    resolved package in `project.assets.json` checks whether a matching
    `<id>.<version>.nupkg` exists in a folder source (flat or hierarchical v3
    layout). If so, its file hash is folded in. This is the only signal for a
    same-version re-pack of a folder-feed package — NuGet's global cache can keep
    serving the old extraction, so the assets.json sha512 looks unchanged.
    **HTTP-feed packages (nuget.org, GitHub Packages) contribute nothing** —
    their version coordinate is immutable. *Boundary:* this fixes the
    skip-**publish** half only; it does not bypass the NuGet global-cache
    extraction itself, so a stale cached extraction can still feed
    `dotnet publish`. A forced/clean restore is the separate cure (see
    the issue tracker);
  - and the rendered `dotnet publish` command line plus the
    `ContinuousIntegrationBuild` flag.

  **Not** hashed: the full `roamfile.yaml`, `launchSettings.json` outside the
  project tree, or anything inside an excluded directory other than the
  per-project `obj/project.assets.json` named above. One residual blind spot:
  `project.assets.json` reflects only the last `dotnet restore`, so a
  floating-version (`1.2.*`) change that has not yet been restored is not seen
  until the next restore refreshes it (see the issue tracker).
- `CompletedUtc` — ISO-8601 timestamp of the successful publish.
- `Inputs` — every path the fingerprint considered, plus a
  `localfeed:<id>/<version>` marker for each folder-feed package whose `.nupkg`
  was content-keyed (schema 3). Diagnostic-only; changing this list does not
  invalidate the cache.

Deleting `publish.json` forces a re-publish on the next run. Editing fields
other than `Fingerprint` has no effect — only the hash equality decides.

## `manifests/<profile>/deployed-versions.json`

The managed-assembly **provenance** of the last successful deploy: for every
synced assembly whose file name matched a `deploy.provenance:` glob (default: the
project's own primary output assembly), the versions roam read straight out of
the assembly's PE/CLI metadata, plus the content hash carried over from
`artifacts.json`.

```json
{
  "Schema": 1,
  "Profile": "kiosk",
  "CompletedUtc": "2026-06-15T14:23:14.456Z",
  "Assemblies": [
    {
      "Path": "Contoso.Widgets.dll",
      "InformationalVersion": "1.5.1-alpha.1+9cf5381...",
      "FileVersion": "1.5.1.0",
      "AssemblyVersion": "1.5.1.0",
      "ContentHash": "9c2a4f7e1b8d6a30"
    }
  ]
}
```

On the next deploy, roam loads this file *before* overwriting it and prints a
one-line version diff per assembly — `<name>  <old>  ->  <new>`, or
`<name>  <ver>  ->  (unchanged)` when the version **and** bytes are byte-identical
to the prior deploy. The unchanged case is the whole point: it turns an invisible
"the new behavior just didn't appear" into a visible "that version didn't change",
which is the only practical defense against a `.nupkg` that carries the right
version but stale bytes (Mode A — the lie is *inside* the package, so the publish
fingerprint cannot catch it). roam reads the version via
`System.Reflection.Metadata` (`MetadataReader` over the assembly's custom
attributes) and **never** `Assembly.LoadFrom`, so it works cross-platform on a
foreign win-x64 self-contained publish without loading or locking it.

This manifest only *surfaces* — it cannot assert the *expected* version (roam
doesn't know it). Deleting it just shows every assembly as "new" on the next
deploy. `Path` is the assembly's path as recorded in `artifacts.json` (relative to
the deploy root); it is the stable key for the diff.

## `runs/last.json` and `runs/<profile>.json`

Per-run summary, written at the end of every `roam run` (success or
failure). Purpose: quick diagnostics, no history retention.

```json
{
  "schema": 1,
  "profile": "kiosk",
  "started_utc": "2026-04-16T14:22:58.000Z",
  "finished_utc": "2026-04-16T14:23:14.456Z",
  "exit_code": 0,
  "exit_step": null,
  "exit_host": null,
  "roam_version": "0.1.0",
  "steps": [
    {"name": "sync-source",     "host": "workstation", "duration_ms": 812,  "status": "ok"},
    {"name": "publish",         "host": "workstation", "duration_ms": 12431, "status": "ok"},
    {"name": "stop",            "host": "kiosk-01",    "duration_ms": 312,  "status": "skipped"},
    {"name": "sync-artifacts",  "host": "kiosk-01",    "duration_ms": 1198, "status": "ok"},
    {"name": "start",           "host": "kiosk-01",    "duration_ms": 204,  "status": "ok"},
    {"name": "ready",           "host": "kiosk-01",    "duration_ms": 1102, "status": "ok"}
  ]
}
```

`runs/last.json` is a copy of the most recent run across all
profiles; `runs/<profile>.json` is the most recent for that profile
specifically. v0 keeps only one run per profile; rotation and
retention policies are post-v0.

## `tmp/`

Scratch space for intermediate files `roam` generates during a run
(parsed `ssh -G` output, temporary manifest diffs, captured remote
stderr buffers). Safe to wipe at any time. `roam` clears
`tmp/<run-id>/` on successful exit; failed runs leave it in place
for diagnostics.

## Gitignore

`roam init` appends `.roam/` to the workspace's `.gitignore`,
creating the file if it doesn't exist. The entry is appended only if
not already present. If `.roam/` is already tracked by git,
`roam init` refuses to run and prints:

```
.roam/ is tracked by git; remove it from the index before running
roam init:

    git rm -r --cached .roam/

Then re-run roam init.
```

This avoids clobbering a user who has deliberately committed state or
who is converting from a different tool.

## Idempotency semantics

`roam` treats manifests as an *opportunistic* cache, never a
correctness gate:

- `sync-source` consults `source.json` to skip files with matching
  size+mtime; any file whose metadata mismatches is re-transferred.
- `sync-artifacts` consults `artifacts.json` the same way and uses
  the list of entries to decide which target files are safe to
  delete.
- `publish` consults `publish.json` and skips when the fingerprint
  matches the current workspace AND the publish output is still on
  disk. Local-build profiles only; remote-build profiles always
  re-publish in v0. See the `publish.json` section above for the
  fingerprint contract.
- `stop`, `start`, and `ready` **always re-run** regardless of
  last-run state. There is no skip-if-successful shortcut; the target
  process must be restarted to pick up new artifacts.
- `deployed-versions.json` is **purely informational** — it never gates a
  step. It is read to compute the post-deploy version diff and then
  overwritten; a missing or corrupt file just shows every assembly as "new"
  on the next deploy.

Deleting `.roam/` forces a full re-sync on the next run but is
otherwise harmless. `roam uninstall <profile>` wipes
`.roam/manifests/<profile>/` as part of tear-down (so the next deploy
of that profile is cold); pass `--keep-manifest` to leave it in place.
See [`cli.md`](cli.md) for the verb's full contract.

## Partial failure semantics

A sync is **all-or-nothing for the manifest**. `source.json` and
`artifacts.json` are advanced *only* after their sync step has fully
succeeded — every stale-file delete and every upload. If any delete or
upload fails partway, the sync step throws, and the orchestrator never
reaches the manifest save. The manifest on disk is left exactly as the
last fully-successful sync wrote it.

This is what keeps a failed deploy from lying about target state. A
manifest that recorded "all 250 files are present" after only 120 had
been uploaded would cause the next run to skip the missing 130 — a
permanently-broken deploy that looks warm. Refusing to advance the
manifest on failure makes that unrepresentable.

**Convergence on the next run.** Because the manifest was not advanced,
the next `roam run` re-diffs the current source against the *last
fully-synced* baseline:

- Files that were uploaded just before the failure are re-sent — their
  bytes are correct on the target, but the baseline still records the
  old hash, so the diff re-uploads them. This redundant re-send is the
  deliberate fail-safe direction (re-send a correct file, never skip a
  missing one).
- Stale-file deletes are recomputed from the un-advanced baseline and
  retried. `DeleteFileAsync` is existence-guarded, so deleting an
  already-deleted file is a no-op. Deletes are therefore idempotent
  across a failed-then-retried run.
- Once a run completes without error, the manifest is rewritten to the
  new full state and the next run is warm again.

There is **no in-run auto-retry**. Re-running the command *is* the
retry, and it converges. A failed run still writes a `runs/<profile>.json`
summary (exit code, failing step, failing host) for diagnostics — that
is a run trace, not a sync manifest, and never feeds the diff.

**Transport-specific cleanup.** The two artifact transports
(see [`transport.md`](transport.md)) leave no orphaned state behind a
failure:

- **`per-file`** uploads each changed file in place. A failed upload may
  leave a truncated file at its final path; because that file is a
  *changed* file (baseline mismatch), the next run re-uploads it with
  overwrite. No new paths are created, so nothing accumulates.
- **`archive`** ships one `tar.gz` into the deploy root and extracts it
  remotely. The archive is removed on the extract command's success path;
  on a failed or interrupted extract `roam` best-effort removes the
  orphaned tarball as well. This matters because the archive is **not**
  manifest-owned — manifest-scoped deletion (above) would never reclaim
  it — so without this cleanup, repeated archive-deploy failures would
  litter the deploy root with `roam-sync-*.tar.gz` files.

## Never stored in `.roam/`

- SSH private keys, known_hosts entries, or agent socket paths.
- Environment variable values, command output, or file contents
  beyond the hashes and sizes listed above.
- Secrets of any kind — see [`security.md`](security.md).

If a future feature needs to cache something sensitive, this document
is updated and the schema version is bumped.
