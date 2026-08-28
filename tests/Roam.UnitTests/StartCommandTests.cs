using System.Diagnostics;
using System.Reflection;
using Xunit;

namespace Roam.UnitTests;

// Reflects into the private static RoamCommands.BuildStartCommand to lock the Windows
// interactive-session scheduled-task wrapper. The wrapper is a pure function of
// (host, env, startCommand, interactiveSession, trigger, runLevel, profileName, deployPath), so
// it's the right unit to assert PowerShell-quoting, the opt-in reboot-durability trigger, the
// non-destructive registration ordering, and the integrity level without an SSH-reachable
// Windows target.
public sealed class StartCommandTests
{
    private static readonly IReadOnlyList<KeyValuePair<string, string>> NoEnv = [];

    // Golden shape (issue #10). The task action runs the env-injection + start command from a
    // STAGED .ps1 (`powershell.exe -File`) that the wrapper writes from a single-quoted string
    // literal, rather than from an inline `-EncodedCommand` blob. The inline form was base64,
    // and the SSH transport base64-encodes the whole wrapper a second time -- that double encode
    // pushed a real env-heavy profile past cmd.exe's command-line limit, truncating the payload
    // into a half-script (MissingEndCurlyBrace). Staging keeps it to a single transport encode.
    // Existing interactive-session profiles depend on the task still running the same env and
    // start command -- which it does, now from the staged file.
    [Fact]
    public void BuildStartCommand_WindowsInteractiveNoTrigger_StagesScriptAndRegisters()
    {
        var host = WindowsTarget("kiosk");
        var script = InvokeBuildStartCommand(host, NoEnv, "Start-Process foo.exe", interactiveSession: true, InteractiveSessionTrigger.None, RunLevel.Limited, "Kiosk-Profile", "C:/app");

        var expected =
            "Unregister-ScheduledTask -TaskName 'Roam_Kiosk-Profile' -Confirm:$false -ErrorAction SilentlyContinue;\n" +
            "$startScript = 'Start-Process foo.exe';\n" +
            "[System.IO.File]::WriteAllText('C:/app/.roam-start-Roam_Kiosk-Profile.ps1', $startScript, (New-Object System.Text.UTF8Encoding $true));\n" +
            "$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument " +
            "'-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"C:/app/.roam-start-Roam_Kiosk-Profile.ps1\"'" +
            " -WorkingDirectory 'C:/app';\n" +
            "$principal = New-ScheduledTaskPrincipal -UserId 'kiosk' -LogonType Interactive -RunLevel Limited;\n" +
            "$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Hours 12);\n" +
            "Register-ScheduledTask -TaskName 'Roam_Kiosk-Profile' -Action $action -Principal $principal -Settings $settings -Force | Out-Null;\n" +
            "if (-not (Get-ScheduledTask -TaskName 'Roam_Kiosk-Profile' -ErrorAction SilentlyContinue)) { throw 'interactive-session task Roam_Kiosk-Profile failed to register' };\n" +
            "Start-ScheduledTask -TaskName 'Roam_Kiosk-Profile'";

        Assert.Equal(expected, script);
        Assert.DoesNotContain("Trigger", script);
        Assert.DoesNotContain("-EncodedCommand", script);  // the double-encode footgun is gone
    }

    // Opt-in: at-logon adds a New-ScheduledTaskTrigger -AtLogOn bound to the same user as the
    // principal and threads -Trigger $trigger into the register call, so the task relaunches the
    // workload on the next logon after a reboot.
    [Fact]
    public void BuildStartCommand_WindowsInteractiveAtLogon_EmitsAtLogonTrigger()
    {
        var host = WindowsTarget("kiosk");
        var script = InvokeBuildStartCommand(host, NoEnv, "Start-Process foo.exe", interactiveSession: true, InteractiveSessionTrigger.AtLogon, RunLevel.Limited, "Kiosk-Profile", "C:/app");

        Assert.Contains("$trigger = New-ScheduledTaskTrigger -AtLogOn -User 'kiosk';", script);
        Assert.Contains("-Settings $settings -Trigger $trigger -Force | Out-Null;", script);
    }

    // The trigger is meaningless without the scheduled-task wrapper: a non-interactive start emits a
    // plain inline command regardless of the trigger value.
    [Fact]
    public void BuildStartCommand_NonInteractive_IgnoresTrigger()
    {
        var host = WindowsTarget("kiosk");
        var script = InvokeBuildStartCommand(host, NoEnv, "Start-Process foo.exe", interactiveSession: false, InteractiveSessionTrigger.AtLogon, RunLevel.Limited, "Kiosk-Profile", "C:/app");

        Assert.Equal("Start-Process foo.exe", script);
        Assert.DoesNotContain("ScheduledTask", script);
    }

    // Default run-level Limited registers the principal with -RunLevel Limited (the historical IL),
    // so a roam-deployed interactive-session workload stays non-elevated unless opted up.
    [Fact]
    public void BuildStartCommand_WindowsInteractiveDefaultRunLevel_EmitsLimited()
    {
        var host = WindowsTarget("kiosk");
        var script = InvokeBuildStartCommand(host, NoEnv, "Start-Process foo.exe", interactiveSession: true, InteractiveSessionTrigger.None, RunLevel.Limited, "Kiosk-Profile", "C:/app");

        Assert.Contains("-LogonType Interactive -RunLevel Limited;", script);
        Assert.DoesNotContain("-RunLevel Highest", script);
    }

    // Opt-in: run-level highest registers the principal with -RunLevel Highest so the
    // interactive-session task runs elevated (High IL). Only the integrity level changes -- the
    // task still starts (Start-ScheduledTask present) and carries no trigger unless one is set.
    [Fact]
    public void BuildStartCommand_WindowsInteractiveHighestRunLevel_EmitsHighest()
    {
        var host = WindowsTarget("kiosk");
        var script = InvokeBuildStartCommand(host, NoEnv, "Start-Process foo.exe", interactiveSession: true, InteractiveSessionTrigger.None, RunLevel.Highest, "Kiosk-Profile", "C:/app");

        Assert.Contains("-LogonType Interactive -RunLevel Highest;", script);
        Assert.DoesNotContain("-RunLevel Limited", script);
        Assert.Contains("Start-ScheduledTask -TaskName 'Roam_Kiosk-Profile'", script);
    }

    // `roam deploy` register-without-start: startTask=false omits ONLY the trailing
    // Start-ScheduledTask while still emitting Unregister/stage/Action/Principal/Settings/Register
    // and the post-register verification, so an external launcher owns start. run-level is honored.
    [Fact]
    public void BuildStartCommand_StartTaskFalse_RegistersWithoutStarting()
    {
        var host = WindowsTarget("kiosk");
        var script = InvokeBuildStartCommand(host, NoEnv, "Start-Process foo.exe", interactiveSession: true, InteractiveSessionTrigger.None, RunLevel.Highest, "Kiosk-Profile", "C:/app", startTask: false);

        Assert.Contains("Unregister-ScheduledTask -TaskName 'Roam_Kiosk-Profile'", script);
        Assert.Contains("Register-ScheduledTask -TaskName 'Roam_Kiosk-Profile'", script);
        Assert.Contains("-LogonType Interactive -RunLevel Highest;", script);
        Assert.DoesNotContain("Start-ScheduledTask", script);
        // The register-verification guard is the tail when start is omitted.
        Assert.EndsWith("-ErrorAction SilentlyContinue)) { throw 'interactive-session task Roam_Kiosk-Profile failed to register' };\n", script);
    }

    // startTask=true is the default: register, verify, THEN Start-ScheduledTask.
    [Fact]
    public void BuildStartCommand_StartTaskTrue_StartsTask()
    {
        var host = WindowsTarget("kiosk");
        var script = InvokeBuildStartCommand(host, NoEnv, "Start-Process foo.exe", interactiveSession: true, InteractiveSessionTrigger.None, RunLevel.Limited, "Kiosk-Profile", "C:/app", startTask: true);

        Assert.EndsWith("Start-ScheduledTask -TaskName 'Roam_Kiosk-Profile'", script);
    }

    // Non-destructive ordering (issue #10): the stop step has already killed the workload by the
    // time start runs, so registration of the relaunch task MUST precede (and gate) Start. Assert
    // Register -> verify -> Start, with NO Unregister between Register and Start -- so a Start that
    // throws still leaves the at-logon relaunch task in place rather than a dark box with no task.
    [Fact]
    public void BuildStartCommand_RegistersAndVerifiesBeforeStarting()
    {
        var host = WindowsTarget("kiosk");
        var script = InvokeBuildStartCommand(host, NoEnv, "Start-Process foo.exe", interactiveSession: true, InteractiveSessionTrigger.AtLogon, RunLevel.Limited, "Kiosk-Profile", "C:/app");

        var registerIdx = script.IndexOf("Register-ScheduledTask", StringComparison.Ordinal);
        var verifyIdx = script.IndexOf("if (-not (Get-ScheduledTask", StringComparison.Ordinal);
        var startIdx = script.IndexOf("Start-ScheduledTask", StringComparison.Ordinal);

        Assert.True(registerIdx >= 0 && verifyIdx > registerIdx && startIdx > verifyIdx,
            "expected order: Register-ScheduledTask -> verify guard -> Start-ScheduledTask");

        // No Unregister between Register and Start: a failed Start must not tear down the task it
        // just registered (the relaunch task is the recovery path on next logon).
        var between = script.Substring(registerIdx, startIdx - registerIdx);
        Assert.DoesNotContain("Unregister-ScheduledTask", between);
    }

    // Root-cause regression for issue #10: the failing field profile injected ~15 env vars and a
    // start using the call operator + a `*>` redirect + single-quoted paths. With the old inline
    // `-EncodedCommand` task action, the SSH transport's second base64 encode pushed the remote
    // command-line past cmd.exe's ~8191-char limit; cmd.exe truncated it and PowerShell reported
    // MissingEndCurlyBrace. Run the generated start command through roam's REAL transport
    // (SshHostResolver.BuildSshArgs -> BuildWindowsRemoteCommand) and assert the transported
    // command-line stays comfortably under the limit.
    [Fact]
    public void BuildStartCommand_HeavyEnvProfile_TransportStaysUnderCmdLimit()
    {
        const int CmdLineLimit = 8191;
        var host = WindowsTarget("deploy");
        var script = InvokeBuildStartCommand(host, LargeEnvBlock(), ElevatedStartCommand, interactiveSession: true, InteractiveSessionTrigger.AtLogon, RunLevel.Highest, "agent-elevated", "C:/app/agent");

        var args = new SshHostResolver().BuildSshArgs(host, script);
        var remoteCommandLine = args[^1];

        Assert.True(remoteCommandLine.Length < CmdLineLimit,
            $"transported remote command-line is {remoteCommandLine.Length} chars; cmd.exe truncates past {CmdLineLimit} (issue #10).");
    }

    // The generated start command must be syntactically valid PowerShell for the exact shape that
    // failed in the field -- call operator + `*>` redirect + single-quoted paths + env injection,
    // under interactive-session + at-logon. Round-trips the command through PowerShell's own parser
    // ([Parser]::ParseInput) and asserts zero parse errors. Self-skips when no PowerShell is on
    // PATH (mirrors the ComposeLab self-skip); CI runners (windows/ubuntu/macos) all ship pwsh.
    [Fact]
    public void BuildStartCommand_FailingFieldShape_ParsesAsPowerShell()
    {
        var host = WindowsTarget("deploy");
        var script = InvokeBuildStartCommand(host, LargeEnvBlock(), ElevatedStartCommand, interactiveSession: true, InteractiveSessionTrigger.AtLogon, RunLevel.Highest, "agent-elevated", "C:/app/agent");

        var (ran, errorCount, firstError) = TryPowerShellParse(script);
        if (!ran)
        {
            return;  // no pwsh/powershell on PATH -- skip (CI always has one).
        }

        Assert.True(errorCount == 0, $"generated start command failed to parse ({errorCount} error(s)): {firstError}");
    }

    // The staged inner script (the .ps1 the task action runs via -File) must itself parse for the
    // failing field shape -- the call operator + `*>` redirect + single-quoted paths + env prefix.
    [Fact]
    public void BuildStartCommand_StagedInnerScript_ParsesAsPowerShell()
    {
        var host = WindowsTarget("deploy");
        var script = InvokeBuildStartCommand(host, LargeEnvBlock(), ElevatedStartCommand, interactiveSession: true, InteractiveSessionTrigger.AtLogon, RunLevel.Highest, "agent-elevated", "C:/app/agent");

        var inner = ExtractStagedInnerScript(script);
        Assert.Contains(ElevatedStartCommand, inner);            // the verbatim start command rode through
        Assert.Contains("$env:APP_INSTANCE_ID=", inner);    // env prefix present

        var (ran, errorCount, firstError) = TryPowerShellParse(inner);
        if (!ran)
        {
            return;
        }

        Assert.True(errorCount == 0, $"staged inner script failed to parse ({errorCount} error(s)): {firstError}");
    }

    // The staged inner script is a single-quoted literal, NOT an @'..'@ here-string, so content
    // that would terminate a here-string (a line that is just `'@`) or that contains quotes cannot
    // break out of the wrapper or inject code. A start command crafted to look like a here-string
    // close + an injected command must (a) keep the wrapper parsing and (b) round-trip verbatim
    // into the staged script -- i.e. be inert data, not executed by the wrapper.
    [Fact]
    public void BuildStartCommand_InnerLooksLikeHereStringClose_IsInertNotInjected()
    {
        var host = WindowsTarget("kiosk");
        var malicious = "Start-Process foo.exe\n'@\nWrite-Host pwned\n& 'C:/x.exe'";
        var script = InvokeBuildStartCommand(host, NoEnv, malicious, interactiveSession: true, InteractiveSessionTrigger.None, RunLevel.Limited, "Kiosk-Profile", "C:/app");

        // The crafted payload survives verbatim inside the staged literal (escaped, not executed).
        Assert.Equal(malicious, ExtractStagedInnerScript(script));

        var (ran, errorCount, firstError) = TryPowerShellParse(script);
        if (ran)
        {
            Assert.True(errorCount == 0, $"wrapper failed to parse with here-string-like inner ({errorCount}): {firstError}");
        }
    }

    private const string ElevatedStartCommand =
        "& 'C:/app/agent/ExampleAgent.exe' *> 'C:/app/agent/agent.log'";

    // A ~15-var env block of realistic shape -- endpoints, tokens, GUIDs, paths -- sized to match
    // what a real service configuration looks like (15 vars, ~840 bytes of keys plus values). The
    // size is the point: it is what reproduces the cmd.exe command-line overflow that the old
    // double-encode caused. The names are deliberately generic; nothing here depends on them.
    private static IReadOnlyList<KeyValuePair<string, string>> LargeEnvBlock() =>
    [
        new("APP_INSTANCE_ID", "instance-elevated-primary-01"),
        new("APP_CONTROL_ENDPOINT", "https://control-plane.staging.example.com:8443/agent/v2/register"),
        new("APP_CONTROL_TOKEN", "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.aGVsbG8td29ybGQtdG9rZW4tcGF5bG9hZA"),
        new("APP_TLS_THUMBPRINT", "A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4"),
        new("APP_INSTANCE_GUID", "5f3e9b2a-7c1d-4e8a-9f0b-1a2c3d4e5f60"),
        new("APP_DEPLOY_PROFILE", "example-deploy-profile-primary-elevated"),
        new("DOTNET_ENVIRONMENT", "Staging"),
        new("APP_LOG_DIR", "C:/ProgramData/ExampleAgent/logs"),
        new("APP_DATA_DIR", "C:/ProgramData/ExampleAgent/data"),
        new("APP_PIPE_NAME", "example-agent-ipc-elevated"),
        new("APP_HEARTBEAT_SECONDS", "30"),
        new("APP_UPDATE_FEED", "https://packages.staging.example.com/nuget/v3/index.json"),
        new("APP_FEATURE_FLAGS", "provisioning=off;telemetry=on;crashdumps=on;selfupdate=off"),
        new("APP_OPERATOR", "example-staging-rollout-operator-account-504"),
        new("ASPNETCORE_URLS", "http://127.0.0.1:5099"),
    ];

    // Pull the staged inner script (a single-quoted PowerShell string literal) out of the wrapper
    // and un-double the escaped quotes, recovering the exact bytes that get written to the .ps1.
    private static string ExtractStagedInnerScript(string script)
    {
        const string open = "$startScript = '";
        const string close = "';\n[System.IO.File]::WriteAllText(";
        var start = script.IndexOf(open, StringComparison.Ordinal);
        Assert.True(start >= 0, "wrapper did not stage a start script");
        start += open.Length;
        var end = script.IndexOf(close, start, StringComparison.Ordinal);
        Assert.True(end > start, "staged start-script literal was not terminated");
        return script[start..end].Replace("''", "'");
    }

    // Round-trip a script through PowerShell's own parser. Returns ran=false (test self-skips) when
    // neither pwsh nor powershell is on PATH. The candidate is passed via a temp file + env var so
    // no shell re-parses it. Output line 1 is the parse-error count; line 2 (if any) the first msg.
    private static (bool ran, int errorCount, string firstError) TryPowerShellParse(string candidate)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"roam-parse-{Guid.NewGuid():N}.txt");
        File.WriteAllText(tmp, candidate);
        try
        {
            foreach (var exe in new[] { "pwsh", "powershell" })
            {
                var psi = new ProcessStartInfo(exe)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(
                    "$errs=$null; $src=[System.IO.File]::ReadAllText($env:ROAM_PARSE_FILE); " +
                    "[System.Management.Automation.Language.Parser]::ParseInput($src,[ref]$null,[ref]$errs)|Out-Null; " +
                    "Write-Output $errs.Count; if($errs.Count -gt 0){ Write-Output $errs[0].Message }");
                psi.Environment["ROAM_PARSE_FILE"] = tmp;

                Process? process;
                try
                {
                    process = Process.Start(psi);
                }
                catch
                {
                    continue;  // exe not found -- try the next candidate.
                }

                if (process is null)
                {
                    continue;
                }

                using (process)
                {
                    var stdout = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (lines.Length == 0 || !int.TryParse(lines[0], out var count))
                    {
                        continue;
                    }
                    return (true, count, count > 0 && lines.Length > 1 ? lines[1] : string.Empty);
                }
            }

            return (false, 0, string.Empty);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best-effort temp cleanup */ }
        }
    }

    private static HostResolution WindowsTarget(string user) => new(
        "target", "target", user, 22, null, [], null, null, "windows", false);

    private static string InvokeBuildStartCommand(
        HostResolution host,
        IEnumerable<KeyValuePair<string, string>> variables,
        string? startCommand,
        bool interactiveSession,
        InteractiveSessionTrigger interactiveSessionTrigger,
        RunLevel runLevel,
        string profileName,
        string deployPath,
        bool startTask = true,
        bool detach = false)
    {
        var method = typeof(RoamCommands).GetMethod(
            "BuildStartCommand",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        return (string)method!.Invoke(null, [host, variables, startCommand, interactiveSession, interactiveSessionTrigger, runLevel, profileName, deployPath, startTask, detach])!;
    }

    private static HostResolution LinuxTarget(string user) => new(
        "target", "target", user, 22, null, [], null, null, "linux", false);

    // Unix durability opt-out (default): detach false runs the start inline -- env prefix + the
    // author's command, no backgrounding. Byte-for-byte today's non-Windows behavior.
    [Fact]
    public void BuildStartCommand_LinuxNoDetach_RunsInline()
    {
        var script = InvokeBuildStartCommand(LinuxTarget("svc"), NoEnv, "dotnet App.dll", interactiveSession: false, InteractiveSessionTrigger.None, RunLevel.Limited, "linuxsvc", "/opt/app", startTask: true, detach: false);

        Assert.Equal("dotnet App.dll", script);
        Assert.DoesNotContain("nohup", script);
    }

    // Opt-in: detach true on a Unix target wraps `roam run`'s start in a backgrounded nohup so the
    // service survives the SSH channel close. The env prefix rides inside the single-quoted inner
    // script; all three std streams are redirected (so SSH doesn't block) and the job backgrounds.
    [Fact]
    public void BuildStartCommand_LinuxDetach_WrapsInBackgroundedNohup()
    {
        var env = new[] { new KeyValuePair<string, string>("ASPNETCORE_URLS", "http://127.0.0.1:5099") };
        var script = InvokeBuildStartCommand(LinuxTarget("svc"), env, "dotnet App.dll", interactiveSession: false, InteractiveSessionTrigger.None, RunLevel.Limited, "linuxsvc", "/opt/app", startTask: true, detach: true);

        Assert.StartsWith("nohup sh -c ", script);
        Assert.Contains("< /dev/null", script);
        Assert.EndsWith("2>&1 &", script);
        Assert.Contains("/opt/app/roam-linuxsvc.out", script);
        Assert.Contains("ASPNETCORE_URLS=", script);  // POSIX env prefix inside the inner script
        Assert.Contains("dotnet App.dll", script);
    }

    // detach must NOT start anything for `roam deploy` register-without-start (startTask=false):
    // nothing to background, so it falls back to the inline form.
    [Fact]
    public void BuildStartCommand_LinuxDetachButRegisterOnly_RunsInline()
    {
        var script = InvokeBuildStartCommand(LinuxTarget("svc"), NoEnv, "dotnet App.dll", interactiveSession: false, InteractiveSessionTrigger.None, RunLevel.Limited, "linuxsvc", "/opt/app", startTask: false, detach: true);

        Assert.Equal("dotnet App.dll", script);
        Assert.DoesNotContain("nohup", script);
    }

    // detach is a Unix concept; on a Windows target it is ignored (Windows durability is the
    // interactive-session scheduled task). A non-interactive Windows start stays a plain command.
    [Fact]
    public void BuildStartCommand_WindowsTarget_IgnoresDetach()
    {
        var script = InvokeBuildStartCommand(WindowsTarget("svc"), NoEnv, "Start-Process foo.exe", interactiveSession: false, InteractiveSessionTrigger.None, RunLevel.Limited, "Svc", "C:/app", startTask: true, detach: true);

        Assert.Equal("Start-Process foo.exe", script);
        Assert.DoesNotContain("nohup", script);
    }
}
