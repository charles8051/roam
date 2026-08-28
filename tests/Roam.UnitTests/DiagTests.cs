using System.Text.Json;
using Xunit;

namespace Roam.UnitTests;

public sealed class DiagTests
{
    // ---- DiagPlanner (pure) ----

    // Logs tier: the roam-redirected process log + each operator log, resolved against the deploy
    // path. No unit => no journald capture; no --dump => no dump dir.
    [Fact]
    public void PlanLogsTier_IncludesRedirectedLogAndOperatorLogs()
    {
        var deploy = Deploy("/opt/app", new DiagSpec(CrashDumps: false, Logs: ["app.log", "logs/extra.log"], Unit: null));
        var plan = DiagPlanner.Plan(deploy, windowsTarget: false, new DiagOptions(IncludeLogs: true, IncludeDump: false, TraceSeconds: null, Since: null), "/opt/app/roam-svc.out");

        Assert.Contains(plan.Files, f => f.RemotePath == "/opt/app/roam-svc.out" && f.Kind == "log" && f.LocalName == "process.out");
        Assert.Contains(plan.Files, f => f.RemotePath == "/opt/app/app.log" && f.LocalName == "logs/app.log");
        Assert.Contains(plan.Files, f => f.RemotePath == "/opt/app/logs/extra.log" && f.LocalName == "logs/extra.log");
        Assert.Empty(plan.Captures);
        Assert.Null(plan.DumpDir);
    }

    // Windows target: the shell passes a null redirected log (no nohup .out), and journald is skipped
    // even when a unit is named. Only the operator log survives.
    [Fact]
    public void Plan_WindowsTarget_NoRedirectedLog_NoJournald()
    {
        var deploy = Deploy("C:/app", new DiagSpec(false, ["app.log"], Unit: "myunit"));
        var plan = DiagPlanner.Plan(deploy, windowsTarget: true, new DiagOptions(true, false, null, null), redirectedLogRemotePath: null);

        var only = Assert.Single(plan.Files);
        Assert.Equal("C:/app/app.log", only.RemotePath);
        Assert.Empty(plan.Captures);
    }

    // A named unit on a Unix target yields a journald capture scoped to that unit and --since window.
    [Fact]
    public void Plan_Journald_WhenUnitSetOnUnix()
    {
        var deploy = Deploy("/opt/app", new DiagSpec(false, [], Unit: "kiosk"));
        var plan = DiagPlanner.Plan(deploy, windowsTarget: false, new DiagOptions(true, false, null, "1 hour ago"), null);

        var capture = Assert.Single(plan.Captures);
        Assert.Equal("journal", capture.Kind);
        Assert.Contains("journalctl", capture.Command);
        Assert.Contains("kiosk", capture.Command);
        Assert.Contains("--since", capture.Command);
    }

    [Fact]
    public void Plan_DumpTier_SetsDumpDir()
    {
        var plan = DiagPlanner.Plan(Deploy("/opt/app", null), windowsTarget: false, new DiagOptions(IncludeLogs: false, IncludeDump: true, null, null), null);
        Assert.Equal("/opt/app/.roam-diag/dumps", plan.DumpDir);
        Assert.Empty(plan.Files);
    }

    [Theory]
    [InlineData("/opt/app", "app.log", "/opt/app/app.log")]
    [InlineData("/opt/app/", "logs/a.log", "/opt/app/logs/a.log")]
    [InlineData("/opt/app", "/var/log/app.log", "/var/log/app.log")]   // absolute unix passes through
    [InlineData("C:/app", "C:/logs/x.log", "C:/logs/x.log")]            // absolute windows drive passes through
    [InlineData("C:\\app", "app.log", "C:/app/app.log")]               // backslash deploy path is normalized
    public void JoinRemote_RelativeVsAbsolute(string deployPath, string entry, string expected)
        => Assert.Equal(expected, DiagPlanner.JoinRemote(deployPath, entry));

    // ---- CrashDumpEnv (pure) ----

    [Fact]
    public void CrashDumpEnv_Empty_WhenOff()
    {
        Assert.Empty(DiagPlanner.CrashDumpEnv("/opt/app", null));
        Assert.Empty(DiagPlanner.CrashDumpEnv("/opt/app", new DiagSpec(CrashDumps: false, [], null)));
    }

    [Fact]
    public void CrashDumpEnv_SetsRuntimeVars_WhenOn()
    {
        var env = DiagPlanner.CrashDumpEnv("/opt/app/", new DiagSpec(CrashDumps: true, [], null, DiagToolSource.Target, DumpType: 4));
        Assert.Equal("1", env["DOTNET_DbgEnableMiniDump"]);
        Assert.Equal("4", env["DOTNET_DbgMiniDumpType"]);
        Assert.Equal("/opt/app/.roam-diag/dumps/core.%e.%p.%t.dmp", env["DOTNET_DbgMiniDumpName"]);
    }

    // ---- DiagEngine (shell, with fakes) ----

    // The headline: fetch the bundle, skip files absent on the target, write a machine-readable index
    // with bytes + sha256 for every artifact.
    [Fact]
    public async Task RunAsync_FetchesPresentLogs_WritesBundleAndIndex()
    {
        using var temp = new TempDir();
        var fetcher = new FakeFetcher();
        fetcher.Files["/opt/app/roam-svc.out"] = "roam-xplat sample CROSSPLAT_MARKER_V1\n";
        fetcher.Files["/opt/app/app.log"] = "hello\n";

        var plan = new DiagPlan(
            [
                new DiagFileFetch("/opt/app/roam-svc.out", "process.out", "log"),
                new DiagFileFetch("/opt/app/app.log", "logs/app.log", "log"),
                new DiagFileFetch("/opt/app/missing.log", "logs/missing.log", "log"),
            ],
            [],
            [],
            null);

        var index = await DiagEngine.RunAsync(plan, "svc", "linux01", temp.Path, fetcher, new FakeRunner(), "9.9.9", DateTimeOffset.UnixEpoch, CancellationToken.None);

        Assert.Equal("svc", index.Profile);
        Assert.Equal("linux01", index.Target);
        Assert.Equal(2, index.Artifacts.Count); // missing.log was absent → not indexed
        Assert.Contains("CROSSPLAT_MARKER_V1", await File.ReadAllTextAsync(Path.Combine(temp.Path, "process.out")));

        // diag.json round-trips, and every artifact carries a non-empty hash + positive size.
        var onDisk = JsonSerializer.Deserialize<DiagIndex>(await File.ReadAllTextAsync(Path.Combine(temp.Path, "diag.json")))!;
        Assert.Equal(2, onDisk.Artifacts.Count);
        Assert.All(onDisk.Artifacts, a => Assert.False(string.IsNullOrEmpty(a.Sha256)));
        Assert.All(onDisk.Artifacts, a => Assert.True(a.Bytes > 0));
        Assert.Contains(onDisk.Artifacts, a => a.LocalPath == "logs/app.log");
    }

    [Fact]
    public async Task RunAsync_Capture_WritesStdoutToFile()
    {
        using var temp = new TempDir();
        var runner = new FakeRunner();
        runner.Responses["journalctl"] = new ProcessResult(0, "May 01 INF started\n", "");

        var plan = new DiagPlan([], [], [new DiagCapture("journalctl -u svc", "journal.log", "journal")], null);
        var index = await DiagEngine.RunAsync(plan, "svc", "linux01", temp.Path, new FakeFetcher(), runner, "9.9.9", DateTimeOffset.UnixEpoch, CancellationToken.None);

        var artifact = Assert.Single(index.Artifacts);
        Assert.Equal("journal", artifact.Kind);
        Assert.Contains("started", await File.ReadAllTextAsync(Path.Combine(temp.Path, "journal.log")));
    }

    [Fact]
    public async Task RunAsync_DumpDir_IndexesEachDumpWithCrashReason()
    {
        using var temp = new TempDir();
        var fetcher = new FakeFetcher();
        fetcher.Dirs["/opt/app/.roam-diag/dumps"] = new() { ["core.App.1.dmp"] = "DUMPBYTES" };

        var plan = new DiagPlan([], [], [], "/opt/app/.roam-diag/dumps");
        var index = await DiagEngine.RunAsync(plan, "svc", "linux01", temp.Path, fetcher, new FakeRunner(), "9.9.9", DateTimeOffset.UnixEpoch, CancellationToken.None);

        var dump = Assert.Single(index.Artifacts);
        Assert.Equal("dump", dump.Kind);
        Assert.Equal("crash", dump.Reason);
        Assert.True(File.Exists(Path.Combine(temp.Path, "dumps", "core.App.1.dmp")));
    }

    // ---- glob logs (DiagPlanner + DiagEngine) ----

    [Theory]
    [InlineData("app-*.log", true)]
    [InlineData("app-?.log", true)]
    [InlineData("app.log", false)]
    [InlineData("logs/app.log", false)]
    public void IsGlob_DetectsWildcards(string entry, bool expected)
        => Assert.Equal(expected, DiagPlanner.IsGlob(entry));

    [Theory]
    [InlineData("/opt/app", "app-*.log", "/opt/app", "app-*.log")]
    [InlineData("/opt/app", "logs/app-*.log", "/opt/app/logs", "app-*.log")]
    [InlineData("/opt/app", "/var/log/app-*.log", "/var/log", "app-*.log")]
    public void SplitGlob_SeparatesDirAndPattern(string deployPath, string entry, string expectedDir, string expectedPattern)
    {
        var (dir, pattern) = DiagPlanner.SplitGlob(deployPath, entry);
        Assert.Equal(expectedDir, dir);
        Assert.Equal(expectedPattern, pattern);
    }

    [Theory]
    [InlineData("app-*.log", "app-20260601-1003.log", true)]
    [InlineData("app-*.log", "app-.log", true)]
    [InlineData("app-*.log", "app.txt", false)]
    [InlineData("*.log", "anything.log", true)]
    [InlineData("a?.log", "ab.log", true)]
    [InlineData("a?.log", "abc.log", false)]
    public void MatchesGlob_AnchoredWildcards(string pattern, string name, bool expected)
        => Assert.Equal(expected, DiagPlanner.MatchesGlob(name, pattern, ignoreCase: false));

    [Fact]
    public void MatchesGlob_HonorsCaseSensitivity()
    {
        Assert.False(DiagPlanner.MatchesGlob("APP-1.LOG", "app-*.log", ignoreCase: false));
        Assert.True(DiagPlanner.MatchesGlob("APP-1.LOG", "app-*.log", ignoreCase: true));
    }

    // A wildcard logs: entry becomes a DiagGlobFetch (dir + pattern), not a literal DiagFileFetch.
    [Fact]
    public void Plan_GlobEntry_BecomesGlobFetch()
    {
        var deploy = Deploy("/opt/app", new DiagSpec(false, ["app-*.log"], null));
        var plan = DiagPlanner.Plan(deploy, windowsTarget: false, new DiagOptions(true, false, null, null), redirectedLogRemotePath: null);

        var glob = Assert.Single(plan.Globs);
        Assert.Equal("/opt/app", glob.Dir);
        Assert.Equal("app-*.log", glob.Pattern);
        Assert.False(glob.IgnoreCase);              // Unix target -> case-sensitive
        Assert.Empty(plan.Files);                   // no literal file, no redirected log
    }

    // The engine lists the dir, fetches every match, and skips non-matches.
    [Fact]
    public async Task RunAsync_Glob_FetchesEveryMatch()
    {
        using var temp = new TempDir();
        var fetcher = new FakeFetcher();
        fetcher.Files["/opt/app/app-1.log"] = "one\n";
        fetcher.Files["/opt/app/app-2.log"] = "two\n";
        fetcher.Files["/opt/app/other.txt"] = "nope\n";

        var plan = new DiagPlan([], [new DiagGlobFetch("/opt/app", "app-*.log", "log", IgnoreCase: false)], [], null);
        var index = await DiagEngine.RunAsync(plan, "svc", "linux01", temp.Path, fetcher, new FakeRunner(), "9.9.9", DateTimeOffset.UnixEpoch, CancellationToken.None);

        Assert.Equal(2, index.Artifacts.Count);
        Assert.Contains(index.Artifacts, a => a.LocalPath == "logs/app-1.log");
        Assert.Contains(index.Artifacts, a => a.LocalPath == "logs/app-2.log");
        Assert.DoesNotContain(index.Artifacts, a => a.LocalPath.Contains("other"));
        Assert.True(File.Exists(Path.Combine(temp.Path, "logs", "app-1.log")));
    }

    // ---- helpers / fakes ----

    private static DeploySpec Deploy(string path, DiagSpec? diag)
        => new(path, FlattenPublish: true, Stop: null, Start: null, Ready: null, ReadyTimeoutSeconds: 15, ReadyIntervalMilliseconds: 500, InteractiveSession: false, Diag: diag);

    private sealed class FakeFetcher : IRemoteFileFetcher
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Dictionary<string, string>> Dirs { get; } = new(StringComparer.Ordinal);

        public Task<bool> TryFetchFileAsync(string remotePath, string localPath, CancellationToken cancellationToken)
        {
            if (!Files.TryGetValue(remotePath, out var content))
            {
                return Task.FromResult(false);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            File.WriteAllText(localPath, content);
            return Task.FromResult(true);
        }

        public Task<int> FetchDirectoryAsync(string remoteRoot, string localDir, CancellationToken cancellationToken)
        {
            if (!Dirs.TryGetValue(remoteRoot, out var files))
            {
                return Task.FromResult(0);
            }

            Directory.CreateDirectory(localDir);
            foreach (var (name, content) in files)
            {
                File.WriteAllText(Path.Combine(localDir, name), content);
            }

            return Task.FromResult(files.Count);
        }

        public Task<IReadOnlyList<string>> ListFileNamesAsync(string remoteDir, CancellationToken cancellationToken)
        {
            var prefix = remoteDir.TrimEnd('/') + "/";
            IReadOnlyList<string> names = Files.Keys
                .Where(p => p.StartsWith(prefix, StringComparison.Ordinal) && !p[prefix.Length..].Contains('/'))
                .Select(p => p[prefix.Length..])
                .ToList();
            return Task.FromResult(names);
        }
    }

    private sealed class FakeRunner : IRemoteCommandRunner
    {
        public Dictionary<string, ProcessResult> Responses { get; } = new(StringComparer.Ordinal);

        public Task<ProcessResult> RunAsync(string command, CancellationToken cancellationToken)
        {
            foreach (var (key, response) in Responses)
            {
                if (command.Contains(key, StringComparison.Ordinal))
                {
                    return Task.FromResult(response);
                }
            }

            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("roam-diag-test-").FullName;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }
}
