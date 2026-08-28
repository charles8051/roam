using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Roam;

public sealed class RoamCommands
{
    private readonly SshHostResolver _ssh = new();

    public async Task<CommandOutcome> RunInitAsync(CliOptions cli, string? solution, string? csproj, bool force, CancellationToken cancellationToken, string? workingDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(solution) && !string.IsNullOrWhiteSpace(csproj))
        {
            throw new RoamException(ExitCode.Usage, "parse", "local", "roam init accepts either --solution or --csproj, not both");
        }

        if (workingDirectory is not null && !Directory.Exists(workingDirectory))
        {
            throw new RoamException(ExitCode.Usage, "parse", "local", $"roam init working directory does not exist: {workingDirectory}");
        }

        workingDirectory ??= Directory.GetCurrentDirectory();
        var outputPath = Path.Combine(workingDirectory, "roamfile.yaml");
        if (File.Exists(outputPath) && !force)
        {
            throw new RoamException(ExitCode.Config, "parse", "local", "roamfile.yaml already exists; use --force to overwrite it");
        }

        var chosenSolution = solution;
        var chosenCsproj = csproj;

        if (string.IsNullOrWhiteSpace(chosenSolution) && string.IsNullOrWhiteSpace(chosenCsproj))
        {
            chosenSolution = Directory.GetFiles(workingDirectory, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
            chosenCsproj = chosenSolution is null
                ? Directory.GetFiles(workingDirectory, "*.csproj", SearchOption.AllDirectories).FirstOrDefault()
                : null;
        }

        if (string.IsNullOrWhiteSpace(chosenSolution) && string.IsNullOrWhiteSpace(chosenCsproj))
        {
            throw new RoamException(ExitCode.Config, "parse", "local", "roam init could not find a .sln or .csproj in the current directory");
        }

        var projectPath = chosenCsproj is not null
            ? Path.GetFullPath(chosenCsproj, workingDirectory)
            : Directory.GetFiles(workingDirectory, "*.csproj", SearchOption.AllDirectories).FirstOrDefault();

        if (projectPath is null)
        {
            throw new RoamException(ExitCode.Config, "parse", "local", "roam init found a solution but could not infer a csproj; pass --csproj explicitly");
        }

        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var publishProfilesDirectory = Path.Combine(projectDirectory, "Properties", "PublishProfiles");
        var publishProfiles = Directory.Exists(publishProfilesDirectory)
            ? Directory.GetFiles(publishProfilesDirectory, "*.pubxml", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray()
            : Array.Empty<string>();

        var launchSettingsPath = Path.Combine(projectDirectory, "Properties", "launchSettings.json");
        var launchProfiles = File.Exists(launchSettingsPath)
            ? System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(launchSettingsPath, cancellationToken)).RootElement.GetProperty("profiles").EnumerateObject().Select(x => x.Name).ToArray()
            : Array.Empty<string>();

        var projectName = Path.GetFileNameWithoutExtension(projectPath);
        var relativeSolution = chosenSolution is null ? null : Path.GetRelativePath(workingDirectory, Path.GetFullPath(chosenSolution, workingDirectory)).Replace('\\', '/');
        var relativeCsproj = Path.GetRelativePath(workingDirectory, projectPath).Replace('\\', '/');

        // The scaffold writes only what roam cannot derive. Everything omitted here — version, the
        // local host block, the three host roles, publish, deploy.path — is filled in by
        // ConfigLoader's defaults; see docs/configuration.md ("Defaults").
        var builder = new StringBuilder();
        builder.Append(relativeSolution is not null
            ? $"solution: {relativeSolution}\n"
            : $"csproj: {relativeCsproj}\n");
        builder.Append("\nprofiles:\n  dev-local:\n    description: Publish and run everything on this machine.\n");

        if (publishProfiles.Length > 0)
        {
            builder.Append($"    publish-profile: {publishProfiles[0]}\n");
        }

        // Only pin a launch profile when the project has more than one; with zero or one, the
        // default (first profile, or none at all) already picks the same thing.
        if (launchProfiles.Length > 1)
        {
            builder.Append($"    launch-profile: {launchProfiles[0]}\n");
        }

        builder.Append($"    debug:\n      enabled: true\n      debugger: vsdbg\n      editor: vscode\n      process-name: {projectName}\n");
        var content = builder.ToString();

        await File.WriteAllTextAsync(outputPath, content, cancellationToken);
        EnsureGitIgnoreHasRoam(workingDirectory);
        Console.WriteLine("Scaffolded roamfile.yaml");
        return new CommandOutcome(ExitCode.Ok);
    }

    public async Task<CommandOutcome> RunAttachAsync(CliOptions cli, string profileName, string? outputPath, bool regenerate, CancellationToken cancellationToken)
    {
        var context = await LoadContextAsync(cli, profileName, cancellationToken);
        PreflightProfileExists(context.Roamfile, profileName);
        PreflightHostsDefined(context.Roamfile, context.Profile, profileName);
        ValidateDebugPrerequisites(profileName, context.Profile);

        var targetResolution = await _ssh.ResolveAsync(context.Profile.Target, context.Roamfile.Hosts[context.Profile.Target], isLocal: context.Profile.Target == context.Profile.Source, cancellationToken);
        var buildResolution = await _ssh.ResolveAsync(context.Profile.Build, context.Roamfile.Hosts[context.Profile.Build], isLocal: context.Profile.Build == context.Profile.Source, cancellationToken);
        var remoteProjectDirectory = CombineUnixPath(buildResolution.Workspace ?? context.ProjectPaths.WorkspaceRoot, Path.GetRelativePath(context.ProjectPaths.WorkspaceRoot, context.ProjectPaths.ProjectDirectory).Replace('\\', '/'));
        var launchPath = outputPath is null ? Path.Combine(context.ProjectPaths.WorkspaceRoot, ".vscode", "launch.json") : Path.GetFullPath(outputPath, Directory.GetCurrentDirectory());
        await DebuggerEmitter.EmitAsync(launchPath, profileName, context.ProjectPaths.WorkspaceRoot, context.ProjectPaths.ProjectDirectory, remoteProjectDirectory, targetResolution, context.Profile.Debug, cancellationToken);
        Console.WriteLine($"Wrote {launchPath}");
        return new CommandOutcome(ExitCode.Ok);
    }

    // `roam diag`: agent-first diagnostics (ADR-0002). Read-only capture of a log/dump bundle from
    // the target into .roam/diag/<profile>/<run-id>/ plus a machine-readable diag.json index. Logs
    // are the default tier; --dump adds crash dumps. Inside the provisioning boundary: it fetches
    // and captures only, and mutates no target host state.
    public async Task<CommandOutcome> RunDiagAsync(CliOptions cli, string profileName, string? outputDir, bool logsFlag, bool dumpFlag, string? traceValue, string? since, bool json, bool keepRemote, CancellationToken cancellationToken)
    {
        var context = await LoadContextAsync(cli, profileName, cancellationToken);
        PreflightProfileExists(context.Roamfile, profileName);
        PreflightHostsDefined(context.Roamfile, context.Profile, profileName);
        var profile = context.Profile;

        var targetHost = await _ssh.ResolveAsync(profile.Target, context.Roamfile.Hosts[profile.Target], isLocal: profile.Target == profile.Source, cancellationToken);

        int? traceSeconds = null;
        if (traceValue is not null)
        {
            if (!int.TryParse(traceValue, out var ts) || ts <= 0)
            {
                throw new RoamException(ExitCode.Usage, "diag", targetHost.Name, $"--trace expects a positive number of seconds, got '{traceValue}'");
            }

            traceSeconds = ts;
            Console.Error.WriteLine("  warning: --trace (live trace tier) is not yet implemented in this build; capturing logs/dumps only. See the issue tracker.");
        }

        // Logs are always captured (cheap, and the primary agent signal); --dump (and a future
        // --trace) are additive on top. --logs is accepted for explicitness; logs are on regardless.
        _ = logsFlag;
        var includeLogs = true;
        var includeDump = dumpFlag;

        var windowsTarget = IsWindowsHost(targetHost);
        // The roam-redirected process stdout (Unix detach profiles only). Must match the logPath that
        // BuildStartCommand writes; Windows interactive-session tasks have no redirect.
        var redirectedLog = windowsTarget
            ? null
            : $"{profile.Deploy.Path.Replace('\\', '/').TrimEnd('/')}/roam-{SanitizeTaskName(profileName)}.out";

        var options = new DiagOptions(includeLogs, includeDump, traceSeconds, since);
        var plan = DiagPlanner.Plan(profile.Deploy, windowsTarget, options, redirectedLog);

        var runId = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var outDir = outputDir is not null
            ? Path.GetFullPath(outputDir, Directory.GetCurrentDirectory())
            : Path.Combine(context.ProjectPaths.WorkspaceRoot, ".roam", "diag", SanitizeTaskName(profileName), runId);

        RoamLog.Event("diag.start", "diagnostics capture starting", new Dictionary<string, object?>
        {
            ["profile"] = profileName,
            ["target"] = targetHost.Name,
            ["outDir"] = outDir,
            ["includeLogs"] = includeLogs,
            ["includeDump"] = includeDump,
            ["fileCount"] = plan.Files.Count,
            ["globCount"] = plan.Globs.Count,
            ["captureCount"] = plan.Captures.Count,
        });

        SftpRemoteFileFetcher? sftp = null;
        DiagIndex index;
        try
        {
            IRemoteFileFetcher fetcher;
            IRemoteCommandRunner runner;
            if (targetHost.IsLocal)
            {
                fetcher = new LocalRemoteFileFetcher();
                runner = new LocalCommandRunner();
            }
            else
            {
                sftp = new SftpRemoteFileFetcher(targetHost);
                fetcher = sftp;
                runner = new SshCommandRunner(_ssh, targetHost);
            }

            index = await DiagEngine.RunAsync(plan, profileName, targetHost.Name, outDir, fetcher, runner, GetVersion(), DateTimeOffset.UtcNow, cancellationToken);
        }
        catch (RoamException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RoamException(ExitCode.Deploy, "diag", targetHost.Name, ex.Message);
        }
        finally
        {
            sftp?.Dispose();
        }

        RoamLog.Event("diag.end", "diagnostics capture completed", new Dictionary<string, object?>
        {
            ["profile"] = profileName,
            ["artifactCount"] = index.Artifacts.Count,
        });

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }));
        }
        else if (!cli.Quiet)
        {
            Console.WriteLine($"  diag {profileName} → {targetHost.Name}: {index.Artifacts.Count} artifact(s) in {outDir}");
            foreach (var artifact in index.Artifacts)
            {
                Console.WriteLine($"    [{artifact.Kind}] {artifact.LocalPath} ({artifact.Bytes} bytes)");
            }

            if (index.Artifacts.Count == 0)
            {
                Console.WriteLine("    (no artifacts found — check the profile's deploy.diag.logs / detach / crash-dumps config)");
            }
        }

        return new CommandOutcome(ExitCode.Ok);
    }

    // `roam run`: the full pipeline through start/run + ready. Thin wrapper over the shared core.
    public Task<CommandOutcome> RunPipelineAsync(CliOptions cli, string profileName, string? sourceOverride, string? buildOverride, string? targetOverride, CancellationToken cancellationToken)
        => RunPipelineCoreAsync(cli, profileName, sourceOverride, buildOverride, targetOverride, syncOnly: false, cancellationToken);

    // `roam deploy`: sync-only. Runs the shared steps (sync-source -> publish -> stop ->
    // sync-artifacts) and stops — no start/run/ready. For an interactive-session profile it still
    // registers the scheduled task (so an external launcher can `schtasks /Run` it) but does not
    // start it; for a non-interactive profile it is pure byte delivery, launching nothing.
    public Task<CommandOutcome> RunDeployAsync(CliOptions cli, string profileName, string? sourceOverride, string? buildOverride, string? targetOverride, CancellationToken cancellationToken)
        => RunPipelineCoreAsync(cli, profileName, sourceOverride, buildOverride, targetOverride, syncOnly: true, cancellationToken);

    private async Task<CommandOutcome> RunPipelineCoreAsync(CliOptions cli, string profileName, string? sourceOverride, string? buildOverride, string? targetOverride, bool syncOnly, CancellationToken cancellationToken)
    {
        var context = await LoadContextAsync(cli, profileName, cancellationToken);
        var profile = ApplyOverrides(context.Profile, sourceOverride, buildOverride, targetOverride);
        RoamLog.Event("run.context", "loaded roam context", new Dictionary<string, object?>
        {
            ["profile"] = profileName,
            ["roamfile"] = context.RoamfilePath,
            ["project"] = context.ProjectPaths.ProjectPath,
            ["source"] = profile.Source,
            ["build"] = profile.Build,
            ["target"] = profile.Target,
            ["deployPath"] = profile.Deploy.Path,
            ["transfer"] = profile.Deploy.Transfer.ToString(),
            ["runMode"] = profile.Run.Mode.ToString(),
            ["syncOnly"] = syncOnly,
        });
        PreflightProfileExists(context.Roamfile, profileName);
        PreflightHostsDefined(context.Roamfile, profile, profileName);

        var sourceHost = await _ssh.ResolveAsync(profile.Source, context.Roamfile.Hosts[profile.Source], isLocal: true, cancellationToken);
        var buildHost = await _ssh.ResolveAsync(profile.Build, context.Roamfile.Hosts[profile.Build], isLocal: profile.Build == profile.Source, cancellationToken);
        var targetHost = await _ssh.ResolveAsync(profile.Target, context.Roamfile.Hosts[profile.Target], isLocal: profile.Target == profile.Source, cancellationToken);

        var launchProfile = ProjectMetadataResolver.LoadLaunchProfile(context.ProjectPaths, profile.LaunchProfile);
        var publishSettings = ProjectMetadataResolver.ResolvePublishSettings(context.ProjectPaths, profileName, profile);

        var state = new StateStore(context.ProjectPaths.WorkspaceRoot);
        state.EnsureInitialized();

        await RunPreflightAsync(profileName, profile, context.ProjectPaths, launchProfile, publishSettings, sourceHost, buildHost, targetHost, cancellationToken);

        var steps = new List<StepResult>();
        var started = DateTimeOffset.UtcNow;

        // Step denominator for the shared steps (sync-source -> publish -> stop -> sync-artifacts).
        // `roam run` continues to print "of 6" (service: + start + ready; one-shot prints the run
        // step as "5 of 5", historical). `roam deploy` stops after sync-artifacts, so the user sees
        // "of 4" rather than "4 of 6" followed by a silent stop.
        var totalSteps = syncOnly ? 4 : 6;

        try
        {
            if (buildHost.IsLocal)
            {
                RoamLog.Event("step.skip", "sync-source skipped because build host is local", new Dictionary<string, object?> { ["step"] = "sync-source" });
                PrintStep(1, totalSteps, "sync-source", $"{sourceHost.Name} → {buildHost.Name}", TimeSpan.Zero, skipped: true, cli.Quiet);
                steps.Add(new StepResult("sync-source", buildHost.Name, 0, "skipped"));
            }
            else
            {
                RoamLog.Event("step.start", "sync-source starting", new Dictionary<string, object?> { ["step"] = "sync-source", ["host"] = buildHost.Name });
                var syncStopwatch = Stopwatch.StartNew();
                var sourceManifest = await SyncSourceAsync(profileName, context.ProjectPaths, buildHost, state, cancellationToken);
                syncStopwatch.Stop();
                // Saved only on the sync success path; a failed sync throws past here so the manifest
                // is never advanced (see docs/state.md, "Partial failure semantics").
                state.SaveSourceManifest(profileName, sourceManifest);
                PrintStep(1, totalSteps, "sync-source", $"{sourceHost.Name} → {buildHost.Name}", syncStopwatch.Elapsed, false, cli.Quiet);
                steps.Add(new StepResult("sync-source", buildHost.Name, syncStopwatch.ElapsedMilliseconds, "ok"));
            }

            var publishStopwatch = Stopwatch.StartNew();
            RoamLog.Event("step.start", "publish starting", new Dictionary<string, object?> { ["step"] = "publish", ["host"] = buildHost.Name });
            var ciBuild = profile.Source != profile.Build;
            var publishCommand = PublishCommandBuilder.Build(context.ProjectPaths, publishSettings, buildHost.Workspace, ciBuild);
            var publishStatus = await MaybeSkipPublishOrRunAsync(
                profileName,
                context.ProjectPaths,
                publishSettings,
                buildHost,
                publishCommand,
                ciBuild,
                state,
                cancellationToken);
            publishStopwatch.Stop();
            PrintStep(2, totalSteps, "publish", buildHost.Name, publishStopwatch.Elapsed, skipped: publishStatus == "skipped", cli.Quiet);
            steps.Add(new StepResult("publish", buildHost.Name, publishStopwatch.ElapsedMilliseconds, publishStatus));

            var stopStopwatch = Stopwatch.StartNew();
            RoamLog.Event("step.start", "stop starting", new Dictionary<string, object?> { ["step"] = "stop", ["host"] = targetHost.Name });
            var stopStatus = await ExecuteStopAsync(profileName, targetHost, profile, cancellationToken);
            stopStopwatch.Stop();
            PrintStep(3, totalSteps, "stop", targetHost.Name, stopStopwatch.Elapsed, stopStatus == "skipped", cli.Quiet);
            steps.Add(new StepResult("stop", targetHost.Name, stopStopwatch.ElapsedMilliseconds, stopStatus));

            var artifactStopwatch = Stopwatch.StartNew();
            RoamLog.Event("step.start", "sync-artifacts starting", new Dictionary<string, object?> { ["step"] = "sync-artifacts", ["host"] = targetHost.Name });
            var artifactResult = await SyncArtifactsAsync(profileName, context.ProjectPaths, publishSettings, buildHost, targetHost, profile.Deploy, state, cancellationToken);
            artifactStopwatch.Stop();
            // Saved only on the sync success path; a failed sync throws past here so the prior
            // manifest stays the deletion-ownership baseline and the next run re-diffs and converges
            // (see docs/state.md, "Partial failure semantics").
            state.SaveArtifactManifest(profileName, artifactResult.Manifest);
            PrintStep(4, totalSteps, "sync-artifacts", $"{buildHost.Name} → {targetHost.Name}", artifactStopwatch.Elapsed, false, cli.Quiet);
            steps.Add(new StepResult("sync-artifacts", targetHost.Name, artifactStopwatch.ElapsedMilliseconds, "ok"));

            // Deploy-provenance: diff the synced managed-assembly versions against the previous deploy
            // and surface any that did NOT change, then persist the new record. Load the prior manifest
            // BEFORE overwriting it. Best-effort presentation only — never fails the deploy.
            ReportAndSaveProvenance(profileName, artifactResult.Provenance, state, cli.Quiet);

            if (syncOnly)
            {
                // `roam deploy`: no start/run/ready. For an interactive-session profile, register the
                // Roam_<profile> scheduled task WITHOUT starting it, so an external launcher owns start
                // (schtasks /Run) without roam racing it; honors run-level. For a non-interactive
                // profile there is no task to register and nothing should be launched, so do nothing.
                if (profile.Run.InteractiveSession)
                {
                    RoamLog.Event("deploy.register", "registering scheduled task without starting", new Dictionary<string, object?> { ["host"] = targetHost.Name });
                    await ExecuteStartAsync(profileName, targetHost, profile, launchProfile, startTask: false, cancellationToken);
                }
            }
            else if (profile.Run.Mode == RunMode.OneShot)
            {
                var runStopwatch = Stopwatch.StartNew();
                RoamLog.Event("step.start", "run starting", new Dictionary<string, object?> { ["step"] = "run", ["host"] = targetHost.Name, ["mode"] = "one-shot" });
                var runStatus = await ExecuteOneShotAsync(targetHost, profile, launchProfile, cancellationToken);
                runStopwatch.Stop();
                PrintStep(5, 5, "run", targetHost.Name, runStopwatch.Elapsed, false, cli.Quiet);
                steps.Add(new StepResult("run", targetHost.Name, runStopwatch.ElapsedMilliseconds, runStatus));
            }
            else
            {
                var startStopwatch = Stopwatch.StartNew();
                RoamLog.Event("step.start", "start starting", new Dictionary<string, object?> { ["step"] = "start", ["host"] = targetHost.Name, ["mode"] = "service" });
                await ExecuteStartAsync(profileName, targetHost, profile, launchProfile, startTask: true, cancellationToken);
                startStopwatch.Stop();
                PrintStep(5, 6, "start", targetHost.Name, startStopwatch.Elapsed, false, cli.Quiet);
                steps.Add(new StepResult("start", targetHost.Name, startStopwatch.ElapsedMilliseconds, "ok"));

                var readyStopwatch = Stopwatch.StartNew();
                RoamLog.Event("step.start", "ready starting", new Dictionary<string, object?> { ["step"] = "ready", ["host"] = targetHost.Name });
                var readyDetail = await WaitForReadinessAsync(targetHost, profile, cancellationToken);
                readyStopwatch.Stop();
                PrintReady(targetHost.Name, readyDetail, readyStopwatch.Elapsed, cli.Quiet, success: true);
                steps.Add(new StepResult("ready", targetHost.Name, readyStopwatch.ElapsedMilliseconds, "ok"));
            }

            if (!cli.Quiet)
            {
                Console.Out.WriteLine("  Done.");
            }

            state.SaveRunSummary(profileName, new RunSummary(1, profileName, started.ToString("O"), DateTimeOffset.UtcNow.ToString("O"), 0, null, null, GetVersion(), steps));
            return new CommandOutcome(ExitCode.Ok);
        }
        catch (RoamException ex)
        {
            state.SaveRunSummary(profileName, new RunSummary(1, profileName, started.ToString("O"), DateTimeOffset.UtcNow.ToString("O"), (int)ex.ExitCode, ex.Step, ex.Host, GetVersion(), steps));
            throw;
        }
    }

    public async Task<CommandOutcome> RunUninstallAsync(CliOptions cli, string profileName, bool keepManifest, bool dryRun, CancellationToken cancellationToken)
    {
        var context = await LoadContextAsync(cli, profileName, cancellationToken);
        PreflightProfileExists(context.Roamfile, profileName);
        PreflightHostsDefined(context.Roamfile, context.Profile, profileName);

        var targetHost = await _ssh.ResolveAsync(
            context.Profile.Target,
            context.Roamfile.Hosts[context.Profile.Target],
            isLocal: context.Profile.Target == context.Profile.Source,
            cancellationToken);

        var hasCustom = !string.IsNullOrWhiteSpace(context.Profile.Deploy.Uninstall);
        var plan = BuildUninstallPlan(profileName, context.Profile, targetHost, hasCustom);

        RoamLog.Event("uninstall.plan", "uninstall plan resolved", new Dictionary<string, object?>
        {
            ["profile"] = profileName,
            ["host"] = targetHost.Name,
            ["mode"] = hasCustom ? "custom" : "fallback",
            ["dryRun"] = dryRun,
            ["keepManifest"] = keepManifest,
            ["commandCount"] = plan.Count,
        });

        if (!hasCustom && !cli.Quiet)
        {
            Console.Error.WriteLine($"  warning: profile '{profileName}' has no deploy.uninstall block; falling back to stop + remove '{context.Profile.Deploy.Path}' + wipe manifest. Set deploy.uninstall explicitly for tear-down of services, scheduled tasks, firewall rules, etc.");
        }

        if (dryRun)
        {
            PrintUninstallPlan(plan, keepManifest, context.ProjectPaths.WorkspaceRoot, profileName, cli.Quiet);
            return new CommandOutcome(ExitCode.Ok);
        }

        if (!targetHost.IsLocal)
        {
            await ProbeSshAsync(targetHost, cancellationToken);
        }

        var removed = new List<string>();
        var kept = new List<string>();

        foreach (var step in plan)
        {
            await ExecuteUninstallCommandAsync(step.Key, step.Value, targetHost, profileName, cancellationToken);
            removed.Add($"{targetHost.Name}: {step.Key}");
        }

        var state = new StateStore(context.ProjectPaths.WorkspaceRoot);
        if (keepManifest)
        {
            var manifestRoot = System.IO.Path.Combine(state.RootPath, "manifests", profileName);
            if (Directory.Exists(manifestRoot))
            {
                kept.Add($"manifest: {manifestRoot}");
            }
        }
        else
        {
            var removedPath = state.RemoveManifests(profileName);
            if (removedPath is not null)
            {
                removed.Add($"manifest: {removedPath}");
            }
        }

        PrintUninstallSummary(removed, kept, cli.Quiet);
        RoamLog.Event("uninstall.done", "uninstall completed", new Dictionary<string, object?>
        {
            ["profile"] = profileName,
            ["host"] = targetHost.Name,
            ["removed"] = removed,
            ["kept"] = kept,
        });

        return new CommandOutcome(ExitCode.Ok);
    }

    // Renders the concrete shell snippets `roam uninstall` will run on the target. Custom path:
    // one entry, the user's deploy.uninstall block verbatim. Fallback path: an optional stop
    // step (deploy.stop or run.stop, plus the Windows scheduled-task unregister the existing
    // pipeline already knows how to emit for interactive-session profiles) followed by a
    // recursive delete of deploy.path. Each KeyValuePair is (label, script) — the label feeds
    // both the dry-run printout and the "removed:" summary line. KVP rather than a private
    // record so reflection-based tests don't have to traverse nested-private types.
    private static IReadOnlyList<KeyValuePair<string, string>> BuildUninstallPlan(string profileName, ProfileSpec profile, HostResolution targetHost, bool hasCustom)
    {
        var commands = new List<KeyValuePair<string, string>>();

        if (hasCustom)
        {
            commands.Add(new KeyValuePair<string, string>("deploy.uninstall", profile.Deploy.Uninstall!));
            return commands;
        }

        // Fallback. Reuse the stop semantics the pipeline already implements so an
        // interactive-session profile cleans up its scheduled task even without an explicit
        // uninstall block.
        var stopScript = BuildFallbackStopScript(profileName, profile, targetHost);
        if (!string.IsNullOrWhiteSpace(stopScript))
        {
            commands.Add(new KeyValuePair<string, string>("stop process", stopScript));
        }

        commands.Add(new KeyValuePair<string, string>($"remove {profile.Deploy.Path}", BuildRemoveDirectoryScript(profile.Deploy.Path, targetHost)));
        return commands;
    }

    // Mirrors ExecuteStopAsync's command-build logic without the failure semantics — the
    // fallback should be best-effort: a fresh-deploy uninstall must not trip just because the
    // task doesn't exist or the process is already gone. ErrorAction SilentlyContinue and
    // `|| true` carry that intent.
    private static string BuildFallbackStopScript(string profileName, ProfileSpec profile, HostResolution targetHost)
    {
        var userStop = profile.Run.Stop?.Trim();
        var unregisterTask = profile.Run.Mode == RunMode.Service && profile.Run.InteractiveSession && IsWindowsHost(targetHost);

        var script = string.IsNullOrWhiteSpace(userStop) ? string.Empty : userStop!;
        if (unregisterTask)
        {
            var taskName = $"Roam_{SanitizeTaskName(profileName)}";
            if (script.Length > 0 && !script.EndsWith(";", StringComparison.Ordinal))
            {
                script += ';';
            }
            script += $"Unregister-ScheduledTask -TaskName {ToPowerShellLiteral(taskName)} -Confirm:$false -ErrorAction SilentlyContinue";
        }

        return script;
    }

    private static string BuildRemoveDirectoryScript(string deployPath, HostResolution targetHost)
    {
        if (IsWindowsHost(targetHost))
        {
            return $"Remove-Item -Recurse -Force -LiteralPath {ToPowerShellLiteral(deployPath)} -ErrorAction SilentlyContinue";
        }

        return $"rm -rf {ProcessRunner.ShellQuote(deployPath)}";
    }

    private async Task ExecuteUninstallCommandAsync(string label, string script, HostResolution targetHost, string profileName, CancellationToken cancellationToken)
    {
        RoamLog.Event("uninstall.command", "uninstall command starting", new Dictionary<string, object?>
        {
            ["profile"] = profileName,
            ["host"] = targetHost.Name,
            ["label"] = label,
            ["command"] = script,
        });

        ProcessResult result;
        if (targetHost.IsLocal)
        {
            result = await ProcessRunner.RunBashAsync(script, cancellationToken: cancellationToken);
        }
        else
        {
            result = await RunSshAsync(targetHost, script, cancellationToken);
        }

        RoamLog.Event("uninstall.command.end", "uninstall command exited", new Dictionary<string, object?>
        {
            ["profile"] = profileName,
            ["host"] = targetHost.Name,
            ["label"] = label,
            ["exitCode"] = result.ExitCode,
        });

        if (result.ExitCode != 0)
        {
            var detail = BestErrorLine(result.StdErr) ?? BestErrorLine(result.StdOut) ?? "uninstall command failed";
            throw new RoamException(ExitCode.Deploy, "uninstall", targetHost.Name, $"uninstall step '{label}' failed: {detail}");
        }
    }

    private static void PrintUninstallPlan(IReadOnlyList<KeyValuePair<string, string>> plan, bool keepManifest, string workspaceRoot, string profileName, bool quiet)
    {
        if (quiet)
        {
            return;
        }

        Console.Out.WriteLine("  dry run — no commands executed.");
        foreach (var step in plan)
        {
            Console.Out.WriteLine($"  would run [{step.Key}]:");
            foreach (var line in step.Value.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                Console.Out.WriteLine($"      {line.TrimEnd('\r')}");
            }
        }

        var manifestPath = System.IO.Path.Combine(workspaceRoot, ".roam", "manifests", profileName);
        if (keepManifest)
        {
            Console.Out.WriteLine($"  would keep manifest: {manifestPath}");
        }
        else
        {
            Console.Out.WriteLine($"  would remove manifest: {manifestPath}");
        }
    }

    private static void PrintUninstallSummary(IReadOnlyList<string> removed, IReadOnlyList<string> kept, bool quiet)
    {
        if (quiet)
        {
            return;
        }

        Console.Out.WriteLine($"  removed: {(removed.Count == 0 ? "(nothing)" : string.Join(", ", removed))}");
        if (kept.Count > 0)
        {
            Console.Out.WriteLine($"  kept:    {string.Join(", ", kept)}");
        }
    }

    private static void EnsureGitIgnoreHasRoam(string workspaceRoot)
    {
        var gitIgnorePath = Path.Combine(workspaceRoot, ".gitignore");
        var trackedResult = ProcessRunner.RunBashAsync($"cd {ProcessRunner.ShellQuote(workspaceRoot)} && git ls-files --error-unmatch .roam/", cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
        if (trackedResult.ExitCode == 0)
        {
            throw new RoamException(ExitCode.Config, "parse", "local", ".roam/ is tracked by git; remove it from the index before running roam init");
        }

        var existing = File.Exists(gitIgnorePath) ? File.ReadAllLines(gitIgnorePath).ToList() : [];
        if (!existing.Any(line => string.Equals(line.Trim(), ".roam/", StringComparison.Ordinal)))
        {
            existing.Add(".roam/");
            File.WriteAllLines(gitIgnorePath, existing);
        }
    }

    private async Task<ExecutionContext> LoadContextAsync(CliOptions cli, string profileName, CancellationToken cancellationToken)
    {
        var roamfilePath = ConfigLoader.Discover(cli.RoamfilePath, Directory.GetCurrentDirectory());
        var roamfile = ConfigLoader.Load(roamfilePath);
        PreflightProfileExists(roamfile, profileName);
        var profile = roamfile.Profiles[profileName];
        var projectPaths = ProjectMetadataResolver.ResolveProjectPaths(roamfile, roamfilePath);
        return new ExecutionContext(roamfilePath, roamfile, profile, projectPaths);
    }

    private static ProfileSpec ApplyOverrides(ProfileSpec profile, string? sourceOverride, string? buildOverride, string? targetOverride)
        => profile with
        {
            Source = sourceOverride ?? profile.Source,
            Build = buildOverride ?? profile.Build,
            Target = targetOverride ?? profile.Target,
        };

    private static void PreflightProfileExists(Roamfile roamfile, string profileName)
    {
        if (!roamfile.Profiles.ContainsKey(profileName))
        {
            var known = string.Join(", ", roamfile.Profiles.Keys.OrderBy(x => x, StringComparer.Ordinal));
            throw new RoamException(ExitCode.Preflight, "preflight", "local", $"profile '{profileName}' is not defined in roamfile.yaml (known profiles: {known})");
        }
    }

    private static void PreflightHostsDefined(Roamfile roamfile, ProfileSpec profile, string profileName)
    {
        foreach (var hostName in new[] { profile.Source, profile.Build, profile.Target })
        {
            if (!roamfile.Hosts.ContainsKey(hostName))
            {
                throw new RoamException(ExitCode.Preflight, "preflight", "local", $"profile '{profileName}' references host '{hostName}' which is not defined in roamfile.yaml");
            }
        }
    }

    private static void ValidateDebugPrerequisites(string profileName, ProfileSpec profile)
    {
        if (!profile.Debug.Enabled)
        {
            throw new RoamException(ExitCode.Preflight, "preflight", "local", $"profile '{profileName}' has debug.enabled: false; roam attach has nothing to emit");
        }

        if (!string.Equals(profile.Debug.Editor, "vscode", StringComparison.OrdinalIgnoreCase))
        {
            throw new RoamException(ExitCode.Preflight, "preflight", "local", $"profile '{profileName}' uses debug.editor='{profile.Debug.Editor}'; v0 only supports 'vscode'");
        }

        if (!string.Equals(profile.Debug.Debugger, "vsdbg", StringComparison.OrdinalIgnoreCase))
        {
            throw new RoamException(ExitCode.Preflight, "preflight", "local", $"profile '{profileName}' uses debug.debugger='{profile.Debug.Debugger}'; v0 only supports 'vsdbg'");
        }
    }

    private async Task RunPreflightAsync(
        string profileName,
        ProfileSpec profile,
        ResolvedProjectPaths projectPaths,
        LaunchProfileInfo launchProfile,
        ResolvedPublishSettings publishSettings,
        HostResolution sourceHost,
        HostResolution buildHost,
        HostResolution targetHost,
        CancellationToken cancellationToken)
    {
        // Windows-source/build guards intentionally bypassed for this spike;
        // platform-readiness.md still flags this matrix cell as untested.

        // Catch a publish RID that names a different OS than the target host before spending a
        // publish + sync on binaries that can only fail at `start` (a Windows apphost won't run on
        // Linux and vice versa). Pure check, so it runs first — no SSH round-trip wasted.
        var ridMismatch = RuntimeCompatibility.ValidatePublishOsTargetsHost(publishSettings.RuntimeIdentifier, targetHost.Os);
        if (ridMismatch is not null)
        {
            throw new RoamException(ExitCode.Preflight, "preflight", targetHost.Name, ridMismatch);
        }

        if (!buildHost.IsLocal)
        {
            await ProbeSshAsync(buildHost, cancellationToken);
        }

        if (!targetHost.IsLocal)
        {
            await ProbeSshAsync(targetHost, cancellationToken);
        }

        await EnsureDotnetAsync(buildHost, cancellationToken);
        await EnsureWorkspaceUsableAsync(buildHost, cancellationToken);
        await EnsureDeployPathWritableAsync(targetHost, profile.Deploy.Path, cancellationToken);
        await EnsureTargetRuntimeAsync(targetHost, publishSettings, projectPaths, cancellationToken);

        _ = launchProfile;

        if (profile.Debug.Enabled)
        {
            _ = profile.Debug.ProcessName ?? throw new RoamException(ExitCode.Preflight, "preflight", "local", $"profile '{profileName}' must set debug.process-name when debug.enabled is true");
            Directory.CreateDirectory(Path.Combine(projectPaths.WorkspaceRoot, ".vscode"));
        }

        if (!File.Exists(projectPaths.ProjectPath))
        {
            throw new RoamException(ExitCode.Preflight, "preflight", sourceHost.Name, $"project file '{projectPaths.ProjectPath}' does not exist");
        }
    }

    private async Task ProbeSshAsync(HostResolution host, CancellationToken cancellationToken)
    {
        var result = await RunSshAsync(host, BuildProbeCommand(host), cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new RoamException(ExitCode.Preflight, "preflight", host.Name, $"ssh to {host.Name} failed: {FirstMeaningfulLine(result.StdErr) ?? "authentication rejected"}");
        }
    }

    private async Task EnsureDotnetAsync(HostResolution buildHost, CancellationToken cancellationToken)
    {
        ProcessResult result;
        if (buildHost.IsLocal)
        {
            result = await ProcessRunner.RunAsync("dotnet", "--version", cancellationToken: cancellationToken);
        }
        else
        {
            result = await RunSshAsync(buildHost, "dotnet --version", cancellationToken);
        }

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
        {
            throw new RoamException(ExitCode.Preflight, "preflight", buildHost.Name, $"build host '{buildHost.Name}' has no dotnet on PATH");
        }

        var versionText = FirstMeaningfulLine(result.StdOut) ?? string.Empty;
        // dotnet --version can return SemVer like "10.0.300-preview.0.26177.108"; System.Version
        // rejects the pre-release suffix, so strip everything past the first '-' before parsing.
        var numericVersion = versionText.Split('-', 2)[0];
        if (!Version.TryParse(numericVersion, out var version) || version < new Version(10, 0, 100))
        {
            throw new RoamException(ExitCode.Preflight, "preflight", buildHost.Name, $"build host '{buildHost.Name}' has dotnet {versionText}; roam requires >= 10.0.100");
        }
    }

    private async Task EnsureWorkspaceUsableAsync(HostResolution buildHost, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(buildHost.Workspace))
        {
            return;
        }

        if (buildHost.IsLocal)
        {
            Directory.CreateDirectory(buildHost.Workspace!);
            return;
        }

        var command = $"mkdir -p {ProcessRunner.ShellQuote(buildHost.Workspace!)} && test -d {ProcessRunner.ShellQuote(buildHost.Workspace!)} && test -w {ProcessRunner.ShellQuote(buildHost.Workspace!)}";
        var result = await RunSshAsync(buildHost, command, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new RoamException(ExitCode.Preflight, "preflight", buildHost.Name, $"workspace '{buildHost.Workspace}' on {buildHost.Name} is not writable by user '{buildHost.User}'");
        }
    }

    private async Task EnsureDeployPathWritableAsync(HostResolution targetHost, string deployPath, CancellationToken cancellationToken)
    {
        var marker = $".roam-preflight-{Environment.ProcessId}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        var command = BuildEnsureDeployWritableCommand(targetHost, deployPath, marker);
        ProcessResult result;
        if (targetHost.IsLocal)
        {
            result = await ProcessRunner.RunBashAsync(command, cancellationToken: cancellationToken);
        }
        else
        {
            result = await RunSshAsync(targetHost, command, cancellationToken);
        }

        if (result.ExitCode != 0)
        {
            var detail = FirstMeaningfulLine(result.StdErr) ?? FirstMeaningfulLine(result.StdOut) ?? $"exit={result.ExitCode}";
            throw new RoamException(ExitCode.Preflight, "preflight", targetHost.Name, $"deploy path '{deployPath}' on {targetHost.Name} is not writable by user '{targetHost.User}' ({detail})");
        }
    }

    // For framework-dependent publishes, confirm the target actually has a compatible shared
    // runtime before we ship ~50 MB of app over the wire only to have `start` fail. Deliberately
    // lenient: a confident mismatch (the target has dotnet but no matching major) hard-fails with
    // an actionable message, but anything we can't determine (no TFM, dotnet not on the target's
    // PATH, unparseable output) only warns — an apphost can still locate a runtime the muxer can't.
    private async Task EnsureTargetRuntimeAsync(
        HostResolution targetHost,
        ResolvedPublishSettings publishSettings,
        ResolvedProjectPaths projectPaths,
        CancellationToken cancellationToken)
    {
        if (publishSettings.SelfContained || targetHost.IsLocal)
        {
            return;
        }

        var targetFramework = publishSettings.TargetFramework ?? ProjectMetadataResolver.ReadTargetFramework(projectPaths);
        var required = RuntimeCompatibility.ParseTargetFrameworkVersion(targetFramework);
        if (required is null)
        {
            Console.Error.WriteLine($"  warning: could not determine the target framework for a framework-dependent publish; skipping runtime preflight on {targetHost.Name}.");
            return;
        }

        var result = await RunSshAsync(targetHost, "dotnet --list-runtimes", cancellationToken);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
        {
            Console.Error.WriteLine($"  warning: could not verify the .NET {required.Major}.{required.Minor} runtime on {targetHost.Name} (framework-dependent publish); proceeding.");
            return;
        }

        var installed = RuntimeCompatibility.ParseInstalledRuntimes(result.StdOut);
        if (!RuntimeCompatibility.IsCompatible(required, installed))
        {
            var found = installed.Count == 0 ? "none" : string.Join(", ", installed.Select(version => version.ToString()));
            throw new RoamException(
                ExitCode.Preflight,
                "preflight",
                targetHost.Name,
                $"target '{targetHost.Name}' has no .NET {required.Major}.{required.Minor} runtime for this framework-dependent publish (Microsoft.NETCore.App found: {found}); install the runtime on the target or set publish.self-contained: true");
        }
    }

    private async Task<SyncManifest> SyncSourceAsync(string profileName, ResolvedProjectPaths paths, HostResolution buildHost, StateStore state, CancellationToken cancellationToken)
    {
        var trackedFiles = await GetTrackedFilesAsync(paths.WorkspaceRoot, cancellationToken);
        var gitHead = (await ProcessRunner.RunBashAsync($"cd {ProcessRunner.ShellQuote(paths.WorkspaceRoot)} && git rev-parse HEAD", cancellationToken: cancellationToken)).StdOut.Trim();

        using ISyncTarget target = buildHost.IsLocal
            ? new LocalSyncTarget()
            : new SftpSyncTarget(buildHost);

        try
        {
            return await MetadataDiffSyncEngine.SyncAsync(
                profileName,
                paths.WorkspaceRoot,
                buildHost.Workspace!,
                state.LoadSourceManifest(profileName),
                target,
                profileName,
                buildHost.Name,
                null,
                buildHost.Workspace,
                null,
                false,
                gitHead,
                cancellationToken,
                trackedFiles);
        }
        catch (RoamException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RoamException(ExitCode.Sync, "sync-source", buildHost.Name, ex.Message);
        }
    }

    // Decides whether the publish step can be skipped on this run and, if not, executes the
    // already-built publish command. Skip-eligible when (a) the build host is local — verifying
    // the publish output on a remote build host requires an SSH round-trip and v0 keeps the skip
    // local-only — AND (b) the cached publish manifest's fingerprint matches the current inputs
    // AND (c) the publish output directory still has at least one file (a stale cache survives
    // user-initiated `bin/`-deletes by re-publishing). When publish runs successfully, the new
    // fingerprint is persisted so the next run can short-circuit.
    private async Task<string> MaybeSkipPublishOrRunAsync(
        string profileName,
        ResolvedProjectPaths paths,
        ResolvedPublishSettings publishSettings,
        HostResolution buildHost,
        string publishCommand,
        bool ciBuild,
        StateStore state,
        CancellationToken cancellationToken)
    {
        var fingerprint = await PublishFingerprint.ComputeAsync(paths, publishSettings, publishCommand, ciBuild, cancellationToken);

        RoamLog.Event("publish.fingerprint", "publish fingerprint computed", new Dictionary<string, object?>
        {
            ["profile"] = profileName,
            ["fingerprint"] = fingerprint.Fingerprint,
            ["schema"] = fingerprint.SchemaVersion,
            ["inputCount"] = fingerprint.Inputs.Count,
        });

        if (TrySkipPublish(profileName, paths, publishSettings, buildHost, fingerprint, state, out var skipReason))
        {
            RoamLog.Event("publish.skip", "publish skipped: cached fingerprint matches", new Dictionary<string, object?>
            {
                ["profile"] = profileName,
                ["fingerprint"] = fingerprint.Fingerprint,
                ["reason"] = skipReason,
            });
            return "skipped";
        }

        RoamLog.Event("publish.command", "publish command built", new Dictionary<string, object?>
        {
            ["host"] = buildHost.Name,
            ["isLocal"] = buildHost.IsLocal,
            ["publishDirectory"] = publishSettings.PublishDirectory,
            ["command"] = publishCommand,
            ["skipDecision"] = skipReason,
        });

        await ExecutePublishAsync(publishCommand, paths, buildHost, cancellationToken);

        var manifest = new PublishManifest(
            PublishFingerprint.FingerprintSchemaVersion,
            profileName,
            fingerprint.Fingerprint,
            buildHost.Name,
            publishSettings.PublishDirectory,
            DateTimeOffset.UtcNow.ToString("O"),
            fingerprint.Inputs);
        state.SavePublishManifest(profileName, manifest);
        return "ok";
    }

    // The skip is gated on every condition that could make a cache hit unsafe: remote build
    // host (we'd need SSH to verify the publish output), schema mismatch (the manifest format
    // changed and re-running publish is the only way to rewrite it correctly), fingerprint
    // mismatch, or a missing/empty publish output directory (a `rm -rf bin/` between runs).
    // Every guard emits a `reason` for logging; the caller passes the reason through.
    private static bool TrySkipPublish(
        string profileName,
        ResolvedProjectPaths paths,
        ResolvedPublishSettings publishSettings,
        HostResolution buildHost,
        PublishFingerprintResult fingerprint,
        StateStore state,
        out string reason)
    {
        if (!buildHost.IsLocal)
        {
            reason = "remote-build";
            return false;
        }

        var previous = state.LoadPublishManifest(profileName);
        if (previous is null)
        {
            reason = "no-previous-manifest";
            return false;
        }

        if (previous.Schema != fingerprint.SchemaVersion)
        {
            reason = $"schema-mismatch(previous={previous.Schema},current={fingerprint.SchemaVersion})";
            return false;
        }

        if (!string.Equals(previous.Fingerprint, fingerprint.Fingerprint, StringComparison.Ordinal))
        {
            reason = "fingerprint-mismatch";
            return false;
        }

        var publishRoot = Path.GetFullPath(publishSettings.PublishDirectory, paths.ProjectDirectory);
        if (!Directory.Exists(publishRoot))
        {
            reason = $"publish-output-missing({publishRoot})";
            return false;
        }

        if (!Directory.EnumerateFileSystemEntries(publishRoot).Any())
        {
            reason = $"publish-output-empty({publishRoot})";
            return false;
        }

        reason = "fingerprint-match";
        return true;
    }

    private async Task ExecutePublishAsync(string commandText, ResolvedProjectPaths paths, HostResolution buildHost, CancellationToken cancellationToken)
    {
        ProcessResult result;
        if (buildHost.IsLocal)
        {
            result = await ProcessRunner.RunBashAsync(commandText, workingDirectory: paths.WorkspaceRoot, cancellationToken: cancellationToken);
        }
        else
        {
            result = await RunSshAsync(buildHost, commandText, cancellationToken);
        }

        if (result.ExitCode != 0)
        {
            var detail = BestErrorLine(result.StdErr) ?? BestErrorLine(result.StdOut) ?? "dotnet publish failed";
            throw new RoamException(ExitCode.Publish, "publish", buildHost.Name, detail);
        }
    }

    private async Task<string> ExecuteStopAsync(string profileName, HostResolution targetHost, ProfileSpec profile, CancellationToken cancellationToken)
    {
        var userStop = profile.Run.Stop?.Trim();
        var unregisterTask = profile.Run.Mode == RunMode.Service && profile.Run.InteractiveSession && IsWindowsHost(targetHost);
        RoamLog.Event("deploy.stop.command", "stop command prepared", new Dictionary<string, object?>
        {
            ["host"] = targetHost.Name,
            ["hasUserStop"] = !string.IsNullOrWhiteSpace(userStop),
            ["unregisterTask"] = unregisterTask,
        });

        if (string.IsNullOrWhiteSpace(userStop) && !unregisterTask)
        {
            return "skipped";
        }

        var script = string.IsNullOrWhiteSpace(userStop) ? string.Empty : userStop!;
        if (unregisterTask)
        {
            var taskName = $"Roam_{SanitizeTaskName(profileName)}";
            // PowerShell statement separator. Use SilentlyContinue so a fresh deploy (no
            // pre-existing task) doesn't trip the outer try/catch in BuildWindowsRemoteCommand.
            if (script.Length > 0 && !script.EndsWith(";", StringComparison.Ordinal))
            {
                script += ';';
            }
            script += $"Unregister-ScheduledTask -TaskName {ToPowerShellLiteral(taskName)} -Confirm:$false -ErrorAction SilentlyContinue";
        }

        ProcessResult result;
        if (targetHost.IsLocal)
        {
            result = await ProcessRunner.RunBashAsync(script, cancellationToken: cancellationToken);
        }
        else
        {
            result = await RunSshAsync(targetHost, script, cancellationToken);
        }

        if (result.ExitCode != 0)
        {
            var detail = BestErrorLine(result.StdErr) ?? BestErrorLine(result.StdOut) ?? "stop command failed";
            throw new RoamException(ExitCode.Deploy, "stop", targetHost.Name, detail);
        }

        return "ok";
    }

    // startTask=true (roam run): register the scheduled task and Start-ScheduledTask it (today's
    // behavior). startTask=false (roam deploy): register the task but omit the trailing
    // Start-ScheduledTask, so an external launcher owns start. Only meaningful for an
    // interactive-session profile on a Windows target; otherwise the wrapper has no task to start.
    private async Task ExecuteStartAsync(string profileName, HostResolution targetHost, ProfileSpec profile, LaunchProfileInfo launchProfile, bool startTask, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profile.Run.Command))
        {
            return;
        }

        var env = new Dictionary<string, string>(launchProfile.EnvironmentVariables, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in profile.Env)
        {
            env[pair.Key] = pair.Value;
        }

        // Agent-first diagnostics (ADR-0002): opt-in crash-dump env so the runtime writes a minidump
        // to <deploy.path>/.roam-diag/dumps/ that `roam diag --dump` can fetch. No-op when unset.
        foreach (var pair in DiagPlanner.CrashDumpEnv(profile.Deploy.Path, profile.Deploy.Diag))
        {
            env[pair.Key] = pair.Value;
        }

        var command = BuildStartCommand(targetHost, env, profile.Run.Command, profile.Run.InteractiveSession, profile.Run.InteractiveSessionTrigger, profile.Run.RunLevel, profileName, profile.Deploy.Path, startTask, profile.Run.Detach);
        RoamLog.Event("deploy.start.command", "start command prepared", new Dictionary<string, object?>
        {
            ["host"] = targetHost.Name,
            ["interactiveSession"] = profile.Run.InteractiveSession,
            ["interactiveSessionTrigger"] = profile.Run.InteractiveSessionTrigger.ToString(),
            ["runLevel"] = profile.Run.RunLevel.ToString(),
            ["startTask"] = startTask,
            ["envCount"] = env.Count,
            ["command"] = command,
        });
        ProcessResult result;
        if (targetHost.IsLocal)
        {
            result = await ProcessRunner.RunBashAsync(command, cancellationToken: cancellationToken);
        }
        else
        {
            result = await RunSshAsync(targetHost, command, cancellationToken);
        }

        if (result.ExitCode != 0)
        {
            var detail = BestErrorLine(result.StdErr) ?? BestErrorLine(result.StdOut) ?? "start command failed";
            throw new RoamException(ExitCode.Deploy, "start", targetHost.Name, detail);
        }
    }

    private async Task<string> ExecuteOneShotAsync(HostResolution targetHost, ProfileSpec profile, LaunchProfileInfo launchProfile, CancellationToken cancellationToken)
    {
        var env = new Dictionary<string, string>(launchProfile.EnvironmentVariables, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in profile.Env)
        {
            env[pair.Key] = pair.Value;
        }

        var command = BuildRunCommand(targetHost, env, profile.Run.Command!);
        RoamLog.Event("run.oneshot.command", "one-shot command prepared", new Dictionary<string, object?>
        {
            ["host"] = targetHost.Name,
            ["timeoutSeconds"] = profile.Run.TimeoutSeconds,
            ["successExitCodes"] = profile.Run.SuccessExitCodes,
            ["command"] = command,
        });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(profile.Run.TimeoutSeconds));

        ProcessResult result;
        try
        {
            result = targetHost.IsLocal
                ? await ProcessRunner.RunBashAsync(command, cancellationToken: timeout.Token)
                : await RunSshAsync(targetHost, command, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RoamException(ExitCode.Deploy, "run", targetHost.Name, $"one-shot command timed out after {profile.Run.TimeoutSeconds}s");
        }

        RoamLog.Event("run.oneshot.exit", "one-shot command exited", new Dictionary<string, object?>
        {
            ["host"] = targetHost.Name,
            ["exitCode"] = result.ExitCode,
            ["stdoutBytes"] = result.StdOut.Length,
            ["stderrBytes"] = result.StdErr.Length,
            ["stdout"] = Truncate(result.StdOut, 16 * 1024),
            ["stderr"] = Truncate(result.StdErr, 16 * 1024),
        });

        if (!profile.Run.SuccessExitCodes.Contains(result.ExitCode))
        {
            var detail = BestErrorLine(result.StdErr) ?? BestErrorLine(result.StdOut) ?? $"exit={result.ExitCode}";
            throw new RoamException(ExitCode.Deploy, "run", targetHost.Name, $"one-shot command failed with exit={result.ExitCode}: {detail}");
        }

        return $"exit={result.ExitCode}";
    }

    private async Task<string> WaitForReadinessAsync(HostResolution targetHost, ProfileSpec profile, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(profile.Run.Ready))
        {
            return await PollCommandAsync(targetHost, profile.Run.Ready!, profile.Run.ReadyTimeoutSeconds, profile.Run.ReadyIntervalMilliseconds, "ready", cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(profile.Debug.ProcessName))
        {
            return "skipped";
        }

        var detail = await PollCommandAsync(targetHost, BuildDefaultReadyCommand(targetHost, profile.Debug.ProcessName!), profile.Run.ReadyTimeoutSeconds, profile.Run.ReadyIntervalMilliseconds, "ready", cancellationToken);
        return $"({detail})";
    }

    private async Task<string> PollCommandAsync(HostResolution targetHost, string command, int timeoutSeconds, int intervalMs, string step, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        await Task.Delay(500, cancellationToken);
        var attempt = 0;
        while (DateTimeOffset.UtcNow <= deadline)
        {
            attempt++;
            ProcessResult result;
            if (targetHost.IsLocal)
            {
                result = await ProcessRunner.RunBashAsync(command, cancellationToken: cancellationToken);
            }
            else
            {
                result = await RunSshAsync(targetHost, command, cancellationToken);
            }

            if (result.ExitCode == 0)
            {
                RoamLog.Event("ready.success", "ready command succeeded", new Dictionary<string, object?>
                {
                    ["host"] = targetHost.Name,
                    ["attempt"] = attempt,
                    ["stdout"] = FirstMeaningfulLine(result.StdOut),
                });
                return FirstMeaningfulLine(result.StdOut) ?? "ready";
            }

            RoamLog.Event("ready.retry", "ready command not ready", new Dictionary<string, object?>
            {
                ["host"] = targetHost.Name,
                ["attempt"] = attempt,
                ["exitCode"] = result.ExitCode,
                ["stdout"] = FirstMeaningfulLine(result.StdOut),
                ["stderr"] = FirstMeaningfulLine(result.StdErr),
            });

            await Task.Delay(intervalMs, cancellationToken);
        }

        throw new RoamException(ExitCode.Ready, step, targetHost.Name, $"timed out after {timeoutSeconds}s");
    }

    private async Task<ArtifactSyncResult> SyncArtifactsAsync(string profileName, ResolvedProjectPaths paths, ResolvedPublishSettings publishSettings, HostResolution buildHost, HostResolution targetHost, DeploySpec deploy, StateStore state, CancellationToken cancellationToken)
    {
        var localPublishRoot = buildHost.IsLocal
            ? Path.GetFullPath(publishSettings.PublishDirectory, paths.ProjectDirectory)
            : await MaterializeRemotePublishAsync(buildHost, publishSettings, paths, cancellationToken);

        RoamLog.Event("sync.artifacts.roots", "artifact sync roots resolved", new Dictionary<string, object?>
        {
            ["localPublishRoot"] = localPublishRoot,
            ["buildHost"] = buildHost.Name,
            ["targetHost"] = targetHost.Name,
            ["deployPath"] = deploy.Path,
            ["flattenPublish"] = deploy.FlattenPublish,
            ["transfer"] = deploy.Transfer.ToString(),
        });

        try
        {
            var sourceRoot = deploy.FlattenPublish
                ? localPublishRoot
                : Directory.GetParent(localPublishRoot)?.FullName ?? localPublishRoot;

            var remoteBasePath = deploy.FlattenPublish
                ? deploy.Path
                : CombineRemotePath(targetHost, deploy.Path, Path.GetFileName(localPublishRoot));

            using ISyncTarget target = targetHost.IsLocal
                ? new LocalSyncTarget()
                : new SftpSyncTarget(targetHost, new SshCommandRunner(_ssh, targetHost), deploy.Transfer == SyncTransferMode.Archive);

            var manifest = await MetadataDiffSyncEngine.SyncAsync(
                profileName,
                sourceRoot,
                remoteBasePath,
                state.LoadArtifactManifest(profileName),
                target,
                null,
                buildHost.Name,
                targetHost.Name,
                null,
                deploy.Path,
                deploy.FlattenPublish,
                null,
                cancellationToken);

            // Scan managed-assembly provenance while the publish payload is still on disk (a remote-
            // build temp dir is deleted in the finally below). Reuses the content hashes the sync
            // manifest just computed; never throws past here (a metadata read failure yields no row).
            var provenance = DeployProvenance.Scan(
                manifest.Entries,
                localPublishRoot,
                deploy.FlattenPublish,
                deploy.Provenance,
                paths.ProjectName);

            return new ArtifactSyncResult(manifest, provenance);
        }
        catch (RoamException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RoamException(ExitCode.Sync, "sync-artifacts", targetHost.Name, ex.Message);
        }
        finally
        {
            if (!buildHost.IsLocal && Directory.Exists(localPublishRoot))
            {
                Directory.Delete(localPublishRoot, true);
            }
        }
    }

    private async Task<string> MaterializeRemotePublishAsync(HostResolution buildHost, ResolvedPublishSettings publishSettings, ResolvedProjectPaths paths, CancellationToken cancellationToken)
    {
        var projectDirectoryRelative = Path.GetRelativePath(paths.WorkspaceRoot, paths.ProjectDirectory).Replace('\\', '/');
        var remotePublishRoot = CombineUnixPath(buildHost.Workspace!, CombineUnixPath(projectDirectoryRelative, publishSettings.PublishDirectory.TrimEnd('/')));
        var localTempRoot = Path.Combine(Path.GetTempPath(), $"roam-publish-{Guid.NewGuid():N}");
        Directory.CreateDirectory(localTempRoot);

        await SftpDirectoryDownloader.DownloadDirectoryAsync(buildHost, remotePublishRoot, localTempRoot, cancellationToken);
        return localTempRoot;
    }

    private static async Task<IReadOnlyList<RemoteFileEntry>> GetTrackedFilesAsync(string workspaceRoot, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunBashAsync($"cd {ProcessRunner.ShellQuote(workspaceRoot)} && git ls-files -z", cancellationToken: cancellationToken);
        result.EnsureSuccess("git ls-files failed");

        var files = result.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(relative =>
            {
                var fullPath = Path.Combine(workspaceRoot, relative);
                var info = new FileInfo(fullPath);
                return new RemoteFileEntry(relative.Replace('\\', '/'), info.Length, info.LastWriteTimeUtc);
            })
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .ToArray();

        return files;
    }

    // Runs a command on a remote host via `ssh` with argv-passed arguments, so no local shell (bash
    // or pwsh) re-parses the payload — the SSH-command counterpart to RunBashAsync for local
    // commands, and the fix for the Windows-controller transport quoting bug.
    private Task<ProcessResult> RunSshAsync(HostResolution host, string command, CancellationToken cancellationToken)
        => ProcessRunner.RunProcessAsync("ssh", _ssh.BuildSshArgs(host, command), cancellationToken: cancellationToken);

    // Drives a shell command on a remote host through the same OS-aware SSH wrapping the rest of
    // the pipeline uses. Handed to SftpSyncTarget so the archive transport can run a remote tar.
    private sealed class SshCommandRunner(SshHostResolver ssh, HostResolution host) : IRemoteCommandRunner
    {
        public Task<ProcessResult> RunAsync(string command, CancellationToken cancellationToken)
            => ProcessRunner.RunProcessAsync("ssh", ssh.BuildSshArgs(host, command), cancellationToken: cancellationToken);
    }

    // Runs a diag capture locally (IsLocal target — target == source); same shape as
    // SshCommandRunner but with no SSH wrapping.
    private sealed class LocalCommandRunner : IRemoteCommandRunner
    {
        public Task<ProcessResult> RunAsync(string command, CancellationToken cancellationToken)
            => ProcessRunner.RunBashAsync(command, cancellationToken: cancellationToken);
    }

    // deployed-versions.json schema. Bump if the manifest shape changes; an unreadable/old file is
    // already treated as "no prior deploy", so a bump just shows every assembly as new for one run.
    private const int ProvenanceManifestSchema = 1;

    // Persists the deploy-provenance manifest and prints a compact version diff against the previous
    // deploy, highlighting assemblies whose version AND bytes did not change (the "the new behavior
    // didn't ship because the package was stale" red flag). Best-effort: any IO failure here is
    // swallowed so this cosmetic, after-the-fact step never fails an otherwise-successful deploy.
    private static void ReportAndSaveProvenance(string profileName, IReadOnlyList<DeployedAssembly> provenance, StateStore state, bool quiet)
    {
        if (provenance.Count == 0)
        {
            return;
        }

        DeployedVersionsManifest? previous;
        try
        {
            previous = state.LoadDeployedVersionsManifest(profileName);
        }
        catch
        {
            previous = null;
        }

        var current = new DeployedVersionsManifest(
            ProvenanceManifestSchema,
            profileName,
            DateTimeOffset.UtcNow.ToString("O"),
            provenance);

        if (!quiet)
        {
            PrintProvenanceDiff(DeployProvenance.Diff(previous, current));
        }

        RoamLog.Event("deploy.provenance", "deployed assembly versions recorded", new Dictionary<string, object?>
        {
            ["profile"] = profileName,
            ["assemblyCount"] = provenance.Count,
            ["assemblies"] = provenance.Select(a => new Dictionary<string, object?>
            {
                ["path"] = a.Path,
                ["informationalVersion"] = a.InformationalVersion,
                ["fileVersion"] = a.FileVersion,
                ["assemblyVersion"] = a.AssemblyVersion,
                ["contentHash"] = a.ContentHash,
            }).ToArray(),
        });

        try
        {
            state.SaveDeployedVersionsManifest(profileName, current);
        }
        catch (Exception ex)
        {
            RoamLog.Event("deploy.provenance.save_failed", "could not persist deployed-versions manifest", new Dictionary<string, object?>
            {
                ["profile"] = profileName,
                ["error"] = ex.Message,
            });
        }
    }

    private static void PrintProvenanceDiff(IReadOnlyList<ProvenanceDiffLine> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        var nameWidth = Math.Min(40, lines.Max(l => l.Name.Length));
        var beforeWidth = Math.Min(28, lines.Max(l => l.Before.Length));

        Console.Out.WriteLine("  deployed versions:");
        foreach (var line in lines)
        {
            var after = line.Unchanged ? "(unchanged)" : line.After;
            Console.Out.WriteLine($"    {line.Name.PadRight(nameWidth)}  {line.Before.PadRight(beforeWidth)}  ->  {after}");
        }
    }

    private static void PrintStep(int index, int total, string name, string subject, TimeSpan elapsed, bool skipped, bool quiet)
    {
        if (quiet)
        {
            return;
        }

        var status = skipped ? $"[{index}/{total}]" : $"[{index}/{total}]";
        Console.Out.WriteLine($"  {status} {name,-15} {subject,-24} {elapsed.TotalSeconds:0.0}s");
    }

    private static void PrintReady(string host, string detail, TimeSpan elapsed, bool quiet, bool success)
    {
        if (quiet)
        {
            return;
        }

        var glyph = success ? "[✓]" : "[✗]";
        Console.Out.WriteLine($"  {glyph}   {"ready",-15} {host,-12} {detail,-12} {elapsed.TotalSeconds:0.0}s");
    }

    private static string GetVersion()
        => VersionInfo.Current;

    // Thin wrappers over the SshOutputLines pure core (the single source of truth for line
    // selection, shared with SshHostResolver/SyncEngine). Both strip benign ssh noise first so a
    // routine warning never masks the real failure (charles8051/roam#7). FirstMeaningfulLine takes
    // the first meaningful line; BestErrorLine prefers an error-marked line over banner output and
    // falls back to the last meaningful line.
    private static string? FirstMeaningfulLine(string text)
        => SshOutputLines.FirstMeaningful(text);

    private static string? BestErrorLine(string text)
        => SshOutputLines.BestError(text);

    private static string BuildProbeCommand(HostResolution host)
        => IsWindowsHost(host) ? "exit 0" : "true";

    private static string BuildEnsureDeployWritableCommand(HostResolution host, string deployPath, string marker)
    {
        var markerPath = CombineRemotePath(host, deployPath, marker);
        if (IsWindowsHost(host))
        {
            return $"New-Item -ItemType Directory -Force -Path {ToPowerShellLiteral(deployPath)} | Out-Null; New-Item -ItemType File -Force -Path {ToPowerShellLiteral(markerPath)} | Out-Null; Remove-Item -Force {ToPowerShellLiteral(markerPath)}";
        }

        return $"mkdir -p {ProcessRunner.ShellQuote(deployPath)} && touch {ProcessRunner.ShellQuote(markerPath)} && rm -f {ProcessRunner.ShellQuote(markerPath)}";
    }

    private static string BuildDefaultReadyCommand(HostResolution host, string processName)
    {
        if (!IsWindowsHost(host))
        {
            return $"pgrep -x {ProcessRunner.ShellQuote(processName)} | head -n 1";
        }

        var normalized = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;
        return $"$proc = Get-Process -Name {ToPowerShellLiteral(normalized)} -ErrorAction Stop | Select-Object -First 1; $proc.Id";
    }

    private static string BuildStartCommand(HostResolution host, IEnumerable<KeyValuePair<string, string>> variables, string? startCommand, bool interactiveSession, InteractiveSessionTrigger interactiveSessionTrigger, RunLevel runLevel, string profileName, string deployPath, bool startTask = true, bool detach = false)
    {
        startCommand ??= string.Empty;
        if (!IsWindowsHost(host))
        {
            var inline = ProcessRunner.BuildEnvironmentPrefix(variables) + startCommand;

            // Opt-in Unix durability (deploy/run `detach: true`). `roam run` starts the service over
            // an SSH channel that closes when the start step returns, so an inline foreground process
            // dies with it. nohup ignores SIGHUP; redirecting all three std streams frees the channel
            // so SSH doesn't block waiting on them; `&` backgrounds it -- so the service outlives the
            // deploy. POSIX-only (Linux + macOS; no setsid dependency). Skipped for `roam deploy`
            // register-without-start (startTask=false). Reboot durability is a separate systemd story.
            if (detach && startTask)
            {
                var logPath = $"{deployPath.Replace('\\', '/').TrimEnd('/')}/roam-{SanitizeTaskName(profileName)}.out";
                return $"nohup sh -c {ProcessRunner.ShellQuote(inline)} < /dev/null > {ProcessRunner.ShellQuote(logPath)} 2>&1 &";
            }

            return inline;
        }

        var inner = new StringBuilder();
        foreach (var pair in variables)
        {
            inner.Append("$env:")
                .Append(pair.Key)
                .Append('=')
                .Append(ToPowerShellLiteral(pair.Value))
                .Append(';');
        }

        inner.Append(startCommand);

        if (!interactiveSession)
        {
            return inner.ToString();
        }

        // Windows GUI ceremony: launching via SSH lands the process in session 0 with no
        // display, so an Avalonia/WPF app either fails to render or exits immediately.
        // Wrap the start command in a Scheduled Task with LogonType Interactive so the
        // task action runs inside the target user's desktop session. This mirrors the
        // approach a hand-written kiosk-deploy script would take.
        //
        // The task action runs the env-injection + start command from a STAGED .ps1 file
        // (`powershell.exe -File`), NOT an inline `-EncodedCommand` blob. Issue #10: an inline
        // `-EncodedCommand` is base64, and roam's SSH transport
        // (SshHostResolver.BuildWindowsRemoteCommand) base64-encodes the *whole wrapper* a
        // SECOND time. That double encode is ~7x the inner script's size (each layer is utf-16
        // -> base64 ~2.7x, applied twice), so a profile with ~15 env vars pushed the remote
        // command-line past cmd.exe's ~8191-char limit; cmd.exe truncated it, the outer base64
        // decoded to a half-script with an unterminated `try {`, and PowerShell died with
        // MissingEndCurlyBrace -- AFTER the stop step had already killed the workload, leaving
        // the target down with no task registered. Staging the inner script as a single literal
        // keeps it to ONE transport encode (~2.7x), well under the limit, and is the
        // cmd.exe-EncodedCommand-limit cure documented in docs/powershell-5.1-over-ssh.md (#4).
        var taskName = $"Roam_{SanitizeTaskName(profileName)}";
        var startScriptPath = $"{deployPath.Replace('\\', '/').TrimEnd('/')}/.roam-start-{taskName}.ps1";
        var wrapper = new StringBuilder();
        wrapper.Append("Unregister-ScheduledTask -TaskName ").Append(ToPowerShellLiteral(taskName)).Append(" -Confirm:$false -ErrorAction SilentlyContinue;\n");

        // Stage the start script as a single-quoted PowerShell string literal (ToPowerShellLiteral
        // doubles any embedded ' -- the same escaping the rest of the wrapper uses). The $env:
        // assignments, the call operator, single-quoted paths, and the `*>` redirect are written
        // VERBATIM; nothing in the inner command is re-parsed or interpolated by the wrapper
        // (issue #10's failing shape). A single-quoted literal -- unlike an @'..'@ here-string --
        // has no in-band terminator, so an inner line that happens to look like a here-string
        // close ('@) or contains quotes cannot break out of (or inject into) the wrapper. The file
        // is written UTF-8 *with BOM* so Windows PowerShell 5.1 -- which reads a BOM-less .ps1 as
        // Windows-1252 -- keeps any non-ASCII env values intact (docs/powershell-5.1-over-ssh.md #3).
        wrapper.Append("$startScript = ").Append(ToPowerShellLiteral(inner.ToString())).Append(";\n");
        wrapper.Append("[System.IO.File]::WriteAllText(").Append(ToPowerShellLiteral(startScriptPath)).Append(", $startScript, (New-Object System.Text.UTF8Encoding $true));\n");

        wrapper.Append("$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ");
        wrapper.Append(ToPowerShellLiteral($"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{startScriptPath}\""));
        wrapper.Append(" -WorkingDirectory ").Append(ToPowerShellLiteral(deployPath)).Append(";\n");
        var runLevelArg = runLevel == RunLevel.Highest ? "Highest" : "Limited";
        wrapper.Append("$principal = New-ScheduledTaskPrincipal -UserId ").Append(ToPowerShellLiteral(host.User)).Append(" -LogonType Interactive -RunLevel ").Append(runLevelArg).Append(";\n");
        wrapper.Append("$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Hours 12);\n");

        // Opt-in reboot durability: without a trigger the task is action + principal only and stays
        // down after a reboot until the next deploy. An -AtLogOn trigger bound to the same user as
        // the principal relaunches the workload on the next logon (autologon station).
        var triggerArg = string.Empty;
        if (interactiveSessionTrigger == InteractiveSessionTrigger.AtLogon)
        {
            wrapper.Append("$trigger = New-ScheduledTaskTrigger -AtLogOn -User ").Append(ToPowerShellLiteral(host.User)).Append(";\n");
            triggerArg = " -Trigger $trigger";
        }

        wrapper.Append("Register-ScheduledTask -TaskName ").Append(ToPowerShellLiteral(taskName)).Append(" -Action $action -Principal $principal -Settings $settings").Append(triggerArg).Append(" -Force | Out-Null;\n");

        // Non-destructive guard (issue #10): the stop step has already killed the workload, so a
        // start that fails MUST NOT leave the box down with no relaunch task. Registration runs
        // BEFORE the Start below and we verify it landed -- so once an at-logon trigger is set the
        // relaunch task exists even if Start-ScheduledTask later throws, and the station recovers
        // on next logon rather than staying dark. If registration itself failed, throw here (the
        // transport's outer try/catch maps it to a non-zero start exit) instead of the historical
        // silent "registered nothing, started nothing, reported success".
        wrapper.Append("if (-not (Get-ScheduledTask -TaskName ").Append(ToPowerShellLiteral(taskName)).Append(" -ErrorAction SilentlyContinue)) { throw ").Append(ToPowerShellLiteral($"interactive-session task {taskName} failed to register")).Append(" };\n");

        // `roam deploy` registers the task but hands start to an external launcher, so it omits ONLY
        // this trailing Start-ScheduledTask. `roam run` (startTask=true) keeps it.
        if (startTask)
        {
            wrapper.Append("Start-ScheduledTask -TaskName ").Append(ToPowerShellLiteral(taskName));
        }

        return wrapper.ToString();
    }

    private static string BuildRunCommand(HostResolution host, IEnumerable<KeyValuePair<string, string>> variables, string command)
    {
        if (!IsWindowsHost(host))
        {
            return ProcessRunner.BuildEnvironmentPrefix(variables) + command;
        }

        var builder = new StringBuilder();
        foreach (var pair in variables)
        {
            builder.Append("$env:")
                .Append(pair.Key)
                .Append('=')
                .Append(ToPowerShellLiteral(pair.Value))
                .Append(';');
        }

        builder.Append(command);
        return builder.ToString();
    }

    private static string Truncate(string value, int maxChars)
        => value.Length <= maxChars ? value : value[..maxChars] + $"\n... truncated {value.Length - maxChars} chars";

    private static string SanitizeTaskName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_');
        }
        return builder.Length == 0 ? "default" : builder.ToString();
    }

    private static bool IsWindowsHost(HostResolution host)
        => string.Equals(host.Os, "windows", StringComparison.OrdinalIgnoreCase);

    private static string CombineRemotePath(HostResolution host, string left, string right)
    {
        if (string.IsNullOrWhiteSpace(right))
        {
            return left;
        }

        if (IsWindowsHost(host))
        {
            return $"{left.TrimEnd('\\', '/') }\\{right.TrimStart('\\', '/').Replace('/', '\\')}";
        }

        return CombineUnixPath(left, right);
    }

    private static string ToPowerShellLiteral(string value)
        => $"'{value.Replace("'", "''")}'";

    private static string CombineUnixPath(string left, string right)
    {
        // Path.GetRelativePath returns "." when the two paths are equal — combining that as
        // a path segment yields `left/.` which breaks VSCode sourceFileMap prefix-matching
        // (PDB paths don't start with `/.`). Treat "." (and "./") as empty.
        if (string.IsNullOrWhiteSpace(right) || right == "." || right == "./")
        {
            return left;
        }

        return $"{left.TrimEnd('/')}/{right.TrimStart('/')}";
    }

    private sealed record ExecutionContext(
        string RoamfilePath,
        Roamfile Roamfile,
        ProfileSpec Profile,
        ResolvedProjectPaths ProjectPaths);
}
