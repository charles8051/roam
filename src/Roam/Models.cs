using System.Text.Json.Serialization;

namespace Roam;

public enum ExitCode
{
    Ok = 0,
    Usage = 2,
    Config = 3,
    Preflight = 4,
    Publish = 5,
    Sync = 6,
    Deploy = 7,
    Ready = 8,
    Attach = 9,
    Internal = 10
}

public sealed record CliOptions(
    string? RoamfilePath,
    bool Verbose,
    bool Quiet,
    string? LogFile,
    bool NoColor);

public sealed record CommandOutcome(
    ExitCode ExitCode,
    string? FailureStep = null,
    string? FailureHost = null,
    string? Message = null);

public sealed record Roamfile(
    int Version,
    string? Project,
    string? Solution,
    string? Csproj,
    IReadOnlyDictionary<string, HostSpec> Hosts,
    IReadOnlyDictionary<string, ProfileSpec> Profiles);

public sealed record HostSpec(
    string? Ssh,
    string? User,
    int? Port,
    string? IdentityFile,
    string? Workspace,
    string? Os);

public sealed record ProfileSpec(
    string? Description,
    string Source,
    string Build,
    string Target,
    string? PublishProfile,
    PublishSpec? Publish,
    // Null when the roamfile omits `launch-profile:`. ProjectMetadataResolver.LoadLaunchProfile
    // then falls back to the first profile in launchSettings.json, or to no launch profile at all
    // when the project has no launchSettings.json. See docs/configuration.md ("Defaults").
    string? LaunchProfile,
    IReadOnlyDictionary<string, string> Env,
    DeploySpec Deploy,
    RunSpec Run,
    DebugSpec Debug);

public sealed record PublishSpec(
    string Rid,
    bool SelfContained,
    string? Configuration,
    string? Framework);

public sealed record DeploySpec(
    string Path,
    bool FlattenPublish,
    string? Stop,
    string? Start,
    string? Ready,
    int ReadyTimeoutSeconds,
    int ReadyIntervalMilliseconds,
    bool InteractiveSession,
    SyncTransferMode Transfer = SyncTransferMode.PerFile,
    // Free-form shell run on the target by `roam uninstall`. When unset, roam falls back to
    // stop-process + remove-deploy-path + wipe-manifest and emits a warning. See docs/cli.md
    // ("roam uninstall") and docs/configuration.md for the full contract.
    string? Uninstall = null,
    // Reboot-durability trigger for the interactive-session scheduled task (Windows, service
    // mode). Default None preserves today's behavior: the task is registered with an action and a
    // principal but no trigger, so it does not relaunch after a reboot until the next `roam run`.
    // AtLogon attaches an -AtLogOn trigger bound to the target user so the workload returns on the
    // next logon (autologon station). Only consulted when InteractiveSession is true; see
    // docs/configuration.md.
    InteractiveSessionTrigger InteractiveSessionTrigger = InteractiveSessionTrigger.None,
    // Integrity level for the interactive-session scheduled task (Windows, service mode). Default
    // Limited preserves today's behavior: the task principal is registered -RunLevel Limited, so the
    // workload runs non-elevated. Highest registers the task to run elevated (High IL) in the
    // interactive desktop session; Task Scheduler launches a -RunLevel Highest interactive task
    // without a UAC prompt when the principal user is a local admin. Only consulted when
    // InteractiveSession is true; see docs/configuration.md.
    RunLevel RunLevel = RunLevel.Limited,
    // Unix-target durability — the analog of InteractiveSession on Windows. When true and the
    // target is non-Windows, roam wraps `roam run`'s start command in `nohup ... < /dev/null &` so
    // the service survives the SSH channel closing once the start step returns. Default false
    // preserves today's behavior: the start command runs inline and dies with the channel unless
    // the author's command daemonizes itself. Ignored on Windows targets (which use the
    // interactive-session scheduled task) and for `roam deploy` register-without-start. Reboot
    // durability is a separate systemd story (tracked in the issue tracker). YAML key: `detach`.
    bool Detach = false,
    // Agent-first diagnostics (ADR-0002). When unset, `roam diag` still fetches the roam-redirected
    // process log; this block adds operator-named log files, opt-in crash dumps, a journald unit
    // hint, and the live dump/trace tool source. YAML key: `diag`.
    DiagSpec? Diag = null,
    // Deploy-provenance globs (the stale-package footgun guard). After each deploy, roam reads the
    // AssemblyInformationalVersion of every synced managed assembly whose file name matches one of
    // these globs and prints a one-line version diff against the previous deploy, highlighting any
    // assembly whose version/hash did NOT change (the red flag for "I rebuilt a local-feed library
    // but the deployed bytes are the same"). When null/empty, roam reports just the project's own
    // primary output assembly (<ProjectName>.dll/.exe). Globs match the file name only and accept
    // `*` / `?` (e.g. `Contoso.*`, `Fabrikam.*`). YAML key: `provenance`. roam cannot ASSERT the
    // expected version, only SURFACE it — see docs/state.md (deployed-versions.json).
    IReadOnlyList<string>? Provenance = null);

// Agent-first diagnostics configuration (ADR-0002), nested under `deploy.diag`. All fields optional;
// `roam diag` degrades to fetching just the roam-redirected `roam-<profile>.out` log when this is
// absent. Read-only on the target except the opt-in crash-dump env injected at start.
public sealed record DiagSpec(
    // Inject DOTNET_DbgEnableMiniDump at start so the runtime's built-in createdump writes a minidump
    // on an unhandled crash to <deploy.path>/.roam-diag/dumps/. No extra tooling. YAML: `crash-dumps`.
    bool CrashDumps,
    // Operator-named app log files to fetch, each resolved against deploy.path when not absolute.
    // The universal artifact on a Windows target (which has no roam-redirected .out). YAML: `logs`.
    IReadOnlyList<string> Logs,
    // systemd unit name for a `journalctl --user -u <unit>` capture on a Unix target. YAML: `unit`.
    string? Unit,
    // Where dotnet-trace / dotnet-dump come from for the live dump/trace tier: assume present on the
    // target's PATH (default), or ship+run+remove a bundled single-file tool. YAML: `tool-source`.
    DiagToolSource ToolSource = DiagToolSource.Target,
    // DOTNET_DbgMiniDumpType when crash-dumps is on: 1=Mini, 2=Heap (default), 3=Triage, 4=Full.
    int DumpType = 2);

// Source of the live-diagnostics tools (dotnet-trace / dotnet-dump). Tool-gated per ADR-0002 §C.
public enum DiagToolSource
{
    // Assume the tool is on the target's PATH (operator-provisioned). Preflight, fail with guidance
    // if absent. roam installs nothing — stays inside the provisioning boundary.
    Target,

    // Ship the single-file tool to a roam-owned scratch dir, run it, fetch the artifact, remove it.
    // Legal (the diagnostic tools are redistributable) and reversible; explicit opt-in only.
    Bundled,
}

public enum SyncTransferMode
{
    // One SFTP round-trip set per file. The historical default; safe on any target.
    PerFile,

    // Pack the to-upload set into one tar.gz, transfer it once, expand on the target. Trades a
    // remote `tar` dependency for collapsing N per-file round-trips into one — a large win on
    // high-latency links during cold deploys.
    Archive,
}

// Optional trigger attached to the interactive-session scheduled task (Windows, service mode) so
// the wrapped GUI/desktop workload survives a reboot. A string enum (one value today) rather than
// a bool, to leave room for future kinds (e.g. at-startup). YAML key: `interactive-session-trigger`.
public enum InteractiveSessionTrigger
{
    // No trigger on the registered task. The historical default: roam (re)registers and starts the
    // task on each deploy, but it carries no trigger of its own and so does not relaunch after a
    // reboot until the next `roam run`.
    None,

    // Attach `New-ScheduledTaskTrigger -AtLogOn -User <target user>` so the task relaunches the
    // workload the next time that user logs on. Opt-in via `interactive-session-trigger: at-logon`.
    AtLogon,
}

// Integrity level the interactive-session scheduled task (Windows, service mode) runs at. A string
// enum rather than a bool, to leave room for future levels. YAML key: `run-level`.
public enum RunLevel
{
    // Register the task principal with `-RunLevel Limited`. The historical default: the workload
    // runs non-elevated (standard-user / Medium IL token), which is correct for supervision, C2,
    // and IPC and is fully back-compat with every existing interactive-session profile.
    Limited,

    // Register the task principal with `-RunLevel Highest` so the workload runs elevated (High IL)
    // in the interactive desktop session. Task Scheduler launches a -RunLevel Highest interactive
    // task without a UAC prompt when the principal user is a local admin. Opt-in via
    // `run-level: highest`, for the case where an elevated supervisor launches a
    // limited-privilege workload.
    Highest,
}

public sealed record RunSpec(
    RunMode Mode,
    string? Command,
    string? Stop,
    string? Ready,
    int ReadyTimeoutSeconds,
    int ReadyIntervalMilliseconds,
    bool InteractiveSession,
    int TimeoutSeconds,
    IReadOnlyList<int> SuccessExitCodes,
    // Mirrors DeploySpec.InteractiveSessionTrigger but scoped to run:. Only consulted in service
    // mode on a Windows target when InteractiveSession is true.
    InteractiveSessionTrigger InteractiveSessionTrigger = InteractiveSessionTrigger.None,
    // Mirrors DeploySpec.RunLevel but scoped to run:. Only consulted in service mode on a Windows
    // target when InteractiveSession is true.
    RunLevel RunLevel = RunLevel.Limited,
    // Mirrors DeploySpec.Detach but scoped to run:. Only consulted in service mode on a non-Windows
    // target.
    bool Detach = false);

public enum RunMode
{
    Service,
    OneShot,
}

public sealed record DebugSpec(
    bool Enabled,
    string? Debugger,
    string? Editor,
    string? ProcessName,
    bool InstallOnTarget);

public sealed record ResolvedProjectPaths(
    string WorkspaceRoot,
    string RoamfilePath,
    string ProjectPath,
    string ProjectDirectory,
    string ProjectName,
    string? SolutionPath);

public sealed record LaunchProfileInfo(
    string Name,
    string? CommandName,
    string? CommandLineArgs,
    IReadOnlyDictionary<string, string> EnvironmentVariables);

public sealed record PublishProfileInfo(
    string Name,
    string? RuntimeIdentifier,
    bool SelfContained,
    string PublishDirectory,
    string? Configuration,
    string? TargetFramework);

public sealed record ResolvedPublishSettings(
    string? Name,
    bool UsePublishProfile,
    string? RuntimeIdentifier,
    bool SelfContained,
    string PublishDirectory,
    string? Configuration,
    string? TargetFramework);

public sealed record ManifestEntry(
    string Path,
    long Size,
    double Mtime,
    string? ContentHash = null);

public sealed record SyncManifest(
    int Schema,
    string Profile,
    string? SourceHost,
    string? BuildHost,
    string? TargetHost,
    string? Workspace,
    string? DeployPath,
    bool FlattenPublish,
    string? GitHead,
    string CompletedUtc,
    IReadOnlyList<ManifestEntry> Entries);

public sealed record PublishManifest(
    int Schema,
    string Profile,
    string Fingerprint,
    string? BuildHost,
    string? PublishDirectory,
    string CompletedUtc,
    IReadOnlyList<string> Inputs);

// One managed assembly in a completed deploy's synced payload, with the versions roam read out of
// its PE/CLI metadata and the content hash carried over from artifacts.json. Persisted in
// deployed-versions.json and diffed against the prior deploy. See docs/state.md.
public sealed record DeployedAssembly(
    // Path of the assembly as recorded in artifacts.json (relative to the deploy root). Stable key
    // for the cross-deploy diff.
    string Path,
    string? InformationalVersion,
    string? FileVersion,
    string? AssemblyVersion,
    // The XxHash64 content hash reused from the artifacts.json manifest entry (null only if the sync
    // manifest happened to lack one). Lets the diff flag "same version AND same bytes" precisely.
    string? ContentHash)
{
    // The single version string surfaced in the deploy summary: informational -> file -> assembly.
    public string Display
        => First(InformationalVersion) ?? First(FileVersion) ?? First(AssemblyVersion) ?? "(unknown)";

    private static string? First(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

// Per-profile record of the managed-assembly versions roam deployed on the last run, persisted at
// .roam/manifests/<profile>/deployed-versions.json. The next deploy loads it to compute the version
// diff before overwriting it. See docs/state.md ("deployed-versions.json").
public sealed record DeployedVersionsManifest(
    int Schema,
    string Profile,
    string CompletedUtc,
    IReadOnlyList<DeployedAssembly> Assemblies);

// What sync-artifacts produced: the file-level sync manifest plus the managed-assembly provenance
// scan taken while the publish payload was still on disk (the remote-build temp dir is wiped right
// after). Kept distinct so artifacts.json and deployed-versions.json stay separate concerns.
public sealed record ArtifactSyncResult(
    SyncManifest Manifest,
    IReadOnlyList<DeployedAssembly> Provenance);

public sealed record StepResult(
    string Name,
    string Host,
    long DurationMs,
    string Status);

public sealed record RunSummary(
    [property: JsonPropertyName("schema")] int Schema,
    [property: JsonPropertyName("profile")] string Profile,
    [property: JsonPropertyName("started_utc")] string StartedUtc,
    [property: JsonPropertyName("finished_utc")] string FinishedUtc,
    [property: JsonPropertyName("exit_code")] int ExitCode,
    [property: JsonPropertyName("exit_step")] string? ExitStep,
    [property: JsonPropertyName("exit_host")] string? ExitHost,
    [property: JsonPropertyName("roam_version")] string RoamVersion,
    [property: JsonPropertyName("steps")] IReadOnlyList<StepResult> Steps);

public sealed record HostResolution(
    string Name,
    string SshHost,
    string User,
    int Port,
    string? IdentityFile,
    IReadOnlyList<string> IdentityFiles,
    string? ProxyJump,
    string? Workspace,
    string? Os,
    bool IsLocal);

public sealed record SshConfigSnapshot(
    string? HostName,
    string? User,
    int? Port,
    string? IdentityFile,
    IReadOnlyList<string> IdentityFiles,
    string? ProxyJump);

public sealed record SshIdentityCandidate(
    string Path,
    string Source,
    bool Exists,
    bool Loadable = false,
    string? FailureReason = null);

public sealed record SshIdentityLoadResult(
    IReadOnlyList<SshIdentityCandidate> Candidates,
    IReadOnlyList<Renci.SshNet.PrivateKeyFile> Keys);

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public void EnsureSuccess(string message)
    {
        if (ExitCode != 0)
        {
            throw new InvalidOperationException($"{message}{Environment.NewLine}{StdErr}".Trim());
        }
    }
}

public sealed record RemoteFileEntry(string RelativePath, long Size, DateTimeOffset LastWriteTimeUtc);

public sealed record SyncFileUpload(
    string LocalPath,
    string RelativePath,
    string DestinationPath,
    DateTimeOffset LastWriteTimeUtc);
