using System.Reflection;
using Xunit;

namespace Roam.UnitTests;

public sealed class RoamCommandsArtifactSyncTests
{
    [Fact]
    public async Task SyncArtifactsAsyncLocalTargetDeletesOnlyManifestOwnedStaleFilesAndPreservesUnmanagedFiles()
    {
        var tempRoot = Directory.CreateTempSubdirectory("roam-artifact-sync-");
        try
        {
            var workspaceRoot = tempRoot.FullName;
            var projectDirectory = Path.Combine(workspaceRoot, "src", "SampleApp");
            var publishDirectory = Path.Combine(projectDirectory, "publish");
            var deployDirectory = Path.Combine(workspaceRoot, "deploy");
            Directory.CreateDirectory(publishDirectory);
            Directory.CreateDirectory(deployDirectory);

            var publishedArtifactPath = Path.Combine(publishDirectory, "app.dll");
            await File.WriteAllTextAsync(publishedArtifactPath, "new bits");

            var staleManagedPath = Path.Combine(deployDirectory, "stale-managed.txt");
            var unmanagedPath = Path.Combine(deployDirectory, "unmanaged-sentinel.txt");
            await File.WriteAllTextAsync(staleManagedPath, "old bits");
            await File.WriteAllTextAsync(unmanagedPath, "keep me");

            var state = new StateStore(workspaceRoot);
            state.EnsureInitialized();
            state.SaveArtifactManifest(
                "demo",
                new SyncManifest(
                    1,
                    "demo",
                    null,
                    "local-build",
                    "local-target",
                    null,
                    deployDirectory,
                    true,
                    null,
                    DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O"),
                    [
                        new ManifestEntry("stale-managed.txt", new FileInfo(staleManagedPath).Length, new DateTimeOffset(File.GetLastWriteTimeUtc(staleManagedPath)).ToUnixTimeMilliseconds() / 1000d)
                    ]));

            var method = typeof(RoamCommands).GetMethod("SyncArtifactsAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var commands = new RoamCommands();
            var task = (Task<ArtifactSyncResult>)method!.Invoke(
                commands,
                [
                    "demo",
                    new ResolvedProjectPaths(
                        workspaceRoot,
                        Path.Combine(workspaceRoot, "roamfile.yaml"),
                        Path.Combine(projectDirectory, "SampleApp.csproj"),
                        projectDirectory,
                        "SampleApp",
                        null),
                    new ResolvedPublishSettings("Default", true, null, false, "publish", null, null),
                    new HostResolution("local-build", "localhost", Environment.UserName, 22, null, [], null, null, "linux", true),
                    new HostResolution("local-target", "localhost", Environment.UserName, 22, null, [], null, null, "linux", true),
                    new DeploySpec(deployDirectory, true, null, null, null, 30, 250, false),
                    state,
                    CancellationToken.None
                ])!;

            var manifest = (await task).Manifest;

            Assert.False(File.Exists(staleManagedPath));
            Assert.True(File.Exists(unmanagedPath));
            Assert.Equal("keep me", await File.ReadAllTextAsync(unmanagedPath));
            Assert.True(File.Exists(Path.Combine(deployDirectory, "app.dll")));
            Assert.Contains(manifest.Entries, entry => entry.Path == "app.dll");
            Assert.DoesNotContain(manifest.Entries, entry => entry.Path == "stale-managed.txt");
        }
        finally
        {
            tempRoot.Delete(true);
        }
    }
}
