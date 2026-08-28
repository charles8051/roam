using System.Reflection;
using Xunit;

namespace Roam.UnitTests;

public sealed class UninstallTests
{
    // The custom-uninstall path: the user's deploy.uninstall block is the single shell snippet
    // to run, with no fallback stop or directory-removal interpolation. The label round-trips
    // verbatim so the summary line ("removed: target: deploy.uninstall") names the same thing
    // the operator wrote.
    [Fact]
    public void BuildUninstallPlan_PassesCustomUninstallBlockThrough()
    {
        var profile = MinimalProfile() with
        {
            Deploy = MinimalDeploy() with { Uninstall = "systemctl --user stop kiosk-ui; rm -rf /opt/kiosk" },
        };
        var target = LinuxTarget();

        var plan = InvokeBuildUninstallPlan("kiosk", profile, target, hasCustom: true);

        var single = Assert.Single(plan.RemoteCommands);
        Assert.Equal("deploy.uninstall", single.Label);
        Assert.Equal(profile.Deploy.Uninstall, single.Script);
    }

    // Linux fallback: stop block (if any) followed by `rm -rf` of deploy.path. Quoting must
    // use ShellQuote so an exotic path like `/tmp/with spaces` survives.
    [Fact]
    public void BuildUninstallPlan_LinuxFallback_StopThenRemoveDeployPath()
    {
        var profile = MinimalProfile() with
        {
            Deploy = MinimalDeploy() with
            {
                Path = "/opt/roam-fixture",
                Stop = "pkill -f '[R]oam.SampleApp' || true",
            },
            Run = MinimalRun() with { Stop = "pkill -f '[R]oam.SampleApp' || true" },
        };
        var target = LinuxTarget();

        var plan = InvokeBuildUninstallPlan("demo", profile, target, hasCustom: false);

        Assert.Equal(2, plan.RemoteCommands.Count);
        Assert.Equal("stop process", plan.RemoteCommands[0].Label);
        Assert.Contains("pkill", plan.RemoteCommands[0].Script);
        Assert.Equal("remove /opt/roam-fixture", plan.RemoteCommands[1].Label);
        Assert.Equal("rm -rf '/opt/roam-fixture'", plan.RemoteCommands[1].Script);
    }

    // Windows fallback: PowerShell Remove-Item with -LiteralPath + SilentlyContinue so a
    // missing dir doesn't trip BuildWindowsRemoteCommand's outer try/catch.
    [Fact]
    public void BuildUninstallPlan_WindowsFallback_UsesPowerShellRemoveItem()
    {
        var profile = MinimalProfile() with
        {
            Deploy = MinimalDeploy() with { Path = "C:/Users/kiosk/app" },
        };
        var target = WindowsTarget();

        var plan = InvokeBuildUninstallPlan("demo", profile, target, hasCustom: false);

        var lastScript = plan.RemoteCommands[^1].Script;
        Assert.Contains("Remove-Item", lastScript);
        Assert.Contains("-Recurse", lastScript);
        Assert.Contains("-Force", lastScript);
        Assert.Contains("-LiteralPath 'C:/Users/kiosk/app'", lastScript);
        Assert.Contains("SilentlyContinue", lastScript);
    }

    // Windows interactive-session profiles register a Roam_<profile> scheduled task at start.
    // The fallback must unregister it, otherwise the kiosk re-launches the deleted app at next
    // login. Mirrors ExecuteStopAsync's interactive-session branch.
    [Fact]
    public void BuildUninstallPlan_WindowsInteractiveSessionFallback_UnregistersScheduledTask()
    {
        var profile = MinimalProfile() with
        {
            Deploy = MinimalDeploy() with { Path = "C:/app", InteractiveSession = true },
            Run = MinimalRun() with { InteractiveSession = true, Mode = RunMode.Service },
        };
        var target = WindowsTarget();

        var plan = InvokeBuildUninstallPlan("Kiosk-Profile", profile, target, hasCustom: false);

        var stopScript = plan.RemoteCommands[0].Script;
        Assert.Contains("Unregister-ScheduledTask", stopScript);
        Assert.Contains("'Roam_Kiosk-Profile'", stopScript);
        Assert.Contains("SilentlyContinue", stopScript);
    }

    // The state-store side of `roam uninstall`: RemoveManifests wipes the per-profile manifest
    // directory and reports the path. Subsequent loads see nothing — guaranteeing the next
    // `roam run` is a cold deploy with no false-warm diff.
    [Fact]
    public void StateStore_RemoveManifests_DeletesProfileDirectoryAndReportsPath()
    {
        using var workspace = new TempWorkspace();
        var state = new StateStore(workspace.Root);
        state.EnsureInitialized();

        state.SavePublishManifest("demo", new PublishManifest(
            PublishFingerprint.FingerprintSchemaVersion,
            "demo",
            "deadbeef",
            "local",
            "obj/publish",
            DateTimeOffset.UtcNow.ToString("O"),
            ["a.cs", "b.cs"]));

        Assert.NotNull(state.LoadPublishManifest("demo"));

        var removed = state.RemoveManifests("demo");

        Assert.NotNull(removed);
        Assert.False(Directory.Exists(removed!));
        Assert.Null(state.LoadPublishManifest("demo"));
    }

    [Fact]
    public void StateStore_RemoveManifests_ReturnsNull_WhenProfileDirectoryAbsent()
    {
        using var workspace = new TempWorkspace();
        var state = new StateStore(workspace.Root);
        state.EnsureInitialized();

        Assert.Null(state.RemoveManifests("never-deployed"));
    }

    // Schema: the deploy.uninstall key must round-trip cleanly through ConfigLoader so the
    // YAML pattern `deploy.uninstall: <shell>` actually reaches DeploySpec.Uninstall. Without
    // this the entire feature is unreachable from a roamfile.
    [Fact]
    public void ConfigLoader_ParsesDeployUninstallKey()
    {
        var path = WriteTempRoamfile(
            "version: 1\n" +
            "csproj: app.csproj\n" +
            "hosts:\n" +
            "  local:\n" +
            "    ssh: localhost\n" +
            "    user: test\n" +
            "    os: linux\n" +
            "profiles:\n" +
            "  demo:\n" +
            "    source: local\n" +
            "    build: local\n" +
            "    target: local\n" +
            "    publish-profile: Demo\n" +
            "    launch-profile: Demo\n" +
            "    deploy:\n" +
            "      path: /tmp/demo\n" +
            "      uninstall: |\n" +
            "        systemctl --user stop kiosk-ui\n" +
            "        rm -rf /opt/kiosk\n");

        var roamfile = ConfigLoader.Load(path);
        var uninstall = roamfile.Profiles["demo"].Deploy.Uninstall;

        Assert.NotNull(uninstall);
        Assert.Contains("systemctl --user stop kiosk-ui", uninstall);
        Assert.Contains("rm -rf /opt/kiosk", uninstall);
    }

    [Fact]
    public void ConfigLoader_DeployUninstallIsOptional()
    {
        var path = WriteTempRoamfile(
            "version: 1\n" +
            "csproj: app.csproj\n" +
            "hosts:\n" +
            "  local:\n" +
            "    ssh: localhost\n" +
            "    user: test\n" +
            "    os: linux\n" +
            "profiles:\n" +
            "  demo:\n" +
            "    source: local\n" +
            "    build: local\n" +
            "    target: local\n" +
            "    publish-profile: Demo\n" +
            "    launch-profile: Demo\n" +
            "    deploy:\n" +
            "      path: /tmp/demo\n");

        var roamfile = ConfigLoader.Load(path);

        Assert.Null(roamfile.Profiles["demo"].Deploy.Uninstall);
    }

    private static ProfileSpec MinimalProfile() => new(
        Description: null,
        Source: "local",
        Build: "local",
        Target: "target",
        PublishProfile: "Demo",
        Publish: null,
        LaunchProfile: "Demo",
        Env: new Dictionary<string, string>(),
        Deploy: MinimalDeploy(),
        Run: MinimalRun(),
        Debug: new DebugSpec(false, null, null, null, false));

    private static DeploySpec MinimalDeploy() => new(
        Path: "/tmp/demo",
        FlattenPublish: true,
        Stop: null,
        Start: null,
        Ready: null,
        ReadyTimeoutSeconds: 15,
        ReadyIntervalMilliseconds: 500,
        InteractiveSession: false);

    private static RunSpec MinimalRun() => new(
        Mode: RunMode.Service,
        Command: null,
        Stop: null,
        Ready: null,
        ReadyTimeoutSeconds: 15,
        ReadyIntervalMilliseconds: 500,
        InteractiveSession: false,
        TimeoutSeconds: 60,
        SuccessExitCodes: [0]);

    private static HostResolution LinuxTarget() => new(
        "target", "target", "roam", 22, null, [], null, null, "linux", false);

    private static HostResolution WindowsTarget() => new(
        "target", "target", "roam", 22, null, [], null, null, "windows", false);

    // Reflect into the private static BuildUninstallPlan helper to test its decisions in
    // isolation, without booting up a full SSH-reachable target. The plan is the right unit:
    // it's a pure function of (profile, target, hasCustom) and decides what the uninstall verb
    // will actually execute. Tests at this layer catch quoting and label regressions cheaply.
    private static UninstallPlanView InvokeBuildUninstallPlan(string profileName, ProfileSpec profile, HostResolution target, bool hasCustom)
    {
        var method = typeof(RoamCommands).GetMethod(
            "BuildUninstallPlan",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var plan = (IReadOnlyList<KeyValuePair<string, string>>)method!.Invoke(null, [profileName, profile, target, hasCustom])!;
        return new UninstallPlanView(plan);
    }

    // Test-facing view over the plan list. The shape (label, script) tuples mirror the
    // production KeyValuePair payload but spell the fields out so assertions read naturally:
    // `plan.RemoteCommands[0].Label` rather than `plan[0].Key`. Pure presentation glue.
    private sealed record UninstallPlanView(IReadOnlyList<KeyValuePair<string, string>> Raw)
    {
        public IReadOnlyList<UninstallStepView> RemoteCommands
            => Raw.Select(kv => new UninstallStepView(kv.Key, kv.Value)).ToArray();
    }

    private sealed record UninstallStepView(string Label, string Script);

    private static string WriteTempRoamfile(string content)
    {
        var dir = Directory.CreateTempSubdirectory("roam-uninstall-tests-");
        var path = Path.Combine(dir.FullName, "roamfile.yaml");
        File.WriteAllText(path, content);
        return path;
    }

    private sealed class TempWorkspace : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("roam-uninstall-state-");

        public string Root => _root.FullName;

        public void Dispose() => _root.Delete(recursive: true);
    }
}
