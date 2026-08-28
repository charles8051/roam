using Xunit;

namespace Roam.UnitTests;

public sealed class PublishFingerprintTests
{
    // The headline case: re-running the same publish over an unchanged workspace must produce
    // the same fingerprint, otherwise the warm-publish skip never triggers.
    [Fact]
    public async Task ProducesStableFingerprint_OverRepeatedRunsOnSameInputs()
    {
        using var workspace = new TempWorkspace();
        var paths = workspace.WriteMinimalProject();

        var first = await ComputeAsync(paths, "publish-command-A");
        var second = await ComputeAsync(paths, "publish-command-A");

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(PublishFingerprint.FingerprintSchemaVersion, first.SchemaVersion);
        Assert.NotEmpty(first.Inputs);
    }

    // Touching a .cs file is the canonical real source change. Must invalidate the fingerprint.
    [Fact]
    public async Task FingerprintChanges_WhenCsFileEdited()
    {
        using var workspace = new TempWorkspace();
        var paths = workspace.WriteMinimalProject();

        var before = await ComputeAsync(paths, "cmd");
        await File.WriteAllTextAsync(Path.Combine(paths.ProjectDirectory, "Program.cs"), "// edited body\nclass C { }");
        var after = await ComputeAsync(paths, "cmd");

        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
    }

    // Same for the csproj — PackageReference bumps, TargetFramework changes, anything in here.
    [Fact]
    public async Task FingerprintChanges_WhenCsprojEdited()
    {
        using var workspace = new TempWorkspace();
        var paths = workspace.WriteMinimalProject();

        var before = await ComputeAsync(paths, "cmd");
        var csprojPath = paths.ProjectPath;
        await File.WriteAllTextAsync(csprojPath, await File.ReadAllTextAsync(csprojPath) + "\n<!-- bump -->");
        var after = await ComputeAsync(paths, "cmd");

        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
    }

    // Embedded resources can be anything inside the project tree. A .resx (or any other file)
    // inside the project dir is an input the SDK might glob; it must invalidate the fingerprint.
    [Fact]
    public async Task FingerprintChanges_WhenEmbeddedResourceEdited()
    {
        using var workspace = new TempWorkspace();
        var paths = workspace.WriteMinimalProject();
        var resxPath = Path.Combine(paths.ProjectDirectory, "Strings.resx");
        await File.WriteAllTextAsync(resxPath, "<resources><data name=\"hi\"><value>hello</value></data></resources>");

        var before = await ComputeAsync(paths, "cmd");
        await File.WriteAllTextAsync(resxPath, "<resources><data name=\"hi\"><value>hi</value></data></resources>");
        var after = await ComputeAsync(paths, "cmd");

        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
    }

    // The publish command string captures every publish-affecting roamfile field (RID,
    // configuration, self-contained, framework, ContinuousIntegrationBuild). A different command
    // line must produce a different fingerprint — that's how `publish.rid: linux-x64 → arm64`
    // forces a re-publish without hashing the whole roamfile.
    [Fact]
    public async Task FingerprintChanges_WhenPublishCommandChanges()
    {
        using var workspace = new TempWorkspace();
        var paths = workspace.WriteMinimalProject();

        var first = await ComputeAsync(paths, "dotnet publish --runtime linux-x64");
        var second = await ComputeAsync(paths, "dotnet publish --runtime linux-arm64");

        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
    }

    // The ciBuild flag is part of the publish command in practice, but it's also passed
    // separately. Keep them distinct so an inadvertent (source!=build) flip can't masquerade
    // as a same-fingerprint deploy.
    [Fact]
    public async Task FingerprintChanges_WhenCiBuildFlagFlips()
    {
        using var workspace = new TempWorkspace();
        var paths = workspace.WriteMinimalProject();

        var ci = await PublishFingerprint.ComputeAsync(paths, RoamNativePublish(), "cmd", ciBuild: true, CancellationToken.None);
        var nonCi = await PublishFingerprint.ComputeAsync(paths, RoamNativePublish(), "cmd", ciBuild: false, CancellationToken.None);

        Assert.NotEqual(ci.Fingerprint, nonCi.Fingerprint);
    }

    // bin/, obj/, and the rest of the excluded directories are MSBuild output, not input —
    // hashing them would invalidate the fingerprint on every build, defeating the whole point.
    [Fact]
    public async Task FingerprintUnchanged_WhenBinOrObjFilesAdded()
    {
        using var workspace = new TempWorkspace();
        var paths = workspace.WriteMinimalProject();

        var before = await ComputeAsync(paths, "cmd");

        // Drop some files into bin/ and obj/ as though dotnet publish had just run.
        Directory.CreateDirectory(Path.Combine(paths.ProjectDirectory, "bin", "Debug", "net10.0"));
        await File.WriteAllTextAsync(Path.Combine(paths.ProjectDirectory, "bin", "Debug", "net10.0", "App.dll"), "binary blob");
        Directory.CreateDirectory(Path.Combine(paths.ProjectDirectory, "obj", "Debug", "net10.0"));
        await File.WriteAllTextAsync(Path.Combine(paths.ProjectDirectory, "obj", "Debug", "net10.0", "App.csproj.AssemblyReference.cache"), "cache");

        var after = await ComputeAsync(paths, "cmd");

        Assert.Equal(before.Fingerprint, after.Fingerprint);
    }

    // launchSettings.json is consumed at runtime, not at publish. Editing it must NOT force a
    // re-publish — that's the kind of false-invalidation that defeats the warm-publish skip.
    [Fact]
    public async Task FingerprintUnchanged_WhenLaunchSettingsEdited()
    {
        using var workspace = new TempWorkspace();
        var paths = workspace.WriteMinimalProject();
        var launchSettings = Path.Combine(paths.ProjectDirectory, "Properties", "launchSettings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(launchSettings)!);
        await File.WriteAllTextAsync(launchSettings, "{ \"profiles\": { \"Development\": {} } }");

        var before = await ComputeAsync(paths, "cmd");
        await File.WriteAllTextAsync(launchSettings, "{ \"profiles\": { \"Development\": { \"commandName\": \"Project\" } } }");
        var after = await ComputeAsync(paths, "cmd");

        // launchSettings.json lives inside the project directory and is hashed as part of the
        // source tree. That's intentional: it's cheap and the user gets a re-publish even when
        // it wouldn't change publish output bytes. Document this in the test name if it ever
        // changes — but for now both sides hash it the same way, so editing it WILL change the
        // fingerprint. Validate that direction explicitly so the behaviour is locked in.
        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
    }

    // The transitive ProjectReference closure is the other half of "what counts as a source
    // change". A .cs file inside a referenced library must invalidate the consuming project's
    // fingerprint, otherwise warm-publish ships stale binaries on multi-project edits.
    [Fact]
    public async Task FingerprintChanges_WhenTransitiveProjectReferenceContentChanges()
    {
        using var workspace = new TempWorkspace();
        var paths = workspace.WriteMinimalProject();

        // Create a sibling library project NEXT TO the app (both under src/) and reference it
        // from the app with a relative path that actually resolves.
        var libDir = Path.Combine(workspace.Root, "src", "Lib");
        Directory.CreateDirectory(libDir);
        var libCsproj = Path.Combine(libDir, "Lib.csproj");
        await File.WriteAllTextAsync(libCsproj,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(libDir, "Lib.cs"), "namespace Lib; public class A { }");

        // Add the ProjectReference to the app csproj. App lives at src/App/, Lib at src/Lib/.
        await File.WriteAllTextAsync(paths.ProjectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>\n  <ItemGroup><ProjectReference Include=\"../Lib/Lib.csproj\" /></ItemGroup>\n</Project>");

        var before = await ComputeAsync(paths, "cmd");

        // Edit the library's source. Without ProjectReference traversal, this is invisible to
        // the fingerprint.
        await File.WriteAllTextAsync(Path.Combine(libDir, "Lib.cs"), "namespace Lib; public class A { public int X => 42; }");
        var after = await ComputeAsync(paths, "cmd");

        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
    }

    // Directory.Build.props/.targets are evaluated implicitly by MSBuild from every project
    // directory upward. They can change C#-visible defines, package versions, and output
    // layout — they must feed the fingerprint.
    [Fact]
    public async Task FingerprintChanges_WhenDirectoryBuildPropsEdited()
    {
        using var workspace = new TempWorkspace();
        var paths = workspace.WriteMinimalProject();
        var props = Path.Combine(workspace.Root, "Directory.Build.props");
        await File.WriteAllTextAsync(props, "<Project><PropertyGroup><Authors>roam</Authors></PropertyGroup></Project>");

        var before = await ComputeAsync(paths, "cmd");
        await File.WriteAllTextAsync(props, "<Project><PropertyGroup><Authors>roam-edited</Authors></PropertyGroup></Project>");
        var after = await ComputeAsync(paths, "cmd");

        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
    }

    // global.json pins SDK selection. Different SDK → different publish output. Must hash.
    [Fact]
    public async Task FingerprintChanges_WhenGlobalJsonEdited()
    {
        using var workspace = new TempWorkspace();
        var paths = workspace.WriteMinimalProject();
        var globalJson = Path.Combine(workspace.Root, "global.json");
        await File.WriteAllTextAsync(globalJson, "{ \"sdk\": { \"version\": \"10.0.100\" } }");

        var before = await ComputeAsync(paths, "cmd");
        await File.WriteAllTextAsync(globalJson, "{ \"sdk\": { \"version\": \"10.0.200\" } }");
        var after = await ComputeAsync(paths, "cmd");

        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
    }

    // The pubxml — when publish-profile: is in use — is the publish-shape source of truth. Edit
    // it, fingerprint changes.
    [Fact]
    public async Task FingerprintChanges_WhenReferencedPubxmlEdited()
    {
        using var workspace = new TempWorkspace();
        var paths = workspace.WriteMinimalProject();
        var pubxmlDir = Path.Combine(paths.ProjectDirectory, "Properties", "PublishProfiles");
        Directory.CreateDirectory(pubxmlDir);
        var pubxml = Path.Combine(pubxmlDir, "Linux64.pubxml");
        await File.WriteAllTextAsync(pubxml,
            "<Project><PropertyGroup><RuntimeIdentifier>linux-x64</RuntimeIdentifier><PublishDir>bin/Release/publish</PublishDir></PropertyGroup></Project>");
        var pubxmlSettings = new ResolvedPublishSettings(
            Name: "Linux64",
            UsePublishProfile: true,
            RuntimeIdentifier: "linux-x64",
            SelfContained: true,
            PublishDirectory: "bin/Release/publish",
            Configuration: "Release",
            TargetFramework: "net10.0");

        var before = await PublishFingerprint.ComputeAsync(paths, pubxmlSettings, "cmd", ciBuild: false, CancellationToken.None);
        await File.WriteAllTextAsync(pubxml,
            "<Project><PropertyGroup><RuntimeIdentifier>linux-x64</RuntimeIdentifier><PublishDir>bin/Release/publish</PublishDir><SelfContained>true</SelfContained></PropertyGroup></Project>");
        var after = await PublishFingerprint.ComputeAsync(paths, pubxmlSettings, "cmd", ciBuild: false, CancellationToken.None);

        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
    }

    // The full skip-decision plumbing: save a fingerprint to state, recompute, check the
    // private TrySkipPublish helper reports "skip". Belt-and-suspenders coverage of the
    // glue between StateStore, PublishFingerprint, and RoamCommands' skip gate — the bit
    // that actually saves the ~13s per config-only deploy in real use.
    [Fact]
    public async Task TrySkipPublish_ReturnsTrue_WhenFingerprintMatchesAndOutputDirHasFiles()
    {
        using var workspace = new TempWorkspace();
        var paths = workspace.WriteMinimalProject();
        var state = new StateStore(paths.WorkspaceRoot);
        state.EnsureInitialized();

        var publish = new ResolvedPublishSettings(
            Name: null,
            UsePublishProfile: false,
            RuntimeIdentifier: "linux-x64",
            SelfContained: true,
            PublishDirectory: "bin/publish",
            Configuration: "Release",
            TargetFramework: "net10.0");
        var fingerprint = await PublishFingerprint.ComputeAsync(paths, publish, "cmd", ciBuild: false, CancellationToken.None);

        // Pre-seed the manifest as though the last publish ran successfully…
        state.SavePublishManifest("demo", new PublishManifest(
            PublishFingerprint.FingerprintSchemaVersion,
            "demo",
            fingerprint.Fingerprint,
            "local-build",
            publish.PublishDirectory,
            DateTimeOffset.UtcNow.ToString("O"),
            fingerprint.Inputs));

        // …and that the publish output is still present.
        var publishRoot = Path.GetFullPath(publish.PublishDirectory, paths.ProjectDirectory);
        Directory.CreateDirectory(publishRoot);
        await File.WriteAllTextAsync(Path.Combine(publishRoot, "App.dll"), "binary bytes");

        Assert.True(InvokeTrySkipPublish(paths, publish, fingerprint, state, buildHostIsLocal: true, out var skipReason));
        Assert.Equal("fingerprint-match", skipReason);

        // A remote build host must always re-publish in v0, even with a matching fingerprint.
        Assert.False(InvokeTrySkipPublish(paths, publish, fingerprint, state, buildHostIsLocal: false, out var remoteReason));
        Assert.Equal("remote-build", remoteReason);

        // Delete the publish output: the cache hit must NOT trigger, otherwise sync-artifacts
        // finds nothing to ship and the kiosk runs whatever was last on disk (or fails).
        Directory.Delete(publishRoot, recursive: true);
        Assert.False(InvokeTrySkipPublish(paths, publish, fingerprint, state, buildHostIsLocal: true, out var missingReason));
        Assert.StartsWith("publish-output-missing", missingReason);
    }

    // Schema mismatch: a manifest written by an older roam (or a future roam) must NOT be
    // honoured. Treating it as a cache hit could ship stale binaries on an algorithm change.
    [Fact]
    public async Task TrySkipPublish_ReturnsFalse_WhenManifestSchemaDiffers()
    {
        using var workspace = new TempWorkspace();
        var paths = workspace.WriteMinimalProject();
        var state = new StateStore(paths.WorkspaceRoot);
        state.EnsureInitialized();

        var publish = new ResolvedPublishSettings(
            Name: null,
            UsePublishProfile: false,
            RuntimeIdentifier: "linux-x64",
            SelfContained: true,
            PublishDirectory: "bin/publish",
            Configuration: "Release",
            TargetFramework: "net10.0");
        var fingerprint = await PublishFingerprint.ComputeAsync(paths, publish, "cmd", ciBuild: false, CancellationToken.None);

        // Pretend a prior roam wrote the manifest at a non-current schema.
        state.SavePublishManifest("demo", new PublishManifest(
            Schema: PublishFingerprint.FingerprintSchemaVersion + 999,
            "demo",
            fingerprint.Fingerprint,
            "local-build",
            publish.PublishDirectory,
            DateTimeOffset.UtcNow.ToString("O"),
            fingerprint.Inputs));

        var publishRoot = Path.GetFullPath(publish.PublishDirectory, paths.ProjectDirectory);
        Directory.CreateDirectory(publishRoot);
        await File.WriteAllTextAsync(Path.Combine(publishRoot, "App.dll"), "binary bytes");

        Assert.False(InvokeTrySkipPublish(paths, publish, fingerprint, state, buildHostIsLocal: true, out var reason));
        Assert.StartsWith("schema-mismatch", reason);
    }

    private static bool InvokeTrySkipPublish(
        ResolvedProjectPaths paths,
        ResolvedPublishSettings publish,
        PublishFingerprintResult fingerprint,
        StateStore state,
        bool buildHostIsLocal,
        out string reason)
    {
        var buildHost = new HostResolution(
            Name: "local-build",
            SshHost: "localhost",
            User: Environment.UserName,
            Port: 22,
            IdentityFile: null,
            IdentityFiles: [],
            ProxyJump: null,
            Workspace: null,
            Os: "linux",
            IsLocal: buildHostIsLocal);

        var method = typeof(RoamCommands).GetMethod(
            "TrySkipPublish",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var args = new object?[] { "demo", paths, publish, buildHost, fingerprint, state, null };
        var result = (bool)method!.Invoke(null, args)!;
        reason = (string)args[6]!;
        return result;
    }

    // Files outside the project closure (e.g. an unrelated repo-level README) must not
    // contribute to the fingerprint — otherwise unrelated edits force re-publish.
    [Fact]
    public async Task FingerprintUnchanged_WhenWorkspaceFileOutsideProjectTreeEdited()
    {
        using var workspace = new TempWorkspace();
        var paths = workspace.WriteMinimalProject();
        var unrelated = Path.Combine(workspace.Root, "README.md");
        await File.WriteAllTextAsync(unrelated, "# Hi");

        var before = await ComputeAsync(paths, "cmd");
        await File.WriteAllTextAsync(unrelated, "# Hi, edited");
        var after = await ComputeAsync(paths, "cmd");

        Assert.Equal(before.Fingerprint, after.Fingerprint);
    }

    // Directory.Packages.props pins Central Package Management versions. Bumping a pin changes the
    // published bytes with no edit to any source file — exactly the class of change that shipped
    // stale binaries before v2 of the fingerprint. It lives at an ANCESTOR of the project dir
    // (workspace root), so this also exercises the ancestor walk for the new input.
    [Fact]
    public async Task FingerprintChanges_WhenDirectoryPackagesPropsEdited()
    {
        using var workspace = new TempWorkspace();
        var paths = workspace.WriteMinimalProject();
        var packages = Path.Combine(workspace.Root, "Directory.Packages.props");
        await File.WriteAllTextAsync(packages,
            "<Project><ItemGroup><PackageVersion Include=\"Serilog\" Version=\"3.1.0\" /></ItemGroup></Project>");

        var before = await ComputeAsync(paths, "cmd");
        await File.WriteAllTextAsync(packages,
            "<Project><ItemGroup><PackageVersion Include=\"Serilog\" Version=\"4.0.0\" /></ItemGroup></Project>");
        var after = await ComputeAsync(paths, "cmd");

        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
    }

    // nuget.config selects the package feeds (e.g. an in-development local feed) that decide which
    // bytes a given version resolves to. Repointing a feed must re-publish.
    [Fact]
    public async Task FingerprintChanges_WhenNuGetConfigEdited()
    {
        using var workspace = new TempWorkspace();
        var paths = workspace.WriteMinimalProject();
        var nugetConfig = Path.Combine(workspace.Root, "nuget.config");
        await File.WriteAllTextAsync(nugetConfig,
            "<configuration><packageSources><add key=\"local\" value=\"C:/feed-a\" /></packageSources></configuration>");

        var before = await ComputeAsync(paths, "cmd");
        await File.WriteAllTextAsync(nugetConfig,
            "<configuration><packageSources><add key=\"local\" value=\"C:/feed-b\" /></packageSources></configuration>");
        var after = await ComputeAsync(paths, "cmd");

        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
    }

    // THE regression test for the reported bug: a dependency change that moves NO source file in
    // the closure must still invalidate the fingerprint. obj/project.assets.json is the resolved
    // dependency graph (every package id + version + sha512); a transitive bump or a floating
    // version re-resolving to a newer local-feed build rewrites it. Before v2 this was invisible
    // (obj/ is excluded from the source walk) and roam shipped stale binaries on a warm deploy.
    [Fact]
    public async Task FingerprintChanges_WhenProjectAssetsJsonChanges_NoSourceEdit()
    {
        using var workspace = new TempWorkspace();
        var paths = workspace.WriteMinimalProject();
        var assets = Path.Combine(paths.ProjectDirectory, "obj", "project.assets.json");
        Directory.CreateDirectory(Path.GetDirectoryName(assets)!);
        await File.WriteAllTextAsync(assets,
            "{ \"version\": 3, \"libraries\": { \"Contoso.Lib/7.0.0-beta.3\": { \"sha512\": \"AAAA\" } } }");

        var before = await ComputeAsync(paths, "cmd");

        // Simulate a restore re-resolving the dependency to new bytes (new version + sha512) — the
        // app's source tree is untouched.
        await File.WriteAllTextAsync(assets,
            "{ \"version\": 3, \"libraries\": { \"Contoso.Lib/7.0.0-beta.4\": { \"sha512\": \"BBBB\" } } }");
        var after = await ComputeAsync(paths, "cmd");

        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
        Assert.Contains("src/App/obj/project.assets.json", after.Inputs);
    }

    // The resolved-graph capture must span the whole ProjectReference closure: a referenced
    // library re-resolving a package (its obj/project.assets.json changing) must invalidate the
    // consuming project's fingerprint too.
    [Fact]
    public async Task FingerprintChanges_WhenTransitiveProjectAssetsJsonChanges()
    {
        using var workspace = new TempWorkspace();
        var paths = workspace.WriteMinimalProject();

        var libDir = Path.Combine(workspace.Root, "src", "Lib");
        Directory.CreateDirectory(libDir);
        await File.WriteAllTextAsync(Path.Combine(libDir, "Lib.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(libDir, "Lib.cs"), "namespace Lib; public class A { }");
        await File.WriteAllTextAsync(paths.ProjectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>\n  <ItemGroup><ProjectReference Include=\"../Lib/Lib.csproj\" /></ItemGroup>\n</Project>");

        var libAssets = Path.Combine(libDir, "obj", "project.assets.json");
        Directory.CreateDirectory(Path.GetDirectoryName(libAssets)!);
        await File.WriteAllTextAsync(libAssets, "{ \"libraries\": { \"Dep/1.0.0\": { \"sha512\": \"X\" } } }");

        var before = await ComputeAsync(paths, "cmd");
        await File.WriteAllTextAsync(libAssets, "{ \"libraries\": { \"Dep/1.1.0\": { \"sha512\": \"Y\" } } }");
        var after = await ComputeAsync(paths, "cmd");

        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
    }

    // Guard against the new obj/project.assets.json input OVER-invalidating: a project.assets.json
    // that NuGet leaves byte-identical across restores (the common no-dependency-change case) must
    // produce a stable fingerprint, otherwise every config-only iteration re-publishes and the
    // optimization is dead. (assets.json is deterministic given unchanged restore inputs.)
    [Fact]
    public async Task FingerprintStable_WhenProjectAssetsJsonUnchanged()
    {
        using var workspace = new TempWorkspace();
        var paths = workspace.WriteMinimalProject();
        var assets = Path.Combine(paths.ProjectDirectory, "obj", "project.assets.json");
        Directory.CreateDirectory(Path.GetDirectoryName(assets)!);
        await File.WriteAllTextAsync(assets, "{ \"version\": 3, \"libraries\": {} }");

        var first = await ComputeAsync(paths, "cmd");
        var second = await ComputeAsync(paths, "cmd");

        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    // THE Mode-B regression test (schema 3): a local FOLDER-feed package re-packed at the SAME
    // version, with assets.json untouched (NuGet's cache keeps serving the old extraction, so the
    // recorded sha512 is unchanged), must still invalidate the fingerprint. Before schema 3 the
    // (id, version, sha512) coordinate looked identical and the publish was skipped on stale bytes.
    [Fact]
    public async Task FingerprintChanges_WhenLocalFeedNupkgBytesChange_AtSameVersion()
    {
        using var workspace = new TempWorkspace();
        var paths = workspace.WriteMinimalProject();

        // A local folder feed holding one package (flat layout: Id.Version.nupkg).
        var feedDir = Path.Combine(workspace.Root, "local-feed");
        Directory.CreateDirectory(feedDir);
        var nupkg = Path.Combine(feedDir, "Example.Devices.1.5.1-alpha.1.nupkg");
        await File.WriteAllBytesAsync(nupkg, [1, 2, 3]);

        // nuget.config at the workspace root points at the folder feed.
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "nuget.config"),
            $"<configuration><packageSources><add key=\"local\" value=\"{feedDir.Replace('\\', '/')}\" /></packageSources></configuration>");

        // The resolved graph records the package at that version. We deliberately do NOT touch
        // assets.json between runs — that's the whole premise of Mode B (cached sha512 unchanged).
        var assets = Path.Combine(paths.ProjectDirectory, "obj", "project.assets.json");
        Directory.CreateDirectory(Path.GetDirectoryName(assets)!);
        await File.WriteAllTextAsync(assets,
            "{ \"libraries\": { \"Example.Devices/1.5.1-alpha.1\": { \"type\": \"package\", \"sha512\": \"CACHED\" } } }");

        var before = await ComputeAsync(paths, "cmd");
        Assert.Contains("localfeed:Example.Devices/1.5.1-alpha.1", before.Inputs);

        // Re-pack the SAME version with different bytes; assets.json stays identical.
        await File.WriteAllBytesAsync(nupkg, [9, 9, 9, 9]);
        var after = await ComputeAsync(paths, "cmd");

        Assert.NotEqual(before.Fingerprint, after.Fingerprint);
    }

    // The counterpart guard: an HTTP-feed package contributes no file hash, so re-running with the
    // same assets.json is a stable fingerprint (no spurious republish). A version on nuget.org is
    // immutable, so its coordinate is trustworthy and roam must NOT fold anything extra in.
    [Fact]
    public async Task FingerprintStable_WhenHttpFeedPackageUnchanged_AtSameVersion()
    {
        using var workspace = new TempWorkspace();
        var paths = workspace.WriteMinimalProject();

        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "nuget.config"),
            "<configuration><packageSources><add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\" /></packageSources></configuration>");

        var assets = Path.Combine(paths.ProjectDirectory, "obj", "project.assets.json");
        Directory.CreateDirectory(Path.GetDirectoryName(assets)!);
        await File.WriteAllTextAsync(assets,
            "{ \"libraries\": { \"Serilog/3.1.0\": { \"type\": \"package\", \"sha512\": \"ABC\" } } }");

        var first = await ComputeAsync(paths, "cmd");
        var second = await ComputeAsync(paths, "cmd");

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.DoesNotContain(first.Inputs, i => i.StartsWith("localfeed:", StringComparison.Ordinal));
    }

    // Even with a folder feed configured, a package that is NOT present in it (resolved from an HTTP
    // feed instead) must contribute no file hash — only packages actually found in a folder source
    // are content-keyed.
    [Fact]
    public async Task FingerprintHasNoLocalFeedInput_WhenPackageNotInFolderFeed()
    {
        using var workspace = new TempWorkspace();
        var paths = workspace.WriteMinimalProject();

        var feedDir = Path.Combine(workspace.Root, "local-feed");
        Directory.CreateDirectory(feedDir); // empty feed
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "nuget.config"),
            $"<configuration><packageSources><add key=\"local\" value=\"{feedDir.Replace('\\', '/')}\" /></packageSources></configuration>");

        var assets = Path.Combine(paths.ProjectDirectory, "obj", "project.assets.json");
        Directory.CreateDirectory(Path.GetDirectoryName(assets)!);
        await File.WriteAllTextAsync(assets,
            "{ \"libraries\": { \"Serilog/3.1.0\": { \"type\": \"package\", \"sha512\": \"ABC\" } } }");

        var result = await ComputeAsync(paths, "cmd");
        Assert.DoesNotContain(result.Inputs, i => i.StartsWith("localfeed:", StringComparison.Ordinal));
    }

    private static Task<PublishFingerprintResult> ComputeAsync(ResolvedProjectPaths paths, string publishCommand)
        => PublishFingerprint.ComputeAsync(paths, RoamNativePublish(), publishCommand, ciBuild: false, CancellationToken.None);

    private static ResolvedPublishSettings RoamNativePublish()
        => new(
            Name: null,
            UsePublishProfile: false,
            RuntimeIdentifier: "linux-x64",
            SelfContained: true,
            PublishDirectory: "obj/roam/demo/publish",
            Configuration: "Release",
            TargetFramework: "net10.0");

    private sealed class TempWorkspace : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("roam-publish-fingerprint-");

        public string Root => _root.FullName;

        // Writes a minimal but realistic single-project workspace: a workspace root containing
        // a src/App/ subdirectory with a csproj and a Program.cs. The roamfile.yaml is written
        // too so ResolvedProjectPaths fields are populated consistently — even though the
        // fingerprint deliberately doesn't hash the roamfile, the path resolution does.
        public ResolvedProjectPaths WriteMinimalProject()
        {
            var workspaceRoot = _root.FullName;
            var projectDirectory = Path.Combine(workspaceRoot, "src", "App");
            Directory.CreateDirectory(projectDirectory);

            var projectPath = Path.Combine(projectDirectory, "App.csproj");
            File.WriteAllText(projectPath,
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>\n</Project>");
            File.WriteAllText(Path.Combine(projectDirectory, "Program.cs"), "class C { static void Main() { } }");

            var roamfilePath = Path.Combine(workspaceRoot, "roamfile.yaml");
            File.WriteAllText(roamfilePath, "version: 1\ncsproj: src/App/App.csproj\n");

            return new ResolvedProjectPaths(
                workspaceRoot,
                roamfilePath,
                projectPath,
                projectDirectory,
                "App",
                SolutionPath: null);
        }

        public void Dispose() => _root.Delete(recursive: true);
    }
}
