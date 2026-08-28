# Exit codes

**Status:** load-bearing for v0. `roam` is run from terminals, scripts,
and (eventually) CI. Distinguishable exit codes let callers react to
failure categories without parsing stderr.

## The taxonomy

Every `roam` invocation exits with exactly one of the following codes.
The code is the last signal the process emits before termination and is
paired with a structured stderr suffix (see [`logging.md`](logging.md))
that names the step and host that failed.

| Code | Name         | Meaning                                                            |
|------|--------------|--------------------------------------------------------------------|
| 0    | `ok`         | Pipeline completed; all steps succeeded.                           |
| 2    | `usage`      | CLI parse error, unknown subcommand, unknown flag, or bad args.    |
| 3    | `config`     | `roamfile.yaml` missing, unparsable, or fails schema validation.   |
| 4    | `preflight`  | A preflight check failed before any destructive work was attempted.|
| 5    | `publish`    | `dotnet publish` exited non-zero on the build host.                |
| 6    | `sync`       | Source or artifact sync failed (transport or filesystem error).    |
| 7    | `deploy`     | Stop or start command failed on the target host.                   |
| 8    | `ready`      | Readiness check timed out or the readiness command failed.         |
| 9    | `attach`     | `roam attach` could not emit or rewrite `launch.json`.             |
| 10   | `internal`   | Unexpected error in `roam` itself (bug). Stack trace in `--verbose`.|

These are the only exit codes v0 emits. Future versions may add codes;
no existing code will be reassigned or removed without a schema-version
bump.

## Exit suffix on stderr

When `roam` exits non-zero, the last line it writes to stderr has the
form:

```
roam: exit=<code> step=<step> host=<host>
```

Where:

- `<code>` is the numeric code above,
- `<step>` is one of `parse`, `preflight`, `sync-source`, `publish`,
  `stop`, `sync-artifacts`, `start`, `ready`, `attach`, or `internal`,
- `<host>` is the host on which the failure occurred, or `local` if the
  failure was in the roam process itself.

Example:

```
roam: exit=7 step=start host=kiosk-01
```

Scripts can parse this line cheaply without reading the full log stream.

## Rules

1. **Preflight always wins.** If preflight fails, the code is `4`, even
   if the underlying symptom could map to `5` or `6`. Preflight is the
   contract that no destructive work happens first.
2. **First failure wins.** If the pipeline aborts mid-step, the code is
   the one for that step, not a composite.
3. **Internal errors are `10`.** An unhandled exception in `roam`'s own
   code is a bug. It must not be reported as `usage` or `config`, even
   if the exception was raised during parsing.
4. **`roam attach` only emits `0`, `2`, `3`, `4`, or `9`.** It never
   runs the pipeline, so `5`–`8` are out of scope.
5. **SIGINT / `Ctrl-C` exits `130`** (the conventional `128 + SIGINT`),
   not one of the codes above. This is distinguishable from a real
   failure.

## Not in v0

- Partial-success codes. If `roam run` succeeds but `attach` would have
  failed, `roam run` still exits `0` — it does not run `attach`.
- Warning-only codes. Any condition `roam` warns about but continues
  past exits `0`.
- Retry-hinting codes. A transient SSH failure exits `6`; the caller
  decides whether to retry.
