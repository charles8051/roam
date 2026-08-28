# Targeting Windows PowerShell 5.1 over OpenSSH

Windows targets running stock Windows PowerShell 5.1, reached through OpenSSH, have a set of
behaviours that do not appear in mainstream PowerShell documentation — because they only manifest in
the SSH-launched-process case. roam works around all four; they are recorded here because anyone
writing a `deploy.start:` or install script for a Windows target will hit them, and because roam's own
source comments refer to them by number.

## 1. `$PSScriptRoot` and `$PSCommandPath` are empty during `param()` default evaluation

When a script is launched with `powershell -File` over OpenSSH, both variables are empty while the
`param()` block's default expressions are evaluated. They are populated by the time the script body
runs.

**Workaround:** move any default derived from the script's own location out of `param()` and into the
body.

## 2. `MultipleInstances StopExisting` is PowerShell 7+ only

PowerShell 5.1's `ScheduledTasks` module accepts only `Parallel`, `Queue`, and `IgnoreNew`.

This matters more than it looks. A task left in a stale `Running` state, combined with `IgnoreNew`,
makes a subsequent `Start-ScheduledTask` a **silent no-op** — no error, no start.

**Workaround:** an explicit stop-then-start before `Start-ScheduledTask`.

## 3. Non-ASCII characters break parsing of BOM-less `.ps1` files

PowerShell 5.1 reads a script with no byte-order mark as Windows-1252. Em-dashes, arrows and smart
quotes turn into mojibake, and the parser then dies on the next quote-like character — with an error
that points nowhere near the real problem.

**Workaround:** keep `.ps1` files ASCII-only, or save them as UTF-8 **with** a BOM. roam writes its
staged start script with a BOM for exactly this reason.

## 4. `-EncodedCommand` has a payload limit, and it is cmd.exe's

Windows OpenSSH's default shell is `cmd.exe` unless `DefaultShell` is set explicitly, so an
`-EncodedCommand` payload is subject to cmd.exe's command-line limit of roughly 8191 characters.

Base64-of-UTF-16 is about 2.7x, so a script encoded twice — which is easy to do accidentally when one
layer wraps another — lands at roughly 7x its original size. Past the limit, cmd.exe **truncates**
rather than failing, so the payload decodes to a syntactically broken half-script and PowerShell
reports a parse error such as `MissingEndCurlyBrace`. The reported error has nothing to do with the
actual cause.

**Workaround:** for any non-trivial script, stage a `.ps1` on the target and execute it with
`powershell.exe -File` rather than passing it inline. roam does this for its interactive-session start
path.
