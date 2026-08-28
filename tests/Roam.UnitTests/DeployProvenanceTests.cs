using Xunit;

namespace Roam.UnitTests;

public sealed class DeployProvenanceTests
{
    // The version reader must read a real managed assembly's metadata without loading it. Point it at
    // a known-managed assembly already on disk (System.Private.CoreLib) and assert it surfaces a
    // version. A native/garbage file must yield null (the "skip non-managed" contract).
    [Fact]
    public void AssemblyVersionReader_ReadsManagedAssembly_AndRejectsNonManaged()
    {
        var coreLib = typeof(object).Assembly.Location;
        var info = AssemblyVersionReader.Read(coreLib);
        Assert.NotNull(info);
        Assert.NotEqual("(unknown)", info!.Display);

        var garbage = Path.Combine(Path.GetTempPath(), $"roam-not-an-assembly-{Guid.NewGuid():N}.dll");
        File.WriteAllBytes(garbage, [0x00, 0x01, 0x02, 0x03, 0x04]);
        try
        {
            Assert.Null(AssemblyVersionReader.Read(garbage));
        }
        finally
        {
            File.Delete(garbage);
        }
    }

    // Scan must (1) include managed assemblies whose file name matches a provenance glob, carrying
    // the artifacts.json content hash, and (2) skip a glob-matching file that isn't a managed
    // assembly (a native DLL). flatten-publish layout: manifest paths are publish-root-relative.
    [Fact]
    public void Scan_IncludesManagedGlobMatch_SkipsNativeAndUnmatched()
    {
        var publishRoot = Directory.CreateTempSubdirectory("roam-provenance-").FullName;
        try
        {
            // A real managed assembly, renamed to look like a library we care about.
            var managed = Path.Combine(publishRoot, "Example.Devices.dll");
            File.Copy(typeof(object).Assembly.Location, managed);

            // A glob-matching file that is NOT a managed assembly.
            var native = Path.Combine(publishRoot, "Example.Native.dll");
            File.WriteAllBytes(native, [0x00, 0x01, 0x02]);

            // An unrelated managed assembly that does not match the glob.
            var other = Path.Combine(publishRoot, "App.dll");
            File.Copy(typeof(object).Assembly.Location, other);

            var entries = new ManifestEntry[]
            {
                new("Example.Devices.dll", new FileInfo(managed).Length, 0, "hash-managed"),
                new("Example.Native.dll", new FileInfo(native).Length, 0, "hash-native"),
                new("App.dll", new FileInfo(other).Length, 0, "hash-app"),
            };

            var result = DeployProvenance.Scan(entries, publishRoot, flattenPublish: true, ["Example.*"], "App");

            var assembly = Assert.Single(result);
            Assert.Equal("Example.Devices.dll", assembly.Path);
            Assert.Equal("hash-managed", assembly.ContentHash);
            Assert.NotEqual("(unknown)", assembly.Display);
        }
        finally
        {
            Directory.Delete(publishRoot, recursive: true);
        }
    }

    // With no globs configured, Scan falls back to the project's own primary output assembly.
    [Fact]
    public void Scan_DefaultsToProjectPrimaryAssembly_WhenNoGlobs()
    {
        var publishRoot = Directory.CreateTempSubdirectory("roam-provenance-default-").FullName;
        try
        {
            File.Copy(typeof(object).Assembly.Location, Path.Combine(publishRoot, "App.dll"));
            File.Copy(typeof(object).Assembly.Location, Path.Combine(publishRoot, "Example.Devices.dll"));

            var entries = new ManifestEntry[]
            {
                new("App.dll", 1, 0, "h1"),
                new("Example.Devices.dll", 1, 0, "h2"),
            };

            var result = DeployProvenance.Scan(entries, publishRoot, flattenPublish: true, globs: null, "App");

            var assembly = Assert.Single(result);
            Assert.Equal("App.dll", assembly.Path);
        }
        finally
        {
            Directory.Delete(publishRoot, recursive: true);
        }
    }

    // Non-flatten layout: artifacts.json paths are prefixed with the publish folder name; Scan must
    // strip that prefix to find the assembly on disk under the publish root.
    [Fact]
    public void Scan_ResolvesAssembly_UnderNonFlattenLayout()
    {
        var deployStaging = Directory.CreateTempSubdirectory("roam-provenance-nonflatten-").FullName;
        try
        {
            var publishRoot = Path.Combine(deployStaging, "publish");
            Directory.CreateDirectory(publishRoot);
            File.Copy(typeof(object).Assembly.Location, Path.Combine(publishRoot, "App.dll"));

            // Non-flatten manifest entry: "<publishFolderName>/App.dll".
            var entries = new ManifestEntry[] { new("publish/App.dll", 1, 0, "h1") };

            var result = DeployProvenance.Scan(entries, publishRoot, flattenPublish: false, globs: null, "App");

            var assembly = Assert.Single(result);
            Assert.Equal("publish/App.dll", assembly.Path);
            Assert.NotEqual("(unknown)", assembly.Display);
        }
        finally
        {
            Directory.Delete(deployStaging, recursive: true);
        }
    }

    // The diff classifies each assembly relative to the previous deploy: unchanged (same version AND
    // same bytes), changed (different version), and new (absent before). Pure value logic.
    [Fact]
    public void Diff_Classifies_Unchanged_Changed_And_New()
    {
        var previous = new DeployedVersionsManifest(1, "demo", "t0",
        [
            new DeployedAssembly("Example.Devices.dll", "1.5.1-alpha.1", null, "1.5.1.0", "hashA"),
            new DeployedAssembly("Example.Ui.dll", "2.0.0", null, "2.0.0.0", "hashOld"),
        ]);

        var current = new DeployedVersionsManifest(1, "demo", "t1",
        [
            // Same version, same hash -> unchanged (the red flag).
            new DeployedAssembly("Example.Devices.dll", "1.5.1-alpha.1", null, "1.5.1.0", "hashA"),
            // Version changed -> changed.
            new DeployedAssembly("Example.Ui.dll", "2.1.0", null, "2.1.0.0", "hashNew"),
            // Absent before -> new.
            new DeployedAssembly("New.Lib.dll", "0.1.0", null, "0.1.0.0", "hashZ"),
        ]);

        var lines = DeployProvenance.Diff(previous, current).ToDictionary(l => l.Name);

        Assert.True(lines["Example.Devices.dll"].Unchanged);
        Assert.False(lines["Example.Devices.dll"].IsNew);

        Assert.False(lines["Example.Ui.dll"].Unchanged);
        Assert.Equal("2.0.0", lines["Example.Ui.dll"].Before);
        Assert.Equal("2.1.0", lines["Example.Ui.dll"].After);

        Assert.True(lines["New.Lib.dll"].IsNew);
        Assert.Equal("(new)", lines["New.Lib.dll"].Before);
    }

    // Same version but DIFFERENT bytes must NOT be reported as unchanged — the bytes did change even
    // though the version label didn't, so it isn't the stale-package red flag.
    [Fact]
    public void Diff_SameVersionDifferentHash_IsNotUnchanged()
    {
        var previous = new DeployedVersionsManifest(1, "demo", "t0",
            [new DeployedAssembly("Lib.dll", "1.0.0", null, "1.0.0.0", "hashOld")]);
        var current = new DeployedVersionsManifest(1, "demo", "t1",
            [new DeployedAssembly("Lib.dll", "1.0.0", null, "1.0.0.0", "hashNew")]);

        var line = Assert.Single(DeployProvenance.Diff(previous, current));
        Assert.False(line.Unchanged);
    }

    // The provenance manifest must round-trip through StateStore (written to
    // .roam/manifests/<profile>/deployed-versions.json and read back) so the next deploy can diff.
    [Fact]
    public void StateStore_RoundTrips_DeployedVersionsManifest()
    {
        var root = Directory.CreateTempSubdirectory("roam-provenance-state-").FullName;
        try
        {
            var state = new StateStore(root);
            state.EnsureInitialized();
            Assert.Null(state.LoadDeployedVersionsManifest("demo"));

            var manifest = new DeployedVersionsManifest(1, "demo", "t0",
                [new DeployedAssembly("Example.Devices.dll", "1.5.1-alpha.1", "1.5.1.0", "1.5.1.0", "hashA")]);
            state.SaveDeployedVersionsManifest("demo", manifest);

            var loaded = state.LoadDeployedVersionsManifest("demo");
            Assert.NotNull(loaded);
            var assembly = Assert.Single(loaded!.Assemblies);
            Assert.Equal("Example.Devices.dll", assembly.Path);
            Assert.Equal("1.5.1-alpha.1", assembly.InformationalVersion);
            Assert.Equal("hashA", assembly.ContentHash);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
