using System.Formats.Tar;
using System.IO.Compression;
using System.IO.Hashing;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace Roam;

public interface ISyncTarget : IDisposable
{
    Task<IReadOnlyList<RemoteFileEntry>> ListFilesAsync(string root, CancellationToken cancellationToken);

    Task UploadFileAsync(string localPath, string destinationPath, DateTimeOffset lastWriteTimeUtc, CancellationToken cancellationToken);

    Task DeleteFileAsync(string path, CancellationToken cancellationToken);

    // Transfer a batch of files in one call. The default is the historical behaviour — a per-file
    // loop — so targets that don't optimise batching (LocalSyncTarget, test fakes) need do nothing.
    // SftpSyncTarget overrides this to pack-and-extract a single archive when so configured.
    async Task UploadManyAsync(IReadOnlyList<SyncFileUpload> uploads, string destinationRoot, CancellationToken cancellationToken)
    {
        foreach (var upload in uploads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await UploadFileAsync(upload.LocalPath, upload.DestinationPath, upload.LastWriteTimeUtc, cancellationToken);
        }
    }
}

// Runs a shell command on a remote host (the OS-aware SSH wrapping lives in the implementation).
// Injected into SftpSyncTarget so the archive transport can drive a remote `tar -x` without the
// engine taking a hard dependency on the SSH command machinery.
public interface IRemoteCommandRunner
{
    Task<ProcessResult> RunAsync(string command, CancellationToken cancellationToken);
}

public static class MetadataDiffSyncEngine
{
    // Bumped to 2 when the skip decision moved from (size, mtime) to content hash. Manifests
    // written by an older roam lack ContentHash; they load fine but contribute no baseline,
    // so the first sync after an upgrade re-uploads everything and is warm thereafter.
    public const int ManifestSchemaVersion = 2;

    public static async Task<SyncManifest> SyncAsync(
        string profileName,
        string sourceRoot,
        string destinationRoot,
        SyncManifest? previousManifest,
        ISyncTarget target,
        string? sourceHost,
        string? buildHost,
        string? targetHost,
        string? workspace,
        string? deployPath,
        bool flattenPublish,
        string? gitHead,
        CancellationToken cancellationToken,
        IReadOnlyList<RemoteFileEntry>? sourceFiles = null)
    {
        var currentFiles = (sourceFiles ?? Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Select(path => new RemoteFileEntry(
                Path.GetRelativePath(sourceRoot, path).Replace('\\', '/'),
                new FileInfo(path).Length,
                File.GetLastWriteTimeUtc(path)))
            .ToArray())
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
        RoamLog.Event("sync.scan", "source files discovered", new Dictionary<string, object?>
        {
            ["profile"] = profileName,
            ["sourceRoot"] = sourceRoot,
            ["destinationRoot"] = destinationRoot,
            ["fileCount"] = currentFiles.Length,
            ["totalBytes"] = currentFiles.Sum(file => file.Size),
        });

        var localHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var hashStopwatch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var file in currentFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            localHashes[file.RelativePath] = await ComputeContentHashAsync(LocalSourcePath(sourceRoot, file.RelativePath), cancellationToken);
        }
        hashStopwatch.Stop();
        RoamLog.Event("sync.hash", "source file hashes computed", new Dictionary<string, object?>
        {
            ["profile"] = profileName,
            ["fileCount"] = currentFiles.Length,
            ["elapsedMs"] = hashStopwatch.ElapsedMilliseconds,
        });

        var baselineHashes = previousManifest?.Entries
            .Where(entry => entry.ContentHash is not null)
            .ToDictionary(entry => entry.Path, entry => entry.ContentHash!, StringComparer.Ordinal)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);

        var currentPaths = currentFiles.Select(x => x.RelativePath).ToHashSet(StringComparer.Ordinal);
        var previousPaths = previousManifest?.Entries.Select(x => x.Path).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
        var stalePaths = previousPaths.Except(currentPaths, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        var listStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var remoteFiles = await target.ListFilesAsync(destinationRoot, cancellationToken);
        listStopwatch.Stop();
        RoamLog.Event("sync.remote_list", "remote files listed", new Dictionary<string, object?>
        {
            ["profile"] = profileName,
            ["destinationRoot"] = destinationRoot,
            ["fileCount"] = remoteFiles.Count,
            ["elapsedMs"] = listStopwatch.ElapsedMilliseconds,
        });
        var remoteEntries = remoteFiles.ToDictionary(x => x.RelativePath, x => x, StringComparer.Ordinal);

        foreach (var stale in stalePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await target.DeleteFileAsync(CombineRemotePath(destinationRoot, stale), cancellationToken);
        }

        var uploads = new List<SyncFileUpload>();
        foreach (var file in currentFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ContentUnchanged(file, localHashes[file.RelativePath], baselineHashes, remoteEntries))
            {
                continue;
            }

            uploads.Add(new SyncFileUpload(
                LocalSourcePath(sourceRoot, file.RelativePath),
                file.RelativePath,
                CombineRemotePath(destinationRoot, file.RelativePath),
                file.LastWriteTimeUtc));
        }

        if (uploads.Count > 0)
        {
            RoamLog.Event("sync.upload_plan", "files selected for upload", new Dictionary<string, object?>
            {
                ["profile"] = profileName,
                ["uploadCount"] = uploads.Count,
                ["uploadBytes"] = uploads.Sum(upload => new FileInfo(upload.LocalPath).Length),
                ["staleCount"] = stalePaths.Length,
            });
            await target.UploadManyAsync(uploads, destinationRoot, cancellationToken);
        }
        else
        {
            RoamLog.Event("sync.upload_plan", "no files need upload", new Dictionary<string, object?>
            {
                ["profile"] = profileName,
                ["staleCount"] = stalePaths.Length,
            });
        }

        return new SyncManifest(
            ManifestSchemaVersion,
            profileName,
            sourceHost,
            buildHost,
            targetHost,
            workspace,
            deployPath,
            flattenPublish,
            gitHead,
            DateTimeOffset.UtcNow.ToString("O"),
            currentFiles.Select(file => new ManifestEntry(
                file.RelativePath,
                file.Size,
                file.LastWriteTimeUtc.ToUnixTimeMilliseconds() / 1000d,
                localHashes[file.RelativePath])).ToArray());
    }

    // Skip a file only when its bytes match the last synced bytes (manifest hash equality). The
    // manifest is the content authority because the remote can't be hashed cheaply over plain
    // SFTP; the remote listing is just an existence/size guard so an out-of-band deletion or
    // truncation on the target still forces a re-upload. Crucially this no longer keys on mtime:
    // deterministic rebuilds re-stamp mtimes on byte-identical assemblies, which used to defeat
    // the diff and re-send the whole publish.
    private static bool ContentUnchanged(
        RemoteFileEntry file,
        string localHash,
        IReadOnlyDictionary<string, string> baselineHashes,
        IReadOnlyDictionary<string, RemoteFileEntry> remoteEntries)
    {
        return baselineHashes.TryGetValue(file.RelativePath, out var baselineHash)
            && baselineHash == localHash
            && remoteEntries.TryGetValue(file.RelativePath, out var remote)
            && remote.Size == file.Size;
    }

    private static string LocalSourcePath(string sourceRoot, string relativePath)
        => Path.Combine(sourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static async Task<string> ComputeContentHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hasher = new XxHash64();
        await hasher.AppendAsync(stream, cancellationToken);
        return hasher.GetCurrentHashAsUInt64().ToString("x16");
    }

    private static string CombineRemotePath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return root;
        }

        return root.TrimEnd('/', '\\') + "/" + relativePath.Replace('\\', '/');
    }
}

public sealed class LocalSyncTarget : ISyncTarget
{
    public Task<IReadOnlyList<RemoteFileEntry>> ListFilesAsync(string root, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
        {
            return Task.FromResult<IReadOnlyList<RemoteFileEntry>>([]);
        }

        IReadOnlyList<RemoteFileEntry> result = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => new RemoteFileEntry(
                Path.GetRelativePath(root, path).Replace('\\', '/'),
                new FileInfo(path).Length,
                File.GetLastWriteTimeUtc(path)))
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult(result);
    }

    public Task UploadFileAsync(string localPath, string destinationPath, DateTimeOffset lastWriteTimeUtc, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Copy(localPath, destinationPath, overwrite: true);
        File.SetLastWriteTimeUtc(destinationPath, lastWriteTimeUtc.UtcDateTime);
        return Task.CompletedTask;
    }

    public Task DeleteFileAsync(string path, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
    }
}

public sealed class SftpSyncTarget : ISyncTarget
{
    private readonly SftpClient _client;
    private readonly string _hostName;
    private readonly bool _windowsPaths;
    private readonly IRemoteCommandRunner? _remoteRunner;
    private readonly bool _useArchive;

    public SftpSyncTarget(HostResolution host, IRemoteCommandRunner? remoteRunner = null, bool useArchive = false)
    {
        _hostName = host.Name;
        _windowsPaths = string.Equals(host.Os, "windows", StringComparison.OrdinalIgnoreCase);
        _remoteRunner = remoteRunner;
        _useArchive = useArchive;
        _client = new SftpClient(SshNetConnectionInfoFactory.Create(host));
        try
        {
            RoamLog.Event("sftp.connect.start", "connecting SFTP", new Dictionary<string, object?>
            {
                ["host"] = host.Name,
                ["ssh"] = host.SshHost,
                ["user"] = host.User,
                ["port"] = host.Port,
            });
            _client.Connect();
            RoamLog.Event("sftp.connect.end", "SFTP connected", new Dictionary<string, object?>
            {
                ["host"] = host.Name,
                ["serverVersion"] = _client.ConnectionInfo.ServerVersion,
            });
        }
        catch (SshAuthenticationException ex)
        {
            var loaded = SshNetConnectionInfoFactory.LoadIdentityCandidates(host);
            throw new RoamException(
                ExitCode.Preflight,
                "preflight",
                host.Name,
                SshNetConnectionInfoFactory.FormatAuthenticationFailure(host, loaded.Candidates, ex.Message, loaded.Keys.Count));
        }
    }

    public Task<IReadOnlyList<RemoteFileEntry>> ListFilesAsync(string root, CancellationToken cancellationToken)
    {
        var normalizedRoot = NormalizePath(root);
        RoamLog.Event("sftp.list.start", "listing remote directory", new Dictionary<string, object?>
        {
            ["host"] = _hostName,
            ["root"] = root,
            ["normalizedRoot"] = normalizedRoot,
        });
        if (!_client.Exists(normalizedRoot))
        {
            RoamLog.Event("sftp.list.end", "remote directory does not exist", new Dictionary<string, object?>
            {
                ["host"] = _hostName,
                ["root"] = root,
            });
            return Task.FromResult<IReadOnlyList<RemoteFileEntry>>([]);
        }

        var entries = new List<RemoteFileEntry>();
        ListRecursive(normalizedRoot, string.Empty, entries, cancellationToken);
        IReadOnlyList<RemoteFileEntry> result = entries.OrderBy(x => x.RelativePath, StringComparer.Ordinal).ToArray();
        RoamLog.Event("sftp.list.end", "remote directory listed", new Dictionary<string, object?>
        {
            ["host"] = _hostName,
            ["root"] = root,
            ["fileCount"] = result.Count,
            ["totalBytes"] = result.Sum(entry => entry.Size),
        });
        return Task.FromResult(result);
    }

    public Task UploadFileAsync(string localPath, string destinationPath, DateTimeOffset lastWriteTimeUtc, CancellationToken cancellationToken)
    {
        var normalizedDestination = NormalizePath(destinationPath);
        EnsureDirectory(GetDirectoryName(normalizedDestination));
        using var input = File.OpenRead(localPath);
        UploadWithProgress(input, normalizedDestination, new FileInfo(localPath).Length, "sftp.upload_file.progress");
        var uploadMode = SftpUploadPermissions.GetUploadMode(localPath, _windowsPaths);
        if (uploadMode is not null)
        {
            _client.ChangePermissions(normalizedDestination, uploadMode.Value);
        }

        _client.SetLastWriteTimeUtc(normalizedDestination, lastWriteTimeUtc.UtcDateTime);

        return Task.CompletedTask;
    }

    public async Task UploadManyAsync(IReadOnlyList<SyncFileUpload> uploads, string destinationRoot, CancellationToken cancellationToken)
    {
        if (_useArchive && _remoteRunner is not null && uploads.Count > 0)
        {
            await UploadArchiveAsync(uploads, destinationRoot, cancellationToken);
            return;
        }

        foreach (var upload in uploads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RoamLog.Event("sftp.upload_file.start", "uploading file", new Dictionary<string, object?>
            {
                ["host"] = _hostName,
                ["relativePath"] = upload.RelativePath,
                ["destination"] = upload.DestinationPath,
                ["bytes"] = new FileInfo(upload.LocalPath).Length,
            });
            await UploadFileAsync(upload.LocalPath, upload.DestinationPath, upload.LastWriteTimeUtc, cancellationToken);
        }
    }

    private async Task UploadArchiveAsync(IReadOnlyList<SyncFileUpload> uploads, string destinationRoot, CancellationToken cancellationToken)
    {
        var localArchive = Path.Combine(Path.GetTempPath(), $"roam-sync-{Guid.NewGuid():N}.tar.gz");
        try
        {
            RoamLog.Event("sftp.archive.pack.start", "packing sync archive", new Dictionary<string, object?>
            {
                ["host"] = _hostName,
                ["fileCount"] = uploads.Count,
                ["inputBytes"] = uploads.Sum(upload => new FileInfo(upload.LocalPath).Length),
                ["localArchive"] = localArchive,
            });
            await using (var fileStream = File.Create(localArchive))
            {
                await SyncArchive.WriteAsync(fileStream, uploads, _windowsPaths, cancellationToken);
            }
            var archiveBytes = new FileInfo(localArchive).Length;
            RoamLog.Event("sftp.archive.pack.end", "sync archive packed", new Dictionary<string, object?>
            {
                ["host"] = _hostName,
                ["localArchive"] = localArchive,
                ["archiveBytes"] = archiveBytes,
            });

            // The remote archive lives inside the deploy root (same filesystem, known-writable).
            // Two path forms: the SFTP form for the upload (SSH.NET prefixes a Windows drive with
            // '/'), and the shell form for the `tar` command (a normal C:/... or /opt/... path).
            var shellRoot = destinationRoot.Replace('\\', '/').TrimEnd('/');
            var shellArchive = shellRoot + "/" + Path.GetFileName(localArchive);
            var sftpArchive = NormalizePath(shellArchive);

            EnsureDirectory(GetDirectoryName(sftpArchive));
            using (var input = File.OpenRead(localArchive))
            {
                RoamLog.Event("sftp.archive.upload.start", "uploading sync archive", new Dictionary<string, object?>
                {
                    ["host"] = _hostName,
                    ["remoteArchive"] = shellArchive,
                    ["archiveBytes"] = archiveBytes,
                });
                UploadWithProgress(input, sftpArchive, archiveBytes, "sftp.archive.upload.progress");
                RoamLog.Event("sftp.archive.upload.end", "sync archive uploaded", new Dictionary<string, object?>
                {
                    ["host"] = _hostName,
                    ["remoteArchive"] = shellArchive,
                    ["archiveBytes"] = archiveBytes,
                });
            }

            var extract = BuildExtractCommand(shellArchive, shellRoot, _windowsPaths);
            RoamLog.Event("sftp.archive.extract.start", "extracting sync archive", new Dictionary<string, object?>
            {
                ["host"] = _hostName,
                ["command"] = extract,
                ["destinationRoot"] = shellRoot,
            });
            try
            {
                var result = await _remoteRunner!.RunAsync(extract, cancellationToken);
                RoamLog.Event("sftp.archive.extract.end", "sync archive extract completed", new Dictionary<string, object?>
                {
                    ["host"] = _hostName,
                    ["exitCode"] = result.ExitCode,
                    ["stdout"] = FirstNonEmptyLine(result.StdOut),
                    ["stderr"] = FirstNonEmptyLine(result.StdErr),
                });
                if (result.ExitCode != 0)
                {
                    var detail = FirstNonEmptyLine(result.StdErr) ?? FirstNonEmptyLine(result.StdOut) ?? $"tar exit {result.ExitCode}";
                    throw new InvalidOperationException($"remote archive extraction failed on the target: {detail}");
                }
            }
            catch
            {
                // The extract command removes the remote archive only on its own success path, so a
                // failed or interrupted extract orphans the Guid-named tarball under the deploy root
                // — and it is never manifest-owned, so sync-artifacts will never reclaim it. Best-
                // effort remove it here; the original failure is the one worth surfacing.
                TryRemoveRemoteArchive(sftpArchive);
                throw;
            }
        }
        finally
        {
            if (File.Exists(localArchive))
            {
                File.Delete(localArchive);
            }
        }
    }

    // Best-effort delete of an orphaned remote archive left by a failed extract. Swallows its own
    // errors: it runs on a path that is already failing, and the extraction failure is what matters.
    private void TryRemoveRemoteArchive(string sftpArchivePath)
    {
        try
        {
            if (_client.Exists(sftpArchivePath))
            {
                _client.DeleteFile(sftpArchivePath);
                RoamLog.Event("sftp.archive.cleanup", "orphaned remote archive removed after failed extract", new Dictionary<string, object?>
                {
                    ["host"] = _hostName,
                    ["remoteArchive"] = sftpArchivePath,
                });
            }
        }
        catch (Exception ex)
        {
            RoamLog.Event("sftp.archive.cleanup.failed", "could not remove orphaned remote archive", new Dictionary<string, object?>
            {
                ["host"] = _hostName,
                ["remoteArchive"] = sftpArchivePath,
                ["error"] = ex.Message,
            });
        }
    }

    private static string BuildExtractCommand(string archivePath, string destinationRoot, bool windowsTarget)
    {
        if (windowsTarget)
        {
            // tar is a native exe; guard on $LASTEXITCODE rather than the ambient native-error
            // policy (which varies by PowerShell version). The throw is caught by the SSH command
            // wrapper and surfaced as a non-zero exit.
            return $"tar -xpzf {ProcessRunner.PowerShellQuote(archivePath)} -C {ProcessRunner.PowerShellQuote(destinationRoot)}; if ($LASTEXITCODE -ne 0) {{ throw \"roam archive extract failed ($LASTEXITCODE)\" }}; Remove-Item -Force -LiteralPath {ProcessRunner.PowerShellQuote(archivePath)}";
        }

        return $"tar -xpzf {ProcessRunner.ShellQuote(archivePath)} -C {ProcessRunner.ShellQuote(destinationRoot)} && rm -f {ProcessRunner.ShellQuote(archivePath)}";
    }

    // Delegates to the shared pure core so remote-archive-extract failures over ssh skip benign
    // warnings just like every other step (charles8051/roam#7).
    private static string? FirstNonEmptyLine(string text)
        => SshOutputLines.FirstMeaningful(text);

    private void UploadWithProgress(Stream input, string destination, long totalBytes, string eventName)
    {
        ulong lastLogged = 0;
        var lastLoggedAt = Environment.TickCount64;
        _client.UploadFile(input, destination, canOverride: true, uploaded =>
        {
            var now = Environment.TickCount64;
            if (uploaded == (ulong)totalBytes || now - lastLoggedAt >= 5000 || uploaded - lastLogged >= 64UL * 1024UL * 1024UL)
            {
                lastLogged = uploaded;
                lastLoggedAt = now;
                RoamLog.Event(eventName, "SFTP upload progress", new Dictionary<string, object?>
                {
                    ["host"] = _hostName,
                    ["destination"] = destination,
                    ["uploadedBytes"] = uploaded,
                    ["totalBytes"] = totalBytes,
                    ["percent"] = totalBytes == 0 ? 100 : Math.Round(uploaded * 100d / totalBytes, 2),
                });
            }
        });
    }

    public Task DeleteFileAsync(string path, CancellationToken cancellationToken)
    {
        var normalizedPath = NormalizePath(path);
        if (_client.Exists(normalizedPath))
        {
            _client.DeleteFile(normalizedPath);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    private void ListRecursive(string directory, string relativeDirectory, List<RemoteFileEntry> entries, CancellationToken cancellationToken)
    {
        foreach (var entry in _client.ListDirectory(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Name is "." or "..")
            {
                continue;
            }

            var relativePath = string.IsNullOrEmpty(relativeDirectory) ? entry.Name : relativeDirectory + "/" + entry.Name;
            if (entry.IsDirectory)
            {
                ListRecursive(entry.FullName, relativePath, entries, cancellationToken);
            }
            else
            {
                entries.Add(new RemoteFileEntry(relativePath, entry.Length, entry.LastWriteTimeUtc));
            }
        }
    }

    private void EnsureDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || _client.Exists(directory))
        {
            return;
        }

        var parent = GetDirectoryName(directory);
        if (!string.IsNullOrWhiteSpace(parent) && parent != directory)
        {
            EnsureDirectory(parent);
        }

        if (!_client.Exists(directory))
        {
            _client.CreateDirectory(directory);
        }
    }

    private string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (_windowsPaths && normalized.Length >= 2 && normalized[1] == ':')
        {
            normalized = "/" + normalized;
        }

        return normalized;
    }

    private static string GetDirectoryName(string path)
    {
        var trimmed = path.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        if (index <= 0)
        {
            return "/";
        }

        return trimmed[..index];
    }

}

// Packs a set of files into a gzip-compressed tar stream for the archive transport. Separated from
// SftpSyncTarget (which needs a live server) so the packing is unit-testable in isolation. tar
// natively carries mtimes and Unix modes, so extraction restores them without per-file round-trips.
public static class SyncArchive
{
    private const UnixFileMode DataMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead; // 0644

    private const UnixFileMode ExecutableMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute; // 0755

    public static async Task WriteAsync(Stream destination, IReadOnlyList<SyncFileUpload> uploads, bool windowsTarget, CancellationToken cancellationToken)
    {
        await using var gzip = new GZipStream(destination, CompressionLevel.Optimal, leaveOpen: true);
        await using var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true);
        foreach (var upload in uploads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = new PaxTarEntry(TarEntryType.RegularFile, upload.RelativePath)
            {
                ModificationTime = upload.LastWriteTimeUtc,
                Mode = ResolveEntryMode(upload.LocalPath, windowsTarget),
            };
            await using var content = File.OpenRead(upload.LocalPath);
            entry.DataStream = content;
            await tar.WriteEntryAsync(entry, cancellationToken);
        }
    }

    private static UnixFileMode ResolveEntryMode(string localPath, bool windowsTarget)
    {
        // Mirrors SftpUploadPermissions.GetUploadMode for the archive transport. A Windows target
        // ignores Unix modes; for a Unix target, infer the exec bit from file shape on a Windows
        // controller (NTFS has none to read) or mirror the source file's real mode on a Unix one.
        if (windowsTarget)
        {
            return DataMode;
        }

        if (OperatingSystem.IsWindows())
        {
            return SftpUploadPermissions.LooksExecutableOnUnix(localPath) ? ExecutableMode : DataMode;
        }

        return SftpUploadPermissions.IsExecutable(File.GetUnixFileMode(localPath)) ? ExecutableMode : DataMode;
    }
}

public static class SftpUploadPermissions
{
    public static short? GetUploadMode(string localPath, bool windowsTarget)
    {
        // A Windows target ignores Unix modes entirely.
        if (windowsTarget)
        {
            return null;
        }

        // Unix target from a Windows controller: NTFS has no exec bit to read — the pre-fix behavior
        // returned null (skipped chmod), which shipped the .NET apphost non-executable so a Linux
        // `start` failed with permission-denied. Infer the bit from the file's shape. The early
        // return also keeps the File.GetUnixFileMode call below on a proven-non-Windows path (CA1416).
        if (OperatingSystem.IsWindows())
        {
            return ToPortableMode(LooksExecutableOnUnix(localPath));
        }

        // Unix controller: mirror the source file's real exec bit.
        return ToPortableMode(IsExecutable(File.GetUnixFileMode(localPath)));
    }

    // Whether any owner/group/other execute bit is set. Pure flag check over the System.IO.UnixFileMode
    // enum — platform-neutral (only File.Get/SetUnixFileMode are Windows-gated, not the enum itself).
    public static bool IsExecutable(UnixFileMode mode)
        => mode.HasFlag(UnixFileMode.UserExecute)
        || mode.HasFlag(UnixFileMode.GroupExecute)
        || mode.HasFlag(UnixFileMode.OtherExecute);

    // A Windows controller can't read a Unix exec bit, so infer it from the file's shape. In a
    // .NET publish the executables are extensionless ELF binaries (the apphost named after the
    // assembly, plus createdump / singlefilehost); shell hooks are *.sh. Everything else
    // (.dll/.json/.pdb/.so/...) is data. Erring toward +x for the ambiguous extensionless case
    // is the fail-safe direction: a non-executable apphost is fatal, a +x data file is inert.
    public static bool LooksExecutableOnUnix(string localPath)
    {
        var extension = Path.GetExtension(localPath);
        if (string.IsNullOrEmpty(extension))
        {
            return true;
        }

        return extension.Equals(".sh", StringComparison.OrdinalIgnoreCase);
    }

    public static UnixFileMode? GetDownloadMode(bool executable)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        return executable
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    }

    private static short ToPortableMode(bool executable)
        => executable ? (short)755 : (short)644;
}

public static class SftpDirectoryDownloader
{
    public static Task DownloadDirectoryAsync(HostResolution host, string remoteRoot, string localRoot, CancellationToken cancellationToken)
    {
        using var client = new SftpClient(SshNetConnectionInfoFactory.Create(host));
        client.Connect();
        var normalizedRoot = remoteRoot.Replace('\\', '/');
        DownloadRecursive(client, normalizedRoot, localRoot, cancellationToken);
        return Task.CompletedTask;
    }

    private static void DownloadRecursive(SftpClient client, string remoteDirectory, string localDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(localDirectory);
        foreach (var entry in client.ListDirectory(remoteDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Name is "." or "..")
            {
                continue;
            }

            var localPath = Path.Combine(localDirectory, entry.Name);
            if (entry.IsDirectory)
            {
                DownloadRecursive(client, entry.FullName, localPath, cancellationToken);
            }
            else
            {
                using (var output = File.Create(localPath))
                {
                    client.DownloadFile(entry.FullName, output);
                }

                File.SetLastWriteTimeUtc(localPath, entry.LastWriteTimeUtc);

                var mode = SftpUploadPermissions.GetDownloadMode(entry.OwnerCanExecute || entry.GroupCanExecute || entry.OthersCanExecute);
                if (mode is not null && !OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(localPath, mode.Value);
                }
            }
        }
    }
}

public static class SshNetConnectionInfoFactory
{
    public static ConnectionInfo Create(HostResolution host)
    {
        var loaded = LoadIdentityCandidates(host);

        if (loaded.Keys.Count == 0)
        {
            throw new RoamException(
                ExitCode.Preflight,
                "preflight",
                host.Name,
                FormatAuthenticationFailure(host, loaded.Candidates, "No candidate private key could be loaded by SSH.NET.", loadedKeyCount: 0));
        }

        var auth = new PrivateKeyAuthenticationMethod(host.User, loaded.Keys.ToArray());
        return new ConnectionInfo(host.SshHost, host.Port, host.User, auth);
    }

    public static SshIdentityLoadResult LoadIdentityCandidates(HostResolution host, string? homeDirectory = null)
    {
        var candidates = ResolveIdentityCandidates(host, homeDirectory).ToArray();
        var keys = new List<PrivateKeyFile>();
        var resolvedCandidates = new List<SshIdentityCandidate>();

        foreach (var candidate in candidates)
        {
            if (!candidate.Exists)
            {
                resolvedCandidates.Add(candidate with { FailureReason = "file not found" });
                continue;
            }

            try
            {
                keys.Add(new PrivateKeyFile(candidate.Path));
                resolvedCandidates.Add(candidate with { Loadable = true, FailureReason = null });
            }
            catch (Exception ex) when (ex is SshException or InvalidOperationException or FormatException or IOException)
            {
                resolvedCandidates.Add(candidate with { Loadable = false, FailureReason = DescribeKeyLoadFailure(ex) });
            }
        }

        return new SshIdentityLoadResult(resolvedCandidates, keys);
    }

    public static IReadOnlyList<SshIdentityCandidate> ResolveIdentityCandidates(HostResolution host, string? homeDirectory = null)
    {
        var home = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var result = new List<SshIdentityCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(host.IdentityFile))
        {
            AddCandidate(result, seen, ExpandHome(host.IdentityFile!, home), "explicit");
        }

        foreach (var configuredPath in host.IdentityFiles)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath) && !string.Equals(configuredPath, host.IdentityFile, StringComparison.Ordinal))
            {
                AddCandidate(result, seen, ExpandHome(configuredPath, home), "ssh-config");
            }
        }

        var sshDirectory = Path.Combine(home, ".ssh");
        foreach (var candidate in new[] { "id_ed25519", "id_rsa", "id_ecdsa" })
        {
            var path = Path.Combine(sshDirectory, candidate);
            if (File.Exists(path))
            {
                AddCandidate(result, seen, path, "default");
            }
        }

        return result;
    }

    public static string FormatAuthenticationFailure(HostResolution host, IEnumerable<SshIdentityCandidate> candidates, string reason, int loadedKeyCount)
    {
        var candidateText = candidates.Any()
            ? string.Join(", ", candidates.Select(FormatCandidate))
            : "none";

        return $"host '{host.Name}' ({host.User}@{host.SshHost}, user '{host.User}', port {host.Port}) failed SSH.NET SFTP authentication: {reason} loaded_keys={loadedKeyCount}; candidates: {candidateText}. SSH.NET SFTP sync requires non-interactive private-key authentication; verify the host alias, user, port, IdentityFile entries, key permissions, and whether the key is encrypted or requires ssh-agent.";
    }

    private static void AddCandidate(List<SshIdentityCandidate> result, HashSet<string> seen, string path, string source)
    {
        if (seen.Add(path))
        {
            result.Add(new SshIdentityCandidate(path, source, File.Exists(path)));
        }
    }

    private static string FormatCandidate(SshIdentityCandidate candidate)
    {
        var status = candidate.FailureReason ?? (candidate.Loadable ? "loadable" : candidate.Exists ? "exists" : "file not found");
        return $"{candidate.Path} [{candidate.Source}: {status}]";
    }

    private static string DescribeKeyLoadFailure(Exception ex)
    {
        var message = ex.Message;
        if (message.Contains("private key is encrypted", StringComparison.OrdinalIgnoreCase)
            || message.Contains("passphrase", StringComparison.OrdinalIgnoreCase))
        {
            return "encrypted key requires a passphrase or ssh-agent; SSH.NET v0 path is non-interactive";
        }

        if (ex is IOException)
        {
            return "could not read key file";
        }

        return "encrypted or unsupported key";
    }

    private static string ExpandHome(string path, string homeDirectory)
    {
        if (path == "~")
        {
            return homeDirectory;
        }

        if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            return Path.Combine(homeDirectory, path[2..]);
        }

        return path;
    }
}
