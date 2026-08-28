# CLI surface

**Status:** load-bearing for v0. The v0 command set is frozen in
[`implementation-contract.md`](implementation-contract.md); this
document pins down the exact flags, arguments, and `--help` output so
the implementation and tests share one contract.

## Commands

v0 ships four subcommands and one top-level `--help`/`--version`
pair. Anything else is a usage error (exit `2`).

```
roam init                             Scaffold roamfile.yaml in the current directory.
roam run <profile>                    Execute the fixed pipeline for <profile>.
roam deploy <profile>                 Sync bytes for <profile> (register, don't start); run no app.
roam attach <profile>                 Emit VSCode launch.json entry for <profile>.
roam uninstall <profile>              Tear down a deployed profile and wipe its manifest.
roam --help | -h                      Print top-level help.
roam --version                        Print the tool version.
```

## Shared global flags

These apply to every subcommand. See [`logging.md`](logging.md) and
[`exit-codes.md`](exit-codes.md) for behavior.

| Flag                 | Meaning                                                    |
|----------------------|------------------------------------------------------------|
| `-f` / `--roamfile <path>` | Path to `roamfile.yaml`. Default: walk up from cwd.  |
| `-v` / `--verbose`   | Debug-level logging.                                       |
| `-q` / `--quiet`     | Errors only.                                               |
| `--log-file <path>`  | Write JSONL log to `<path>` at DEBUG level.                |
| `--no-color`         | Disable ANSI color and status glyphs.                      |
| `-h` / `--help`      | Print subcommand-specific help.                            |

## `roam init`

Scaffolds `roamfile.yaml` in the current directory from an existing
.NET solution or csproj. No remote activity.

```
Usage: roam init [--solution <path>] [--csproj <path>] [--force]

Options:
  --solution <path>   Explicit .sln path. Default: first *.sln in cwd.
  --csproj <path>     Explicit .csproj path. Used when no .sln is present.
  --force             Overwrite an existing roamfile.yaml.
```

Behavior:

1. Locate the solution or csproj (explicit flag > auto-discover).
2. Enumerate `Properties/launchSettings.json` and detect any existing
   `Properties/PublishProfiles/*.pubxml` files.
3. Write `roamfile.yaml` with one `dev-local` profile. If publish
   profiles exist, scaffold `publish-profile:` using the first one;
   otherwise scaffold a Roam-native `publish:` block using the current
   host RID, `self-contained: true`, and `configuration: Release`.
4. Append `.roam/` to `.gitignore` (create if missing; see
   [`state.md`](state.md)).
5. Print next steps.

Exit codes: `0`, `2`, `3` (if a pre-existing `roamfile.yaml` is
unparsable and `--force` is absent).

## `roam run <profile>`

Runs the fixed pipeline for `<profile>`. Performs preflight before
any destructive work (see [`preflight.md`](preflight.md)).

```
Usage: roam run <profile> [--source <host>] [--build <host>] [--target <host>]

Arguments:
  <profile>           Profile name as declared in roamfile.yaml.

Options:
  --source <host>     Override the source host for this invocation.
  --build <host>      Override the build host for this invocation.
  --target <host>     Override the target host for this invocation.
```

The three role overrides each accept a host key present in the
`hosts:` map. Overrides do not modify `roamfile.yaml`.

Exit codes: `0`, `2`, `3`, `4`, `5`, `6`, `7`, `8`, `10`. See
[`exit-codes.md`](exit-codes.md).

## `roam deploy <profile>`

Sync-only deploy: runs the pipeline through
`sync-source → publish → stop → sync-artifacts` and then **stops** —
no `start`, no `run.command`, no `ready`. Use it to put a profile's
bytes on the target without roam owning the process lifecycle. Same
role overrides as `roam run`; same preflight.

```
Usage: roam deploy <profile> [--source <host>] [--build <host>] [--target <host>]

Arguments:
  <profile>           Profile name as declared in roamfile.yaml.

Options:
  --source <host>     Override the source host for this invocation.
  --build <host>      Override the build host for this invocation.
  --target <host>     Override the target host for this invocation.
```

What it does after `sync-artifacts` depends on the profile:

- **Interactive-session profile** (`interactive-session: true`).
  `roam deploy` **registers** the `Roam_<profile>` scheduled task —
  the same Unregister/Action/Principal/Settings/[Trigger]/Register
  ceremony `roam run` emits, honoring `run-level` and
  `interactive-session-trigger` — but **omits the trailing
  `Start-ScheduledTask`**. The task exists for a launcher or the
  external launcher to start on its own cadence (`schtasks /Run`)
  without roam racing it to start.
- **Non-interactive profile.** Pure byte delivery: nothing is
  launched and no start command runs at all (there is no scheduled
  task to register).

The `stop` step is kept (consistent with `roam run`): a still-running
process can hold files open and break `sync-artifacts`. The `ready`
step is skipped — there is no roam-started process to health-gate. The
warm-deploy manifest is written exactly as `roam run` writes it, so a
subsequent `roam deploy` or `roam run` stays warm.

Step counter reads "step N of 4" (the four shared steps), not
"4 of 6" followed by a silent stop.

Exit codes: `0`, `2`, `3`, `4`, `5`, `6`, `7`, `10`. Never `8` (no
`ready`). See [`exit-codes.md`](exit-codes.md).

## `roam attach <profile>`

Emits or rewrites the `.vscode/launch.json` entry for `<profile>`.
Runs preflight checks `profile-exists`, `hosts-defined`, and
`debug-prerequisites`; does not require SSH reachability of build or
target.

```
Usage: roam attach <profile> [--output <path>] [--regenerate]

Arguments:
  <profile>           Profile name as declared in roamfile.yaml.

Options:
  --output <path>     launch.json path. Default: ./.vscode/launch.json.
  --regenerate        Rewrite the entry even if it exists and is current.
```

Exit codes: `0`, `2`, `3`, `4`, `9`. Never `5`–`8` (no pipeline run).

## `roam uninstall <profile>`

Tears down a profile that was deployed by `roam run`. The mirror of the
install side: where `run` ships artifacts, registers services / scheduled
tasks / firewall rules via `deploy.start`, and leaves a warm-deploy
manifest behind, `uninstall` reverses those side effects.

```
Usage: roam uninstall <profile> [--keep-manifest] [--dry-run]

Arguments:
  <profile>           Profile name as declared in roamfile.yaml.

Options:
      --keep-manifest   Preserve .roam/manifests/<profile>/ so the next
                        `roam run` stays warm (publish + sync diffs hit).
      --dry-run         Print the uninstall commands without executing them.
                        No SSH activity beyond profile resolution.
```

Behavior — pick exactly one of two paths per profile:

- **Custom uninstall** (`deploy.uninstall:` set). `roam uninstall` runs the
  block verbatim on the target. The project owns what gets removed and what
  stays; roam reports the verb ran successfully and the manifest decision.
- **Fallback** (`deploy.uninstall:` unset). `roam uninstall` runs the stop
  command (deploy.stop / run.stop, plus the Windows scheduled-task
  unregister it already emits for `interactive-session: true` profiles),
  then recursively removes `deploy.path/`. A warning is printed asking the
  operator to define `deploy.uninstall:` explicitly — the fallback can't
  know about services, registry keys, firewall rules, or anything else the
  install script touched outside `deploy.path/`.

In both paths, `.roam/manifests/<profile>/` is removed (unless
`--keep-manifest`) so the next `roam run` is a cold deploy with no
false-warm publish/artifact diff.

Exit codes: `0`, `2`, `3`, `4`, `7`. `7` (deploy) when an uninstall command
exits non-zero — the manifest is then NOT wiped, since state may still
exist on the target.

## Examples

```bash
# First-time setup
roam init

# Build on workstation, run on laptop
roam run workstation-to-laptop

# Deploy to the kiosk
roam run kiosk

# Sync the bytes only — register the task but let the external launcher start it
roam deploy kiosk

# Same profile but build locally because the workstation is down
roam run kiosk --build laptop

# Emit the VSCode debug config
roam attach kiosk

# Tear down a profile (run deploy.uninstall on target, wipe local manifest)
roam uninstall kiosk

# Same but only print what would run
roam uninstall kiosk --dry-run

# Tear down on the target but keep the warm-deploy manifest for next time
roam uninstall kiosk --keep-manifest
```

## Subcommand `--help` output

The `--help` text is a stable part of the v0 contract. Golden-file
tests compare actual output against the text below.

### `roam --help`

```
roam — build .NET on any host, run on any host, debug from anywhere.

Usage: roam <command> [options]

Commands:
  init                 Scaffold roamfile.yaml in the current directory.
  run <profile>        Run the pipeline for a profile.
  deploy <profile>     Sync bytes for a profile (register, don't start); runs no app.
  attach <profile>     Emit VSCode attach config for a profile.
  uninstall <profile>  Tear down a deployed profile and wipe its manifest.

Global options:
  -f, --roamfile <path>   Path to roamfile.yaml (default: walk up from cwd).
  -v, --verbose           Enable debug logging.
  -q, --quiet             Suppress per-step output; errors only.
      --log-file <path>   Write JSONL log to <path>.
      --no-color          Disable ANSI color.
  -h, --help              Show help.
      --version           Show version.

See https://github.com/charles8051/roam for documentation.
```

### `roam run --help`

```
roam run — execute the fixed pipeline for a profile.

Usage: roam run <profile> [options]

Arguments:
  <profile>   Profile name as declared in roamfile.yaml.

Options:
      --source <host>   Override the source host for this invocation.
      --build <host>    Override the build host for this invocation.
      --target <host>   Override the target host for this invocation.

  ... plus global options (see roam --help).
```

### `roam deploy --help`

```
roam deploy — sync a profile's bytes to the target without starting it.

Usage: roam deploy <profile> [options]

Runs sync-source → publish → stop → sync-artifacts, then stops. No start, run,
or ready. For an interactive-session profile it registers the Roam_<profile>
scheduled task but does NOT start it, so an external launcher owns start
(schtasks /Run). For a non-interactive profile it is pure byte delivery and
launches nothing.

Arguments:
  <profile>   Profile name as declared in roamfile.yaml.

Options:
      --source <host>   Override the source host for this invocation.
      --build <host>    Override the build host for this invocation.
      --target <host>   Override the target host for this invocation.

  ... plus global options (see roam --help).
```

### `roam attach --help`

```
roam attach — emit a VSCode launch.json entry for a profile.

Usage: roam attach <profile> [options]

Arguments:
  <profile>   Profile name as declared in roamfile.yaml.

Options:
      --output <path>   launch.json path (default: .vscode/launch.json).
      --regenerate      Rewrite the entry even if it already exists.

  ... plus global options (see roam --help).
```

### `roam init --help`

```
roam init — scaffold roamfile.yaml in the current directory.

Usage: roam init [options]

Options:
      --solution <path>   Explicit .sln path.
      --csproj <path>     Explicit .csproj path.
      --force             Overwrite an existing roamfile.yaml.

  ... plus global options (see roam --help).
```

### `roam uninstall --help`

```
roam uninstall — tear down a deployed profile and wipe its manifest.

Usage: roam uninstall <profile> [options]

Arguments:
  <profile>   Profile name as declared in roamfile.yaml.

Options:
      --keep-manifest   Preserve .roam/manifests/<profile>/ (next run stays warm).
      --dry-run         Print the uninstall commands without running them.

  ... plus global options (see roam --help).
```

## Not in v0

- `roam watch`, `roam doctor`, `roam migrate`, `roam install-debugger`,
  `roam uninstall --receipt` (the receipt-based generic uninstall protocol is
  backlogged behind `deploy.uninstall:`; see `docs/state.md` and the issue tracker).
- Positional host overrides (e.g. `roam run kiosk laptop`). Overrides
  are named flags only.
- Environment variable overrides for CLI flags. `ROAM_VERBOSE=1` does
  nothing in v0; use `-v` explicitly.
- Shell completion scripts. These are trivially added post-v0 from
  `System.CommandLine`'s built-in support.
