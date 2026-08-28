using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Renci.SshNet;

namespace Roam;

// Agent-first diagnostics (ADR-0002): the `roam diag` engine. Read-only capture of a diagnostic
// bundle from the target into a local directory plus a machine-readable diag.json index. The
// planning is a pure function (DiagPlanner); the IO (SSH captures, SFTP file/dir pulls, writing the
// bundle) lives in the shell (DiagEngine + the IRemoteFileFetcher implementations).

// What the caller asked diag to capture. Logs are the default tier; dump/trace are opt-in.
public sealed record DiagOptions(bool IncludeLogs, bool IncludeDump, int? TraceSeconds, string? Since);

// A single remote file to SFTP-download (best-effort: skipped silently if it doesn't exist).
public sealed record DiagFileFetch(string RemotePath, string LocalName, string Kind);

// A glob to expand on the target at fetch time: list `Dir` and fetch every regular file whose name
// matches `Pattern` (`*` / `?` wildcards). For timestamped logs (e.g. `app-20260601-1003.log`) that
// have no single stable filename. `IgnoreCase` follows the target OS (case-insensitive on Windows).
public sealed record DiagGlobFetch(string Dir, string Pattern, string Kind, bool IgnoreCase);

// A remote command whose stdout (+ stderr) is captured into a local file.
public sealed record DiagCapture(string Command, string LocalName, string Kind);

// The fully-resolved, pure plan of what to fetch. No IO, no clock — testable in isolation.
public sealed record DiagPlan(
    IReadOnlyList<DiagFileFetch> Files,
    IReadOnlyList<DiagGlobFetch> Globs,
    IReadOnlyList<DiagCapture> Captures,
    string? DumpDir);

// One artifact in the bundle index. snake_case JSON keys for agent consumption.
public sealed record DiagArtifact(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("target_path")] string? TargetPath,
    [property: JsonPropertyName("local_path")] string LocalPath,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("reason")] string? Reason = null);

// The machine-readable bundle index written to diag.json (and printed to stdout under --json).
public sealed record DiagIndex(
    [property: JsonPropertyName("profile")] string Profile,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("captured_utc")] string CapturedUtc,
    [property: JsonPropertyName("roam_version")] string RoamVersion,
    [property: JsonPropertyName("artifacts")] IReadOnlyList<DiagArtifact> Artifacts);

// Pure planning: given the deploy spec, target OS, options, and the (shell-computed) path of the
// roam-redirected process log, decide exactly which remote files to pull and commands to capture.
public static class DiagPlanner
{
    // The roam-owned scratch root under the deploy path. createdump writes minidumps here when
    // crash-dumps is enabled (see RoamCommands crash-dump env injection).
    public const string ScratchDirName = ".roam-diag";

    public static DiagPlan Plan(DeploySpec deploy, bool windowsTarget, DiagOptions options, string? redirectedLogRemotePath)
    {
        var files = new List<DiagFileFetch>();
        var globs = new List<DiagGlobFetch>();
        var captures = new List<DiagCapture>();
        string? dumpDir = null;

        if (options.IncludeLogs)
        {
            // The roam-redirected process stdout/stderr (Unix detach profiles). Free — roam already
            // writes it. Windows interactive-session tasks have no redirect, so the shell passes null.
            if (!string.IsNullOrWhiteSpace(redirectedLogRemotePath))
            {
                files.Add(new DiagFileFetch(redirectedLogRemotePath!, "process.out", "log"));
            }

            // Operator-named app log files. The universal artifact on a Windows target. Each entry is
            // resolved against the deploy path unless it is already absolute.
            var logs = deploy.Diag?.Logs ?? [];
            foreach (var entry in logs)
            {
                if (IsGlob(entry))
                {
                    var (dir, pattern) = SplitGlob(deploy.Path, entry);
                    globs.Add(new DiagGlobFetch(dir, pattern, "log", windowsTarget));
                }
                else
                {
                    files.Add(new DiagFileFetch(JoinRemote(deploy.Path, entry), "logs/" + LocalLeaf(entry), "log"));
                }
            }

            // systemd journal for the unit, when one is named and the target is Unix.
            var unit = deploy.Diag?.Unit;
            if (!windowsTarget && !string.IsNullOrWhiteSpace(unit))
            {
                var window = string.IsNullOrWhiteSpace(options.Since) ? "-n 500" : $"--since {ShellQuoteSingle(options.Since!)}";
                // Try the per-user journal first, then the system journal; never fail the capture.
                var cmd = $"journalctl --user -u {ShellQuoteSingle(unit!)} {window} --no-pager -o short-iso 2>/dev/null || " +
                          $"journalctl -u {ShellQuoteSingle(unit!)} {window} --no-pager -o short-iso 2>/dev/null || true";
                captures.Add(new DiagCapture(cmd, "journal.log", "journal"));
            }
        }

        if (options.IncludeDump)
        {
            dumpDir = JoinRemote(deploy.Path, ScratchDirName + "/dumps");
        }

        return new DiagPlan(files, globs, captures, dumpDir);
    }

    // Join a deploy-relative entry onto the deploy path as a forward-slash remote path (SFTP uses
    // '/' even for Windows targets; the fetcher re-prefixes a drive letter). Absolute entries pass
    // through. Pure and OS-neutral — absoluteness is detected structurally.
    public static string JoinRemote(string deployPath, string entry)
    {
        var e = entry.Replace('\\', '/');
        var isAbsolute = e.StartsWith('/') || (e.Length >= 2 && e[1] == ':') || e.StartsWith("//", StringComparison.Ordinal);
        if (isAbsolute)
        {
            return e;
        }

        return deployPath.Replace('\\', '/').TrimEnd('/') + "/" + e.TrimStart('/');
    }

    // The local file name for an operator log entry: just its leaf, so `logs/<name>` stays flat.
    private static string LocalLeaf(string entry)
    {
        var e = entry.Replace('\\', '/').TrimEnd('/');
        var slash = e.LastIndexOf('/');
        var leaf = slash < 0 ? e : e[(slash + 1)..];
        return string.IsNullOrEmpty(leaf) ? "log" : leaf;
    }

    // A logs: entry is a glob when it contains a `*` or `?` wildcard. (`[...]` classes are not
    // supported — a bare `[` is treated as a literal filename character.)
    public static bool IsGlob(string entry) => entry.IndexOfAny(['*', '?']) >= 0;

    // Split a glob entry into (remote directory, filename pattern). The wildcard is honored only in
    // the final path segment; the directory part is joined onto the deploy path (or taken absolute).
    // "app-*.log" -> (<deploy>, "app-*.log"); "logs/app-*.log" -> (<deploy>/logs, "app-*.log");
    // "/var/log/app-*.log" -> ("/var/log", "app-*.log").
    public static (string Dir, string Pattern) SplitGlob(string deployPath, string entry)
    {
        var e = entry.Replace('\\', '/');
        var slash = e.LastIndexOf('/');
        var pattern = slash < 0 ? e : e[(slash + 1)..];
        var dirPart = slash < 0 ? string.Empty : e[..slash];
        var dir = string.IsNullOrEmpty(dirPart)
            ? deployPath.Replace('\\', '/').TrimEnd('/')
            : JoinRemote(deployPath, dirPart);
        return (dir, pattern);
    }

    // Whether a filename matches a `*`/`?` glob pattern, via an anchored regex translation.
    public static bool MatchesGlob(string name, string pattern, bool ignoreCase)
    {
        var regex = new StringBuilder("^");
        foreach (var c in pattern)
        {
            regex.Append(c switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(c.ToString()),
            });
        }

        regex.Append('$');
        return Regex.IsMatch(name, regex.ToString(), ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
    }

    private static string ShellQuoteSingle(string value) => "'" + value.Replace("'", "'\\''") + "'";

    // Crash-dump env (ADR-0002): when crash-dumps is enabled, point the runtime's built-in createdump
    // at a roam-owned scratch dir so `roam diag --dump` can fetch a minidump after an unhandled
    // crash. createdump ships in every self-contained publish, so this needs no extra tooling. The
    // shell merges this into the start command's environment. Empty when crash-dumps is off.
    public static IReadOnlyDictionary<string, string> CrashDumpEnv(string deployPath, DiagSpec? diag)
    {
        if (diag is null || !diag.CrashDumps)
        {
            return new Dictionary<string, string>();
        }

        var dumpDir = $"{deployPath.Replace('\\', '/').TrimEnd('/')}/{ScratchDirName}/dumps";
        return new Dictionary<string, string>
        {
            ["DOTNET_DbgEnableMiniDump"] = "1",
            ["DOTNET_DbgMiniDumpType"] = diag.DumpType.ToString(),
            // %e=exe, %p=pid, %t=time. createdump creates the target directory if absent.
            ["DOTNET_DbgMiniDumpName"] = $"{dumpDir}/core.%e.%p.%t.dmp",
        };
    }
}

// Fetches remote files/directories for diag. Abstracted so the engine is unit-testable with a fake;
// the real implementation is SFTP, with a local-filesystem variant for IsLocal targets.
public interface IRemoteFileFetcher
{
    // Download a single remote file to localPath. Returns true iff it existed and was fetched.
    Task<bool> TryFetchFileAsync(string remotePath, string localPath, CancellationToken cancellationToken);

    // Download every regular file directly under remoteRoot into localDir. Returns the count fetched
    // (0 if the directory is absent). Non-recursive: dump/trace artifacts are flat.
    Task<int> FetchDirectoryAsync(string remoteRoot, string localDir, CancellationToken cancellationToken);

    // List the names (not paths) of regular files directly in remoteDir; empty if it's absent.
    // Non-recursive. Used to expand log glob patterns at fetch time.
    Task<IReadOnlyList<string>> ListFileNamesAsync(string remoteDir, CancellationToken cancellationToken);
}

public static class DiagEngine
{
    // Execute a plan: pull files, run captures, fetch the dump dir, hash everything, and write the
    // bundle + diag.json index into outDir. Returns the index (also useful for --json stdout).
    public static async Task<DiagIndex> RunAsync(
        DiagPlan plan,
        string profileName,
        string targetName,
        string outDir,
        IRemoteFileFetcher fetcher,
        IRemoteCommandRunner runner,
        string roamVersion,
        DateTimeOffset capturedUtc,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outDir);
        var artifacts = new List<DiagArtifact>();

        foreach (var file in plan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var localPath = Path.Combine(outDir, file.LocalName.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            RoamLog.Event("diag.fetch.file", "fetching diagnostic file", new Dictionary<string, object?>
            {
                ["remotePath"] = file.RemotePath,
                ["kind"] = file.Kind,
            });
            if (await fetcher.TryFetchFileAsync(file.RemotePath, localPath, cancellationToken))
            {
                artifacts.Add(Describe(file.Kind, file.RemotePath, localPath, outDir));
            }
            else
            {
                RoamLog.Event("diag.fetch.absent", "diagnostic file not present on target", new Dictionary<string, object?>
                {
                    ["remotePath"] = file.RemotePath,
                });
            }
        }

        foreach (var glob in plan.Globs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var names = await fetcher.ListFileNamesAsync(glob.Dir, cancellationToken);
            var matched = names
                .Where(name => DiagPlanner.MatchesGlob(name, glob.Pattern, glob.IgnoreCase))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
            RoamLog.Event("diag.glob", "expanded log glob", new Dictionary<string, object?>
            {
                ["dir"] = glob.Dir,
                ["pattern"] = glob.Pattern,
                ["matchCount"] = matched.Count,
            });
            foreach (var name in matched)
            {
                var remote = glob.Dir.TrimEnd('/') + "/" + name;
                var localPath = Path.Combine(outDir, "logs", name);
                Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                if (await fetcher.TryFetchFileAsync(remote, localPath, cancellationToken))
                {
                    artifacts.Add(Describe(glob.Kind, remote, localPath, outDir));
                }
            }
        }

        foreach (var capture in plan.Captures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RoamLog.Event("diag.capture", "running diagnostic capture", new Dictionary<string, object?>
            {
                ["kind"] = capture.Kind,
                ["command"] = capture.Command,
            });
            var result = await runner.RunAsync(capture.Command, cancellationToken);
            var body = result.StdOut;
            if (!string.IsNullOrEmpty(result.StdErr))
            {
                body += "\n--- stderr ---\n" + result.StdErr;
            }

            var localPath = Path.Combine(outDir, capture.LocalName);
            await File.WriteAllTextAsync(localPath, body, cancellationToken);
            artifacts.Add(Describe(capture.Kind, null, localPath, outDir));
        }

        if (plan.DumpDir is not null)
        {
            var dumpsLocal = Path.Combine(outDir, "dumps");
            RoamLog.Event("diag.fetch.dumps", "fetching crash-dump directory", new Dictionary<string, object?>
            {
                ["remoteDir"] = plan.DumpDir,
            });
            var count = await fetcher.FetchDirectoryAsync(plan.DumpDir, dumpsLocal, cancellationToken);
            if (count > 0)
            {
                foreach (var dumpFile in Directory.GetFiles(dumpsLocal).OrderBy(x => x, StringComparer.Ordinal))
                {
                    var remote = plan.DumpDir.TrimEnd('/') + "/" + Path.GetFileName(dumpFile);
                    artifacts.Add(Describe("dump", remote, dumpFile, outDir, reason: "crash"));
                }
            }
        }

        var index = new DiagIndex(
            profileName,
            targetName,
            capturedUtc.ToString("O"),
            roamVersion,
            artifacts);

        var json = JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(outDir, "diag.json"), json + Environment.NewLine, cancellationToken);
        return index;
    }

    private static DiagArtifact Describe(string kind, string? targetPath, string localPath, string outDir, string? reason = null)
    {
        var info = new FileInfo(localPath);
        var relative = Path.GetRelativePath(outDir, localPath).Replace('\\', '/');
        return new DiagArtifact(kind, targetPath, relative, info.Length, Sha256File(localPath), reason);
    }

    private static string Sha256File(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }
}

// SFTP fetcher: one connection, reused across the bundle. Disposed by the caller.
public sealed class SftpRemoteFileFetcher : IRemoteFileFetcher, IDisposable
{
    private readonly SftpClient _client;
    private readonly bool _windowsPaths;

    public SftpRemoteFileFetcher(HostResolution host)
    {
        _windowsPaths = string.Equals(host.Os, "windows", StringComparison.OrdinalIgnoreCase);
        _client = new SftpClient(SshNetConnectionInfoFactory.Create(host));
        _client.Connect();
    }

    public Task<bool> TryFetchFileAsync(string remotePath, string localPath, CancellationToken cancellationToken)
    {
        var normalized = NormalizePath(remotePath);
        if (!_client.Exists(normalized))
        {
            return Task.FromResult(false);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        using var output = File.Create(localPath);
        _client.DownloadFile(normalized, output);
        return Task.FromResult(true);
    }

    public Task<int> FetchDirectoryAsync(string remoteRoot, string localDir, CancellationToken cancellationToken)
    {
        var normalized = NormalizePath(remoteRoot);
        if (!_client.Exists(normalized))
        {
            return Task.FromResult(0);
        }

        Directory.CreateDirectory(localDir);
        var count = 0;
        foreach (var entry in _client.ListDirectory(normalized))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Name is "." or ".." || entry.IsDirectory)
            {
                continue;
            }

            using var output = File.Create(Path.Combine(localDir, entry.Name));
            _client.DownloadFile(entry.FullName, output);
            count++;
        }

        return Task.FromResult(count);
    }

    public Task<IReadOnlyList<string>> ListFileNamesAsync(string remoteDir, CancellationToken cancellationToken)
    {
        var normalized = NormalizePath(remoteDir);
        if (!_client.Exists(normalized))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        var names = new List<string>();
        foreach (var entry in _client.ListDirectory(normalized))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Name is "." or ".." || entry.IsDirectory)
            {
                continue;
            }

            names.Add(entry.Name);
        }

        return Task.FromResult<IReadOnlyList<string>>(names);
    }

    // SSH.NET wants '/'-style paths; a Windows drive path (C:/...) is prefixed with '/'.
    private string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (_windowsPaths && normalized.Length >= 2 && normalized[1] == ':')
        {
            normalized = "/" + normalized;
        }

        return normalized;
    }

    public void Dispose() => _client.Dispose();
}

// Local-filesystem fetcher for IsLocal targets (target == source). No SSH/SFTP.
public sealed class LocalRemoteFileFetcher : IRemoteFileFetcher
{
    public Task<bool> TryFetchFileAsync(string remotePath, string localPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(remotePath))
        {
            return Task.FromResult(false);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        File.Copy(remotePath, localPath, overwrite: true);
        return Task.FromResult(true);
    }

    public Task<int> FetchDirectoryAsync(string remoteRoot, string localDir, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(remoteRoot))
        {
            return Task.FromResult(0);
        }

        Directory.CreateDirectory(localDir);
        var count = 0;
        foreach (var file in Directory.GetFiles(remoteRoot))
        {
            File.Copy(file, Path.Combine(localDir, Path.GetFileName(file)), overwrite: true);
            count++;
        }

        return Task.FromResult(count);
    }

    public Task<IReadOnlyList<string>> ListFileNamesAsync(string remoteDir, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(remoteDir))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        IReadOnlyList<string> names = Directory.GetFiles(remoteDir).Select(p => Path.GetFileName(p)!).ToList();
        return Task.FromResult(names);
    }
}
