using Xunit;

namespace Roam.UnitTests;

public sealed class MetadataDiffSyncEngineTests
{
    [Fact]
    public void SftpUploadModePreservesExecutableFilesOnUnixTargets()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = Directory.CreateTempSubdirectory("roam-sftp-mode-");
        try
        {
            var executable = Path.Combine(tempRoot.FullName, "app");
            var data = Path.Combine(tempRoot.FullName, "app.json");
            File.WriteAllText(executable, "#!/usr/bin/env bash\n");
            File.WriteAllText(data, "{}\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.SetUnixFileMode(data, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

            Assert.Equal((short)755, SftpUploadPermissions.GetUploadMode(executable, windowsTarget: false));
            Assert.Equal((short)644, SftpUploadPermissions.GetUploadMode(data, windowsTarget: false));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
                SftpUploadPermissions.GetDownloadMode(executable: true));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                SftpUploadPermissions.GetDownloadMode(executable: false));
            Assert.Null(SftpUploadPermissions.GetUploadMode(executable, windowsTarget: true));
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    // The headline regression: deterministic rebuilds re-stamp mtimes on byte-identical files.
    // The old (size, mtime) diff treated a fresh mtime as a change and re-uploaded the whole
    // publish. Content hashing must skip a file whose bytes are unchanged regardless of mtime.
    [Fact]
    public async Task SkipsFileWhenContentMatchesManifestDespiteDifferentMtime()
    {
        using var temp = new TempWorkspace();
        var sourceRoot = temp.CreateDir("publish");
        var file = Path.Combine(sourceRoot, "app.dll");
        await File.WriteAllTextAsync(file, "identical bytes across rebuilds");
        File.SetLastWriteTimeUtc(file, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var target = new RecordingSyncTarget();

        var cold = await Sync(sourceRoot, target, previous: null);
        Assert.Equal(MetadataDiffSyncEngine.ManifestSchemaVersion, cold.Schema);
        Assert.All(cold.Entries, entry => Assert.False(string.IsNullOrEmpty(entry.ContentHash)));
        Assert.Equal("/remote/app.dll", Assert.Single(target.Uploads));

        // Same bytes, brand-new mtime — exactly what a rebuild from another worktree produces.
        target.Uploads.Clear();
        File.SetLastWriteTimeUtc(file, new DateTime(2026, 5, 27, 9, 8, 7, DateTimeKind.Utc));

        await Sync(sourceRoot, target, previous: cold);

        Assert.Empty(target.Uploads);
        Assert.Empty(target.Deletes);
    }

    // Same byte count, different bytes: proves the skip keys on content, not size.
    [Fact]
    public async Task UploadsFileWhenContentChangesEvenAtSameSize()
    {
        using var temp = new TempWorkspace();
        var sourceRoot = temp.CreateDir("publish");
        var file = Path.Combine(sourceRoot, "app.dll");
        await File.WriteAllTextAsync(file, "AAAA");

        var target = new RecordingSyncTarget();
        var cold = await Sync(sourceRoot, target, previous: null);

        target.Uploads.Clear();
        await File.WriteAllTextAsync(file, "BBBB");

        await Sync(sourceRoot, target, previous: cold);

        Assert.Equal("/remote/app.dll", Assert.Single(target.Uploads));
    }

    // The manifest is the content authority, but the remote listing is an existence guard:
    // a file deleted out-of-band on the target must be re-sent even though its hash still matches.
    [Fact]
    public async Task ReuploadsWhenManifestMatchesButRemoteFileMissing()
    {
        using var temp = new TempWorkspace();
        var sourceRoot = temp.CreateDir("publish");
        var file = Path.Combine(sourceRoot, "app.dll");
        await File.WriteAllTextAsync(file, "bytes");

        var target = new RecordingSyncTarget();
        var cold = await Sync(sourceRoot, target, previous: null);

        target.RemoveRemote("/remote/app.dll");
        target.Uploads.Clear();

        await Sync(sourceRoot, target, previous: cold);

        Assert.Equal("/remote/app.dll", Assert.Single(target.Uploads));
    }

    [Fact]
    public async Task DeletesManifestOwnedStaleFilesAndPreservesUnmanagedFiles()
    {
        using var temp = new TempWorkspace();
        var sourceRoot = temp.CreateDir("publish");
        var a = Path.Combine(sourceRoot, "a.dll");
        var b = Path.Combine(sourceRoot, "b.dll");
        await File.WriteAllTextAsync(a, "a bytes");
        await File.WriteAllTextAsync(b, "b bytes");

        var target = new RecordingSyncTarget();
        var cold = await Sync(sourceRoot, target, previous: null);

        target.SeedRemote("/remote/unmanaged.txt", size: 4);
        target.Uploads.Clear();
        File.Delete(b);

        var warm = await Sync(sourceRoot, target, previous: cold);

        Assert.Contains("/remote/b.dll", target.Deletes);
        Assert.DoesNotContain("/remote/unmanaged.txt", target.Deletes);
        Assert.Empty(target.Uploads);
        Assert.Contains(warm.Entries, entry => entry.Path == "a.dll");
        Assert.DoesNotContain(warm.Entries, entry => entry.Path == "b.dll");
    }

    // The engine should hand the whole changed set to UploadManyAsync in a single call — that is
    // what lets the archive transport collapse N per-file round-trips into one.
    [Fact]
    public async Task DispatchesAllChangedFilesAsOneBatch()
    {
        using var temp = new TempWorkspace();
        var sourceRoot = temp.CreateDir("publish");
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "a.dll"), "a");
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "b.dll"), "b");
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "c.dll"), "c");

        var target = new RecordingSyncTarget();
        await Sync(sourceRoot, target, previous: null);

        Assert.Equal(new[] { 3 }, target.Batches);
        Assert.Equal(3, target.Uploads.Count);
    }

    // --- Partial-failure semantics (roadmap #7) -------------------------------------------------
    // The trust guarantee: a sync that fails partway must NOT advance the manifest, so the next run
    // re-diffs against the last *fully synced* state and converges. The engine enforces this by
    // building the manifest only after every delete and upload has succeeded (it is the method's
    // return value), so any partial failure throws before a manifest exists — which makes the
    // caller's post-await `state.SaveArtifactManifest(...)` unreachable (RoamCommands.cs).

    // Upload fails after some files have landed: the engine must propagate (not swallow-and-return),
    // and only the pre-failure files are on the target. No manifest is produced.
    [Fact]
    public async Task PartialUploadFailureThrowsAndProducesNoManifest()
    {
        using var temp = new TempWorkspace();
        var sourceRoot = temp.CreateDir("publish");
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "a.dll"), "a");
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "b.dll"), "b");
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "c.dll"), "c");

        var target = new RecordingSyncTarget { UploadFailAfter = 1 };

        // The throw IS the guarantee: SyncAsync never returns a manifest on partial failure.
        await Assert.ThrowsAsync<IOException>(() => Sync(sourceRoot, target, previous: null));

        // Files are uploaded in Ordinal path order; only the first landed before the failure.
        Assert.Equal("/remote/a.dll", Assert.Single(target.Uploads));
        Assert.Equal("a", target.RemoteContent("/remote/a.dll"));
        Assert.Null(target.RemoteContent("/remote/b.dll"));
    }

    // Delete fails: deletes run before uploads, so a delete failure must abort the whole sync
    // before any upload is attempted, and produce no manifest.
    [Fact]
    public async Task DeleteFailureAbortsBeforeUploadsAndProducesNoManifest()
    {
        using var temp = new TempWorkspace();
        var sourceRoot = temp.CreateDir("publish");
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "a.dll"), "a");
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "b.dll"), "b");

        var target = new RecordingSyncTarget();
        var cold = await Sync(sourceRoot, target, previous: null);

        // Remove b from source so it is a manifest-owned stale delete on the next run, then fail it.
        File.Delete(Path.Combine(sourceRoot, "b.dll"));
        target.Uploads.Clear();
        target.FailDeletes = true;

        await Assert.ThrowsAsync<IOException>(() => Sync(sourceRoot, target, previous: cold));

        Assert.Contains("/remote/b.dll", target.DeleteAttempts);  // the stale delete WAS attempted
        Assert.Empty(target.Deletes);                              // ...but did not succeed
        Assert.Empty(target.Uploads);                              // ...and no upload ran after it
    }

    // The convergence proof: after a partial-upload failure (manifest deliberately NOT advanced,
    // exactly as production leaves it), a retry against the *same prior manifest* re-sends every
    // changed file — including the one that already landed, which is the intended fail-safe — and
    // ends with the target fully correct.
    [Fact]
    public async Task RetryAfterPartialFailureConvergesAgainstUnadvancedManifest()
    {
        using var temp = new TempWorkspace();
        var sourceRoot = temp.CreateDir("publish");
        foreach (var name in new[] { "a", "b", "c" })
        {
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, $"{name}.dll"), "v1-aaaa");
        }

        var target = new RecordingSyncTarget();
        var cold = await Sync(sourceRoot, target, previous: null);

        // Change all three; same length, different bytes (the diff is content-keyed, not size-keyed).
        foreach (var name in new[] { "a", "b", "c" })
        {
            await File.WriteAllTextAsync(Path.Combine(sourceRoot, $"{name}.dll"), "v2-bbbb");
        }

        // Failed run: a lands v2, b throws. Production would not save, so the baseline stays `cold`.
        target.Uploads.Clear();
        target.UploadFailAfter = 1;
        await Assert.ThrowsAsync<IOException>(() => Sync(sourceRoot, target, previous: cold));
        Assert.Equal("/remote/a.dll", Assert.Single(target.Uploads));

        // Retry against the un-advanced baseline (`cold`), now healthy.
        target.Uploads.Clear();
        target.UploadFailAfter = int.MaxValue;
        var warm = await Sync(sourceRoot, target, previous: cold);

        // All three re-sent — including a, whose v2 already landed — because the baseline still
        // records v1 for it. Redundant re-send is the deliberate fail-safe direction.
        Assert.Equal(3, target.Uploads.Count);
        foreach (var name in new[] { "a", "b", "c" })
        {
            Assert.Equal("v2-bbbb", target.RemoteContent($"/remote/{name}.dll"));
        }
        Assert.All(warm.Entries, entry => Assert.False(string.IsNullOrEmpty(entry.ContentHash)));
    }

    // The literal roadmap assertion, exercised through the real StateStore: a failed sync, persisted
    // with the same success-only discipline as RoamCommands, leaves the prior manifest byte-for-byte
    // on disk. Guards against a future refactor moving the save before the await or into a finally.
    [Fact]
    public async Task FailedSyncLeavesPriorManifestOnDiskUnchanged()
    {
        using var temp = new TempWorkspace();
        var workspace = temp.CreateDir("ws");
        var sourceRoot = temp.CreateDir("publish");
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "a.dll"), "v1");
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "b.dll"), "v1");

        var state = new StateStore(workspace);
        state.EnsureInitialized();
        var target = new RecordingSyncTarget();

        // Cold sync, persisted exactly as RoamCommands does: save ONLY after SyncAsync returns.
        var cold = await Sync(sourceRoot, target, previous: null);
        state.SaveArtifactManifest("demo", cold);
        var manifestPath = Path.Combine(workspace, ".roam", "manifests", "demo", "artifacts.json");
        var afterCold = await File.ReadAllTextAsync(manifestPath);

        // Change a file so the next sync has work, then fail the upload on the first file.
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "a.dll"), "v2-changed");
        target.Uploads.Clear();
        target.UploadFailAfter = 0;

        // Mirror the caller's success-only-save discipline: the throw skips the save line.
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            var next = await Sync(sourceRoot, target, previous: cold);
            state.SaveArtifactManifest("demo", next);  // unreachable on a failed sync
        });

        Assert.Equal(afterCold, await File.ReadAllTextAsync(manifestPath));
    }

    private static Task<SyncManifest> Sync(string sourceRoot, ISyncTarget target, SyncManifest? previous)
        => MetadataDiffSyncEngine.SyncAsync(
            profileName: "demo",
            sourceRoot: sourceRoot,
            destinationRoot: "/remote",
            previousManifest: previous,
            target: target,
            sourceHost: null,
            buildHost: "build",
            targetHost: "target",
            workspace: null,
            deployPath: "/remote",
            flattenPublish: true,
            gitHead: null,
            CancellationToken.None);

    private sealed class TempWorkspace : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("roam-sync-engine-");

        public string CreateDir(string name)
        {
            var path = Path.Combine(_root.FullName, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose() => _root.Delete(recursive: true);
    }

    // In-memory ISyncTarget that records exactly which destination paths were uploaded or
    // deleted, and serves a remote listing from whatever it currently holds. Keyed by the full
    // destination path the engine builds (CombineRemotePath always uses forward slashes).
    private sealed class RecordingSyncTarget : ISyncTarget
    {
        private readonly Dictionary<string, (long Size, DateTimeOffset Mtime)> _remote = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _remoteContent = new(StringComparer.Ordinal);

        public List<string> Uploads { get; } = [];

        public List<string> Deletes { get; } = [];

        public List<string> DeleteAttempts { get; } = [];

        public List<int> Batches { get; } = [];

        // After this many uploads have been recorded (since the last Uploads.Clear()), the next
        // UploadFileAsync throws — simulates an upload that fails partway through a batch.
        public int UploadFailAfter { get; set; } = int.MaxValue;

        // When set, every DeleteFileAsync throws after recording the attempt.
        public bool FailDeletes { get; set; }

        public async Task UploadManyAsync(IReadOnlyList<SyncFileUpload> uploads, string destinationRoot, CancellationToken cancellationToken)
        {
            Batches.Add(uploads.Count);
            foreach (var upload in uploads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await UploadFileAsync(upload.LocalPath, upload.DestinationPath, upload.LastWriteTimeUtc, cancellationToken);
            }
        }

        public Task<IReadOnlyList<RemoteFileEntry>> ListFilesAsync(string root, CancellationToken cancellationToken)
        {
            var prefix = root.TrimEnd('/', '\\') + "/";
            IReadOnlyList<RemoteFileEntry> result = _remote
                .Select(kv => new RemoteFileEntry(
                    kv.Key.StartsWith(prefix, StringComparison.Ordinal) ? kv.Key[prefix.Length..] : kv.Key,
                    kv.Value.Size,
                    kv.Value.Mtime))
                .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
                .ToArray();
            return Task.FromResult(result);
        }

        public Task UploadFileAsync(string localPath, string destinationPath, DateTimeOffset lastWriteTimeUtc, CancellationToken cancellationToken)
        {
            if (Uploads.Count >= UploadFailAfter)
            {
                throw new IOException($"simulated upload failure after {Uploads.Count} file(s): {destinationPath}");
            }

            _remote[destinationPath] = (new FileInfo(localPath).Length, lastWriteTimeUtc);
            _remoteContent[destinationPath] = File.ReadAllText(localPath);
            Uploads.Add(destinationPath);
            return Task.CompletedTask;
        }

        public Task DeleteFileAsync(string path, CancellationToken cancellationToken)
        {
            DeleteAttempts.Add(path);
            if (FailDeletes)
            {
                throw new IOException($"simulated delete failure: {path}");
            }

            _remote.Remove(path);
            _remoteContent.Remove(path);
            Deletes.Add(path);
            return Task.CompletedTask;
        }

        public void SeedRemote(string destinationPath, long size) => _remote[destinationPath] = (size, DateTimeOffset.UnixEpoch);

        public void RemoveRemote(string destinationPath) => _remote.Remove(destinationPath);

        // The content last uploaded to a destination path, or null if nothing landed there.
        public string? RemoteContent(string destinationPath)
            => _remoteContent.TryGetValue(destinationPath, out var content) ? content : null;

        public void Dispose()
        {
        }
    }
}
