using System.Security.Cryptography;
using Xunit;

namespace Roam.UnitTests;

public sealed class ConfigLoaderTests
{
    [Fact]
    public void LoadsCanonicalFixture()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var roamfile = ConfigLoader.Load(Path.Combine(repositoryRoot, "tests/fixtures/SampleApp/roamfile.yaml"));

        Assert.Equal(1, roamfile.Version);
        Assert.True(roamfile.Hosts.ContainsKey("source"));
        Assert.True(roamfile.Profiles.ContainsKey("kiosk"));
        Assert.Equal("TestLinuxX64SelfContained", roamfile.Profiles["kiosk"].PublishProfile);
        Assert.Null(roamfile.Profiles["kiosk"].Publish);
        Assert.Equal(20, roamfile.Profiles["kiosk"].Deploy.ReadyTimeoutSeconds);
    }

    [Fact]
    public void AcceptsWindowsTargetHost()
    {
        var temp = CreateTempDirectory();
        var path = Path.Combine(temp, "roamfile.yaml");
        File.WriteAllText(path, "version: 1\ncsproj: app.csproj\nhosts:\n  source:\n    ssh: localhost\n    user: test\n    os: linux\n  build:\n    ssh: buildhost\n    user: test\n    os: linux\n  target:\n    ssh: winhost\n    user: test\n    os: windows\nprofiles:\n  demo:\n    source: source\n    build: build\n    target: target\n    publish-profile: Demo\n    launch-profile: Demo\n    deploy:\n      path: C:/roam/demo\n");

        var roamfile = ConfigLoader.Load(path);
        Assert.Equal("windows", roamfile.Hosts["target"].Os);
    }

    [Fact]
    public void AcceptsRoamNativePublishBlock()
    {
        var temp = CreateTempDirectory();
        var path = Path.Combine(temp, "roamfile.yaml");
        File.WriteAllText(path, "version: 1\ncsproj: app.csproj\nhosts:\n  local:\n    ssh: localhost\n    user: test\n    os: linux\nprofiles:\n  demo:\n    source: local\n    build: local\n    target: local\n    launch-profile: Demo\n    publish:\n      rid: linux-x64\n      self-contained: true\n      configuration: Release\n    deploy:\n      path: /tmp/demo\n");

        var roamfile = ConfigLoader.Load(path);

        Assert.Null(roamfile.Profiles["demo"].PublishProfile);
        Assert.NotNull(roamfile.Profiles["demo"].Publish);
        Assert.Equal("linux-x64", roamfile.Profiles["demo"].Publish!.Rid);
        Assert.True(roamfile.Profiles["demo"].Publish!.SelfContained);
    }

    // A profile that names neither publish shape used to be a config error. It now gets a
    // synthesized publish block; the RID follows the target host's declared OS.
    [Fact]
    public void DefaultsPublishBlockWhenProfileNamesNeitherShape()
    {
        var temp = CreateTempDirectory();
        var path = Path.Combine(temp, "roamfile.yaml");
        File.WriteAllText(path, "version: 1\ncsproj: app.csproj\nhosts:\n  local:\n    ssh: localhost\n    user: test\n    os: linux\nprofiles:\n  demo:\n    source: local\n    build: local\n    target: local\n    launch-profile: Demo\n    deploy:\n      path: /tmp/demo\n");

        var profile = ConfigLoader.Load(path).Profiles["demo"];

        Assert.Null(profile.PublishProfile);
        Assert.NotNull(profile.Publish);
        Assert.StartsWith("linux-", profile.Publish!.Rid);
        Assert.True(profile.Publish.SelfContained);
        Assert.Equal("Release", profile.Publish.Configuration);
    }

    // An explicit publish block that omits only the RID keeps every other field it declared —
    // notably configuration, which stays null so dotnet picks its own default.
    [Fact]
    public void DefaultsOnlyTheRidInsideAnExplicitPublishBlock()
    {
        var temp = CreateTempDirectory();
        var path = Path.Combine(temp, "roamfile.yaml");
        File.WriteAllText(path, "version: 1\ncsproj: app.csproj\nhosts:\n  local:\n    ssh: localhost\n    user: test\n    os: windows\nprofiles:\n  demo:\n    source: local\n    build: local\n    target: local\n    launch-profile: Demo\n    publish:\n      self-contained: false\n    deploy:\n      path: /tmp/demo\n");

        var profile = ConfigLoader.Load(path).Profiles["demo"];

        Assert.StartsWith("win-", profile.Publish!.Rid);
        Assert.False(profile.Publish.SelfContained);
        Assert.Null(profile.Publish.Configuration);
    }

    [Fact]
    public void RejectsProfilesWithBothPublishProfileAndPublishBlock()
    {
        var temp = CreateTempDirectory();
        var path = Path.Combine(temp, "roamfile.yaml");
        File.WriteAllText(path, "version: 1\ncsproj: app.csproj\nhosts:\n  local:\n    ssh: localhost\n    user: test\n    os: linux\nprofiles:\n  demo:\n    source: local\n    build: local\n    target: local\n    publish-profile: Demo\n    launch-profile: Demo\n    publish:\n      rid: linux-x64\n      self-contained: true\n    deploy:\n      path: /tmp/demo\n");

        var ex = Assert.Throws<RoamException>(() => ConfigLoader.Load(path));
        Assert.Contains("set at most one", ex.Message);
    }

    [Fact]
    public void RejectsUnknownTopLevelKey()
    {
        var temp = CreateTempDirectory();
        var path = Path.Combine(temp, "roamfile.yaml");
        File.WriteAllText(path, "version: 1\ncsproj: app.csproj\nunknown: true\nhosts: { local: { ssh: localhost, user: test } }\nprofiles: { p: { source: local, build: local, target: local, publish-profile: X, launch-profile: Y, deploy: { path: /tmp } } }\n");

        var ex = Assert.Throws<RoamException>(() => ConfigLoader.Load(path));
        Assert.Equal(ExitCode.Config, ex.ExitCode);
        Assert.Contains("unknown key 'unknown'", ex.Message);
    }

    [Fact]
    public void ParsesArchiveTransferMode()
    {
        var temp = CreateTempDirectory();
        var path = Path.Combine(temp, "roamfile.yaml");
        File.WriteAllText(path, "version: 1\ncsproj: app.csproj\nhosts:\n  local:\n    ssh: localhost\n    user: test\n    os: linux\nprofiles:\n  demo:\n    source: local\n    build: local\n    target: local\n    publish-profile: Demo\n    launch-profile: Demo\n    deploy:\n      path: /tmp/demo\n      transfer: archive\n");

        var roamfile = ConfigLoader.Load(path);
        Assert.Equal(SyncTransferMode.Archive, roamfile.Profiles["demo"].Deploy.Transfer);
    }

    [Fact]
    public void DefaultsTransferModeToPerFile()
    {
        var temp = CreateTempDirectory();
        var path = Path.Combine(temp, "roamfile.yaml");
        File.WriteAllText(path, "version: 1\ncsproj: app.csproj\nhosts:\n  local:\n    ssh: localhost\n    user: test\n    os: linux\nprofiles:\n  demo:\n    source: local\n    build: local\n    target: local\n    publish-profile: Demo\n    launch-profile: Demo\n    deploy:\n      path: /tmp/demo\n");

        var roamfile = ConfigLoader.Load(path);
        Assert.Equal(SyncTransferMode.PerFile, roamfile.Profiles["demo"].Deploy.Transfer);
        Assert.Equal(RunMode.Service, roamfile.Profiles["demo"].Run.Mode);
        Assert.Null(roamfile.Profiles["demo"].Run.Command);
    }

    [Fact]
    public void ParsesOneShotRunBlock()
    {
        var temp = CreateTempDirectory();
        var path = Path.Combine(temp, "roamfile.yaml");
        File.WriteAllText(path, "version: 1\ncsproj: app.csproj\nhosts:\n  local:\n    ssh: localhost\n    user: test\n    os: linux\nprofiles:\n  demo:\n    source: local\n    build: local\n    target: local\n    publish-profile: Demo\n    launch-profile: Demo\n    deploy:\n      path: /tmp/demo\n    run:\n      mode: one-shot\n      command: /tmp/demo/App --once\n      timeout: 45\n      success-exit-codes: [0, 2]\n");

        var roamfile = ConfigLoader.Load(path);
        var run = roamfile.Profiles["demo"].Run;

        Assert.Equal(RunMode.OneShot, run.Mode);
        Assert.Equal("/tmp/demo/App --once", run.Command);
        Assert.Equal(45, run.TimeoutSeconds);
        Assert.Equal([0, 2], run.SuccessExitCodes);
    }

    [Fact]
    public void RejectsOneShotRunBlockWithoutCommand()
    {
        var temp = CreateTempDirectory();
        var path = Path.Combine(temp, "roamfile.yaml");
        File.WriteAllText(path, "version: 1\ncsproj: app.csproj\nhosts:\n  local:\n    ssh: localhost\n    user: test\n    os: linux\nprofiles:\n  demo:\n    source: local\n    build: local\n    target: local\n    publish-profile: Demo\n    launch-profile: Demo\n    deploy:\n      path: /tmp/demo\n    run:\n      mode: one-shot\n");

        var ex = Assert.Throws<RoamException>(() => ConfigLoader.Load(path));
        Assert.Equal(ExitCode.Config, ex.ExitCode);
        Assert.Contains("run.command", ex.Message);
    }

    [Fact]
    public void RejectsUnknownTransferMode()
    {
        var temp = CreateTempDirectory();
        var path = Path.Combine(temp, "roamfile.yaml");
        File.WriteAllText(path, "version: 1\ncsproj: app.csproj\nhosts:\n  local:\n    ssh: localhost\n    user: test\n    os: linux\nprofiles:\n  demo:\n    source: local\n    build: local\n    target: local\n    publish-profile: Demo\n    launch-profile: Demo\n    deploy:\n      path: /tmp/demo\n      transfer: rsync\n");

        var ex = Assert.Throws<RoamException>(() => ConfigLoader.Load(path));
        Assert.Equal(ExitCode.Config, ex.ExitCode);
        Assert.Contains("deploy.transfer", ex.Message);
    }

    // deploy.interactive-session-trigger reaches DeploySpec and, with no run: block, is inherited
    // onto the resolved RunSpec (the legacy service path). Without this the reboot-durability
    // trigger is unreachable from a roamfile that uses the deploy-only shape.
    [Fact]
    public void ParsesInteractiveSessionTriggerOnDeploy()
    {
        var temp = CreateTempDirectory();
        var path = Path.Combine(temp, "roamfile.yaml");
        File.WriteAllText(path, "version: 1\ncsproj: app.csproj\nhosts:\n  local:\n    ssh: localhost\n    user: test\n    os: windows\nprofiles:\n  demo:\n    source: local\n    build: local\n    target: local\n    publish-profile: Demo\n    launch-profile: Demo\n    deploy:\n      path: C:/demo\n      interactive-session: true\n      interactive-session-trigger: at-logon\n");

        var profile = ConfigLoader.Load(path).Profiles["demo"];

        Assert.Equal(InteractiveSessionTrigger.AtLogon, profile.Deploy.InteractiveSessionTrigger);
        Assert.Equal(InteractiveSessionTrigger.AtLogon, profile.Run.InteractiveSessionTrigger);
    }

    // run.interactive-session-trigger reaches the run-block RunSpec, scoped to run: like
    // interactive-session itself (not inherited from deploy when run: is present).
    [Fact]
    public void ParsesInteractiveSessionTriggerOnRun()
    {
        var temp = CreateTempDirectory();
        var path = Path.Combine(temp, "roamfile.yaml");
        File.WriteAllText(path, "version: 1\ncsproj: app.csproj\nhosts:\n  local:\n    ssh: localhost\n    user: test\n    os: windows\nprofiles:\n  demo:\n    source: local\n    build: local\n    target: local\n    publish-profile: Demo\n    launch-profile: Demo\n    deploy:\n      path: C:/demo\n    run:\n      mode: service\n      interactive-session: true\n      interactive-session-trigger: at-logon\n");

        Assert.Equal(InteractiveSessionTrigger.AtLogon, ConfigLoader.Load(path).Profiles["demo"].Run.InteractiveSessionTrigger);
    }

    // Default (key unset) is None on both deploy and the resolved run, preserving today's
    // no-trigger behavior for every existing interactive-session profile.
    [Fact]
    public void DefaultsInteractiveSessionTriggerToNone()
    {
        var temp = CreateTempDirectory();
        var path = Path.Combine(temp, "roamfile.yaml");
        File.WriteAllText(path, "version: 1\ncsproj: app.csproj\nhosts:\n  local:\n    ssh: localhost\n    user: test\n    os: windows\nprofiles:\n  demo:\n    source: local\n    build: local\n    target: local\n    publish-profile: Demo\n    launch-profile: Demo\n    deploy:\n      path: C:/demo\n      interactive-session: true\n");

        var profile = ConfigLoader.Load(path).Profiles["demo"];

        Assert.Equal(InteractiveSessionTrigger.None, profile.Deploy.InteractiveSessionTrigger);
        Assert.Equal(InteractiveSessionTrigger.None, profile.Run.InteractiveSessionTrigger);
    }

    [Fact]
    public void RejectsUnknownInteractiveSessionTrigger()
    {
        var temp = CreateTempDirectory();
        var path = Path.Combine(temp, "roamfile.yaml");
        File.WriteAllText(path, "version: 1\ncsproj: app.csproj\nhosts:\n  local:\n    ssh: localhost\n    user: test\n    os: windows\nprofiles:\n  demo:\n    source: local\n    build: local\n    target: local\n    publish-profile: Demo\n    launch-profile: Demo\n    deploy:\n      path: C:/demo\n      interactive-session-trigger: at-startup\n");

        var ex = Assert.Throws<RoamException>(() => ConfigLoader.Load(path));
        Assert.Equal(ExitCode.Config, ex.ExitCode);
        Assert.Contains("interactive-session-trigger", ex.Message);
    }

    // Guards that adding interactive-session-trigger to the deploy allow-list did not widen
    // RequireOnlyKeys: a genuinely-unknown deploy key (here a near-miss typo) is still rejected.
    [Fact]
    public void RejectsUnknownDeployKey()
    {
        var temp = CreateTempDirectory();
        var path = Path.Combine(temp, "roamfile.yaml");
        File.WriteAllText(path, "version: 1\ncsproj: app.csproj\nhosts:\n  local:\n    ssh: localhost\n    user: test\n    os: windows\nprofiles:\n  demo:\n    source: local\n    build: local\n    target: local\n    publish-profile: Demo\n    launch-profile: Demo\n    deploy:\n      path: C:/demo\n      interactive-session-triggers: at-logon\n");

        var ex = Assert.Throws<RoamException>(() => ConfigLoader.Load(path));
        Assert.Equal(ExitCode.Config, ex.ExitCode);
        Assert.Contains("unknown key 'interactive-session-triggers'", ex.Message);
    }

    // deploy.run-level reaches DeploySpec and, with no run: block, is inherited onto the resolved
    // RunSpec (the legacy service path) -- exactly like interactive-session-trigger. Without this
    // the elevation knob is unreachable from a roamfile that uses the deploy-only shape.
    [Fact]
    public void ParsesRunLevelHighestOnDeploy()
    {
        var temp = CreateTempDirectory();
        var path = Path.Combine(temp, "roamfile.yaml");
        File.WriteAllText(path, "version: 1\ncsproj: app.csproj\nhosts:\n  local:\n    ssh: localhost\n    user: test\n    os: windows\nprofiles:\n  demo:\n    source: local\n    build: local\n    target: local\n    publish-profile: Demo\n    launch-profile: Demo\n    deploy:\n      path: C:/demo\n      interactive-session: true\n      run-level: highest\n");

        var profile = ConfigLoader.Load(path).Profiles["demo"];

        Assert.Equal(RunLevel.Highest, profile.Deploy.RunLevel);
        Assert.Equal(RunLevel.Highest, profile.Run.RunLevel);
    }

    // run.run-level reaches the run-block RunSpec, scoped to run: (not inherited from deploy when
    // run: is present). 'limited' parses to the explicit default value.
    [Fact]
    public void ParsesRunLevelOnRun()
    {
        var temp = CreateTempDirectory();
        var path = Path.Combine(temp, "roamfile.yaml");
        File.WriteAllText(path, "version: 1\ncsproj: app.csproj\nhosts:\n  local:\n    ssh: localhost\n    user: test\n    os: windows\nprofiles:\n  highest-run:\n    source: local\n    build: local\n    target: local\n    publish-profile: Demo\n    launch-profile: Demo\n    deploy:\n      path: C:/demo\n    run:\n      mode: service\n      interactive-session: true\n      run-level: highest\n  limited-run:\n    source: local\n    build: local\n    target: local\n    publish-profile: Demo\n    launch-profile: Demo\n    deploy:\n      path: C:/demo\n    run:\n      mode: service\n      interactive-session: true\n      run-level: limited\n");

        var profiles = ConfigLoader.Load(path).Profiles;

        Assert.Equal(RunLevel.Highest, profiles["highest-run"].Run.RunLevel);
        Assert.Equal(RunLevel.Limited, profiles["limited-run"].Run.RunLevel);
    }

    // Default (key unset) is Limited on both deploy and the resolved run, preserving today's
    // non-elevated registration for every existing interactive-session profile.
    [Fact]
    public void DefaultsRunLevelToLimited()
    {
        var temp = CreateTempDirectory();
        var path = Path.Combine(temp, "roamfile.yaml");
        File.WriteAllText(path, "version: 1\ncsproj: app.csproj\nhosts:\n  local:\n    ssh: localhost\n    user: test\n    os: windows\nprofiles:\n  demo:\n    source: local\n    build: local\n    target: local\n    publish-profile: Demo\n    launch-profile: Demo\n    deploy:\n      path: C:/demo\n      interactive-session: true\n");

        var profile = ConfigLoader.Load(path).Profiles["demo"];

        Assert.Equal(RunLevel.Limited, profile.Deploy.RunLevel);
        Assert.Equal(RunLevel.Limited, profile.Run.RunLevel);
    }

    [Fact]
    public void RejectsUnknownRunLevel()
    {
        var temp = CreateTempDirectory();
        var path = Path.Combine(temp, "roamfile.yaml");
        File.WriteAllText(path, "version: 1\ncsproj: app.csproj\nhosts:\n  local:\n    ssh: localhost\n    user: test\n    os: windows\nprofiles:\n  demo:\n    source: local\n    build: local\n    target: local\n    publish-profile: Demo\n    launch-profile: Demo\n    deploy:\n      path: C:/demo\n      run-level: elevated\n");

        var ex = Assert.Throws<RoamException>(() => ConfigLoader.Load(path));
        Assert.Equal(ExitCode.Config, ex.ExitCode);
        Assert.Contains("run-level", ex.Message);
    }

    [Fact]
    public async Task AttachEmitterIsDeterministic()
    {
        var temp = CreateTempDirectory();
        var output = Path.Combine(temp, ".vscode", "launch.json");
        var host = new HostResolution("target", "target", "roam", 22, null, [], null, null, "linux", false);
        var debug = new DebugSpec(true, "vsdbg", "vscode", "Roam.SampleApp", false);

        await DebuggerEmitter.EmitAsync(output, "kiosk", "/work/source/repo", "/work/source/repo", "/work/build/repo", host, debug, CancellationToken.None);
        var first = await File.ReadAllBytesAsync(output);
        await DebuggerEmitter.EmitAsync(output, "kiosk", "/work/source/repo", "/work/source/repo", "/work/build/repo", host, debug, CancellationToken.None);
        var second = await File.ReadAllBytesAsync(output);

        Assert.Equal(Convert.ToHexString(SHA256.HashData(first)), Convert.ToHexString(SHA256.HashData(second)));
    }

    // The floor: a roamfile that names one profile and nothing else. Everything the pipeline needs
    // is derived — version, the csproj, the local host, the three roles, publish, deploy.path.
    [Fact]
    public void LoadsMinimalRoamfile()
    {
        var temp = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(temp, "src", "MyApp"));
        File.WriteAllText(Path.Combine(temp, "src", "MyApp", "MyApp.csproj"), "<Project />");
        var path = Path.Combine(temp, "roamfile.yaml");
        File.WriteAllText(path, "profiles:\n  dev-local:\n    deploy:\n      start: ./MyApp\n");

        var roamfile = ConfigLoader.Load(path);
        var profile = roamfile.Profiles["dev-local"];

        Assert.Equal(1, roamfile.Version);
        Assert.Equal("src/MyApp/MyApp.csproj", roamfile.Csproj);
        Assert.Equal("local", profile.Source);
        Assert.Equal("local", profile.Build);
        Assert.Equal("local", profile.Target);
        Assert.Null(profile.LaunchProfile);
        Assert.NotNull(profile.Publish);
        Assert.EndsWith("/.roam-dev", profile.Deploy.Path);
        Assert.Equal("./MyApp", profile.Run.Command);

        var local = roamfile.Hosts["local"];
        Assert.Equal("localhost", local.Ssh);
        Assert.Equal(temp.Replace('\\', '/'), local.Workspace);
    }

    // A profile body with no keys at all still resolves. It deploys nothing, but it parses, which
    // is what makes `deploy:` and the host roles genuinely optional rather than optional-on-paper.
    [Fact]
    public void LoadsProfileWithEmptyBody()
    {
        var temp = CreateTempDirectory();
        File.WriteAllText(Path.Combine(temp, "App.csproj"), "<Project />");
        var path = Path.Combine(temp, "roamfile.yaml");
        File.WriteAllText(path, "profiles:\n  dev-local:\n");

        var profile = ConfigLoader.Load(path).Profiles["dev-local"];

        Assert.Equal("local", profile.Target);
        Assert.EndsWith("/.roam-dev", profile.Deploy.Path);
    }

    // An empty host body is legal: ssh: falls back to the host key and the rest to `ssh -G`.
    [Fact]
    public void LoadsHostWithEmptyBody()
    {
        var temp = CreateTempDirectory();
        File.WriteAllText(Path.Combine(temp, "App.csproj"), "<Project />");
        var path = Path.Combine(temp, "roamfile.yaml");
        File.WriteAllText(path, "hosts:\n  devbox:\nprofiles:\n  demo:\n    deploy:\n      start: ./App\n");

        var roamfile = ConfigLoader.Load(path);

        Assert.Null(roamfile.Hosts["devbox"].Ssh);
        Assert.Equal("devbox", roamfile.Profiles["demo"].Source);
    }

    // build/target follow source; a remote target gets a roam-owned home-relative deploy path and
    // a roam-owned workspace, while the source host's workspace is the roamfile's own directory.
    [Fact]
    public void DefaultsRolesAndPathsAcrossTwoHosts()
    {
        var temp = CreateTempDirectory();
        File.WriteAllText(Path.Combine(temp, "EdgeWorker.csproj"), "<Project />");
        var path = Path.Combine(temp, "roamfile.yaml");
        File.WriteAllText(path, "hosts:\n  laptop:\n    ssh: laptop\n    user: dev\n  edge:\n    ssh: edge\n    user: edge\n    os: linux\nprofiles:\n  demo:\n    source: laptop\n    target: edge\n    deploy:\n      start: ./EdgeWorker\n");

        var roamfile = ConfigLoader.Load(path);
        var profile = roamfile.Profiles["demo"];

        Assert.Equal("laptop", profile.Build);
        Assert.Equal("~/.roam/apps/EdgeWorker", profile.Deploy.Path);
        Assert.Equal(temp.Replace('\\', '/'), roamfile.Hosts["laptop"].Workspace);
        Assert.Equal("~/.roam/src/EdgeWorker", roamfile.Hosts["edge"].Workspace);
    }

    // With more than one host and no `source`, roam cannot know which machine it is running on.
    [Fact]
    public void RejectsOmittedSourceWhenSeveralHostsAreDefined()
    {
        var temp = CreateTempDirectory();
        File.WriteAllText(Path.Combine(temp, "App.csproj"), "<Project />");
        var path = Path.Combine(temp, "roamfile.yaml");
        File.WriteAllText(path, "hosts:\n  a:\n    ssh: a\n  b:\n    ssh: b\nprofiles:\n  demo:\n    deploy:\n      start: ./App\n");

        var ex = Assert.Throws<RoamException>(() => ConfigLoader.Load(path));
        Assert.Equal(ExitCode.Config, ex.ExitCode);
        Assert.Contains("does not set 'source'", ex.Message);
    }

    [Fact]
    public void RejectsAmbiguousCsprojDiscovery()
    {
        var temp = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(temp, "one"));
        Directory.CreateDirectory(Path.Combine(temp, "two"));
        File.WriteAllText(Path.Combine(temp, "one", "One.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(temp, "two", "Two.csproj"), "<Project />");
        var path = Path.Combine(temp, "roamfile.yaml");
        File.WriteAllText(path, "profiles:\n  demo:\n    deploy:\n      start: ./App\n");

        var ex = Assert.Throws<RoamException>(() => ConfigLoader.Load(path));
        Assert.Equal(ExitCode.Config, ex.ExitCode);
        Assert.Contains("one/One.csproj", ex.Message);
        Assert.Contains("set 'csproj' explicitly", ex.Message);
    }

    // Discovery must ignore build output, or any repo that has been built once becomes ambiguous.
    [Fact]
    public void CsprojDiscoveryIgnoresBinAndObj()
    {
        var temp = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(temp, "src"));
        Directory.CreateDirectory(Path.Combine(temp, "src", "obj", "Debug"));
        File.WriteAllText(Path.Combine(temp, "src", "App.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(temp, "src", "obj", "Debug", "App.csproj"), "<Project />");
        var path = Path.Combine(temp, "roamfile.yaml");
        File.WriteAllText(path, "profiles:\n  demo:\n    deploy:\n      start: ./App\n");

        Assert.Equal("src/App.csproj", ConfigLoader.Load(path).Csproj);
    }

    // The pre-defaults shape must keep parsing to exactly the records it did before.
    [Fact]
    public void ExplicitRoamfileIsUnaffectedByDefaults()
    {
        var temp = CreateTempDirectory();
        File.WriteAllText(Path.Combine(temp, "app.csproj"), "<Project />");
        var path = Path.Combine(temp, "roamfile.yaml");
        File.WriteAllText(path, "version: 1\ncsproj: app.csproj\nhosts:\n  local:\n    ssh: localhost\n    user: test\n    workspace: /work\n    os: linux\nprofiles:\n  demo:\n    source: local\n    build: local\n    target: local\n    publish:\n      rid: linux-arm64\n      self-contained: false\n      configuration: Debug\n    launch-profile: Demo\n    deploy:\n      path: /tmp/demo\n");

        var roamfile = ConfigLoader.Load(path);
        var profile = roamfile.Profiles["demo"];

        Assert.Equal("/work", roamfile.Hosts["local"].Workspace);
        Assert.Equal("linux-arm64", profile.Publish!.Rid);
        Assert.False(profile.Publish.SelfContained);
        Assert.Equal("Debug", profile.Publish.Configuration);
        Assert.Equal("Demo", profile.LaunchProfile);
        Assert.Equal("/tmp/demo", profile.Deploy.Path);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "roam-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
