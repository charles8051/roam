using System.IO.Hashing;
using System.Text;
using System.Xml.Linq;

namespace Roam;

// Captures a content fingerprint of every byte that feeds into `dotnet publish` for a profile,
// so the publish step can be skipped on config-only iterations (deploy.start tweaks, hosts:
// edits, installer-script changes) where re-running publish would produce the byte-identical
// output the sync-artifacts content-hash diff already knows how to ship as zero new files.
//
// What feeds the fingerprint:
//   - The full file tree of the profile's csproj plus every <ProjectReference>-reachable
//     csproj, walking bin/obj/.vs/.git/.idea/.vscode/.roam/node_modules out of the way. This
//     covers .cs, .csproj, embedded resources (.resx, .json, images — anything the SDK might
//     glob), PackageReference declarations, and anything else inside the project tree.
//   - Directory.Build.props/.targets AND Directory.Packages.props at each project directory and
//     every ancestor up to (and including) the workspace root, since MSBuild evaluates them
//     implicitly. Directory.Packages.props pins Central Package Management versions — bumping a
//     pin changes the published bytes with no edit to any source file.
//   - nuget.config at each project directory and every ancestor up to the workspace root: it
//     selects the package feeds (a local feed, a private mirror) that determine which package
//     bytes a given version resolves to.
//   - obj/project.assets.json for each project in the closure — the RESOLVED NuGet dependency
//     graph (every resolved package id, version, and sha512 content hash). This is the input that
//     catches a dependency change that moves no source file: a transitive bump, or a floating
//     version (e.g. 1.2.* / *-alpha.*) resolving to a newer local-feed build. obj/ is otherwise
//     excluded from the source walk; this one file is read out of it deliberately. Caveat:
//     assets.json reflects only the LAST `dotnet restore`, so a floating-version change that has
//     not been restored yet is the one residual blind spot (see the issue tracker / Decision below).
//   - The actual .nupkg FILE hash of every resolved package whose restore SOURCE is a local FOLDER
//     feed (LocalFeedResolver, schema 3). This is the only signal for a same-version re-pack of a
//     folder-feed package: NuGet's global cache can keep serving the old extraction, so assets.json
//     still records the cached sha512 and the (id, version, sha512) coordinate looks unchanged.
//     Hashing the on-disk .nupkg catches it. HTTP-feed packages (nuget.org, GitHub Packages) are
//     untouched — their version coordinate is immutable. This fixes the skip-publish half only; the
//     NuGet global-cache extraction itself needs a forced/clean restore (tracked in the issue tracker).
//   - global.json at the workspace root, since it pins SDK selection.
//   - The publish-profile .pubxml file, when `publish-profile:` is in use.
//   - The publish command line PublishCommandBuilder would invoke — a stable summary of RID,
//     configuration, self-contained, framework, target framework, ContinuousIntegrationBuild,
//     and any other CLI-visible setting the roamfile shapes through the publish block.
//   - The fingerprint schema version, so an algorithm bump invalidates every cached manifest.
//
// What deliberately does NOT feed the fingerprint:
//   - The full roamfile.yaml. Only the publish-affecting parts (captured via the publish command
//     and any referenced pubxml) count — editing deploy.start, hosts.<x>.ssh, or any other
//     non-publish field must NOT invalidate the publish. That's the headline "config-only
//     iteration drops 25s → 7s" case the tracked follow-up exists to enable.
//   - launchSettings.json — runtime concern, not publish output.
//   - bin/, obj/, .vs/, .vscode/, .roam/, .git/, .idea/, node_modules/ — tool-generated noise
//     or roam-owned state, not input. The sole exception is obj/project.assets.json, read
//     explicitly above because it is the only on-disk record of the resolved dependency graph.
public static class PublishFingerprint
{
    // Bump when the set of inputs or how they're hashed changes. Older manifests at a different
    // schema are treated as a cache miss — the next publish runs and rewrites the manifest at
    // the new schema. Never silently honour a manifest at an unknown schema.
    //
    // v2 (2026-06): added Directory.Packages.props (CPM version pins), nuget.config (feed
    // selection), and obj/project.assets.json (resolved dependency graph) to close the
    // dependency-change blind spot that let a warm publish ship stale binaries after a package
    // bump / floating-version rebuild with no source edit.
    // v3 (2026-06): added the actual .nupkg FILE hash of every resolved package that lives in a
    // local FOLDER feed (LocalFeedResolver), instead of trusting only the assets.json-recorded
    // sha512. A folder-feed package can be re-packed at the SAME version while NuGet's global cache
    // keeps serving the old extraction (so assets.json still records the cached sha512); folding the
    // file hash makes that same-version re-pack a fingerprint MISS. HTTP-feed packages are unchanged
    // — their version coordinate is immutable and trustworthy. The v2->v3 bump makes the first run
    // after upgrade a guaranteed republish (old manifests are a schema mismatch). NOTE: this fixes
    // the skip-PUBLISH half of the footgun only; bypassing NuGet's global-cache extraction itself is
    // a separate forced/clean-restore cure tracked in the issue tracker.
    public const int FingerprintSchemaVersion = 3;

    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".idea", ".vscode", ".roam", "node_modules",
    };

    public static async Task<PublishFingerprintResult> ComputeAsync(
        ResolvedProjectPaths paths,
        ResolvedPublishSettings publish,
        string publishCommand,
        bool ciBuild,
        CancellationToken cancellationToken)
    {
        var projectClosure = new SortedSet<string>(StringComparer.Ordinal);
        EnumerateProjectClosure(paths.ProjectPath, projectClosure);

        // SortedDictionary keeps inputs in canonical order — same workspace + same content →
        // same fingerprint regardless of filesystem enumeration order.
        var inputs = new SortedDictionary<string, string>(StringComparer.Ordinal);

        // 1. Source trees of each project in the closure.
        foreach (var projectPath in projectClosure)
        {
            var projectDirectory = Path.GetDirectoryName(projectPath);
            if (string.IsNullOrWhiteSpace(projectDirectory) || !Directory.Exists(projectDirectory))
            {
                continue;
            }

            foreach (var file in EnumerateProjectFiles(projectDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = MakeRelative(paths.WorkspaceRoot, file);
                inputs[relative] = await HashFileAsync(file, cancellationToken);
            }
        }

        // 2. Implicit per-directory MSBuild/NuGet inputs at each project dir and every ancestor
        //    up to the workspace root: Directory.Build.props/.targets, Directory.Packages.props
        //    (CPM version pins), and nuget.config (feed selection).
        var workspaceRootFull = Path.GetFullPath(paths.WorkspaceRoot);
        var msbuildDirs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var projectPath in projectClosure)
        {
            var current = Path.GetDirectoryName(projectPath);
            while (!string.IsNullOrEmpty(current)
                   && IsWithinWorkspace(current, workspaceRootFull))
            {
                msbuildDirs.Add(current);
                if (string.Equals(Path.GetFullPath(current), workspaceRootFull, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                current = Path.GetDirectoryName(current);
            }
        }

        foreach (var dir in msbuildDirs)
        {
            // Directory.Build.props/.targets are the MSBuild implicit-import chain;
            // Directory.Packages.props pins Central Package Management versions. All three are
            // evaluated implicitly from the project directory upward and all three change the
            // published bytes, so all three feed the fingerprint.
            foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" })
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path))
                {
                    var relative = MakeRelative(paths.WorkspaceRoot, path);
                    inputs[relative] = await HashFileAsync(path, cancellationToken);
                }
            }

            // nuget.config selects the package feeds that decide which bytes a version resolves
            // to (e.g. a local feed of in-development packages). NuGet matches the file name
            // case-insensitively; take the first casing that exists so a case-insensitive
            // filesystem doesn't double-hash one physical file, and so the input key is stable.
            foreach (var name in new[] { "nuget.config", "NuGet.Config", "NuGet.config" })
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path))
                {
                    var relative = MakeRelative(paths.WorkspaceRoot, path);
                    inputs[relative] = await HashFileAsync(path, cancellationToken);
                    break;
                }
            }
        }

        // 3. global.json at the workspace root pins SDK selection.
        var globalJsonPath = Path.Combine(paths.WorkspaceRoot, "global.json");
        if (File.Exists(globalJsonPath))
        {
            inputs["global.json"] = await HashFileAsync(globalJsonPath, cancellationToken);
        }

        // 4. publish-profile .pubxml content, if the profile uses one. (When publish.UsePublishProfile
        //    is false, the publish command itself carries every relevant flag.)
        if (publish.UsePublishProfile && !string.IsNullOrWhiteSpace(publish.Name))
        {
            var pubxmlPath = Path.Combine(paths.ProjectDirectory, "Properties", "PublishProfiles", $"{publish.Name}.pubxml");
            if (File.Exists(pubxmlPath))
            {
                var relative = MakeRelative(paths.WorkspaceRoot, pubxmlPath);
                inputs[relative] = await HashFileAsync(pubxmlPath, cancellationToken);
            }
        }

        // 5. obj/project.assets.json per closure project — the resolved NuGet dependency graph.
        //    This is the input that catches a dependency change moving no source file: a transitive
        //    package bump, or a floating version (1.2.* / *-alpha.*) re-resolving to a newer
        //    local-feed build. Each library entry carries a sha512, so a same-version content
        //    rebuild that NuGet actually re-extracts also changes the hash. obj/ is excluded from
        //    the source-tree walk, so this single file is read out of it explicitly. Absent when a
        //    project has never been restored — that's fine: with no assets there is no publish
        //    output either, so TrySkipPublish fails the output-exists guard and republishes anyway.
        foreach (var projectPath in projectClosure)
        {
            var projectDirectory = Path.GetDirectoryName(projectPath);
            if (string.IsNullOrWhiteSpace(projectDirectory))
            {
                continue;
            }

            var assetsPath = Path.Combine(projectDirectory, "obj", "project.assets.json");
            if (File.Exists(assetsPath))
            {
                var relative = MakeRelative(paths.WorkspaceRoot, assetsPath);
                inputs[relative] = await HashFileAsync(assetsPath, cancellationToken);
            }
        }

        // 6. Local FOLDER-feed package file hashes — the content-key fix (schema 3). For each
        //    resolved package whose restore source is a local folder feed, fold the actual .nupkg
        //    file hash so a same-version re-pack (invisible to the assets.json sha512 when NuGet
        //    serves a cached extraction) forces a republish. HTTP-feed packages contribute nothing.
        var localFeed = await LocalFeedResolver.ResolveAsync(paths.WorkspaceRoot, projectClosure, cancellationToken);

        // Combine: <relativePath>\0<fileHash>\0 per input, then the local-feed package hashes, then a
        // stable tail with the publish command, ciBuild flag, and schema version. \0 separators keep
        // "ab" + "c" distinct from "a" + "bc".
        var combined = new XxHash64();
        foreach (var (relative, fileHash) in inputs)
        {
            combined.Append(Encoding.UTF8.GetBytes(relative));
            combined.Append(NullByte);
            combined.Append(Encoding.UTF8.GetBytes(fileHash));
            combined.Append(NullByte);
        }
        foreach (var package in localFeed)
        {
            combined.Append(Encoding.UTF8.GetBytes($"localfeed={package.Id}/{package.Version}"));
            combined.Append(NullByte);
            combined.Append(Encoding.UTF8.GetBytes(package.FileHash));
            combined.Append(NullByte);
        }
        combined.Append(Encoding.UTF8.GetBytes($"command={publishCommand}"));
        combined.Append(NullByte);
        combined.Append(Encoding.UTF8.GetBytes($"ciBuild={ciBuild}"));
        combined.Append(NullByte);
        combined.Append(Encoding.UTF8.GetBytes($"schema={FingerprintSchemaVersion}"));
        combined.Append(NullByte);

        // Diagnostic input list: the hashed file paths plus a localfeed:<id>/<version> marker per
        // folder-feed package, so `publish.json`'s Inputs shows what the fingerprint considered.
        var diagnosticInputs = inputs.Keys
            .Concat(localFeed.Select(p => $"localfeed:{p.Id}/{p.Version}"))
            .ToArray();

        return new PublishFingerprintResult(
            combined.GetCurrentHashAsUInt64().ToString("x16"),
            FingerprintSchemaVersion,
            diagnosticInputs);
    }

    private static readonly byte[] NullByte = [0];

    // Iterative directory walk so deep trees don't blow the stack, with the bin/obj/.git/...
    // skip list applied at each level.
    private static IEnumerable<string> EnumerateProjectFiles(string projectDirectory)
    {
        var stack = new Stack<string>();
        stack.Push(projectDirectory);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            IEnumerable<string> files;
            IEnumerable<string> subdirs;
            try
            {
                files = Directory.EnumerateFiles(current);
                subdirs = Directory.EnumerateDirectories(current);
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            foreach (var subdir in subdirs)
            {
                if (ExcludedDirectoryNames.Contains(Path.GetFileName(subdir)))
                {
                    continue;
                }
                stack.Push(subdir);
            }
        }
    }

    // Walks <ProjectReference Include="..."> entries recursively from the entry csproj. Doesn't
    // honour MSBuild Conditions — over-inclusion is safe (more files hashed → fingerprint
    // changes more eagerly, never less). Cycles guarded by the visited set.
    private static void EnumerateProjectClosure(string projectPath, SortedSet<string> visited)
    {
        var canonical = Path.GetFullPath(projectPath);
        if (!visited.Add(canonical))
        {
            return;
        }

        if (!File.Exists(canonical))
        {
            return;
        }

        XDocument document;
        try
        {
            document = XDocument.Load(canonical);
        }
        catch (System.Xml.XmlException)
        {
            return;
        }

        var projectDirectory = Path.GetDirectoryName(canonical);
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            return;
        }

        foreach (var reference in document.Descendants("ProjectReference"))
        {
            var includeAttr = reference.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(includeAttr))
            {
                continue;
            }

            var includePath = includeAttr
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
            var referencedPath = Path.GetFullPath(Path.Combine(projectDirectory, includePath));
            EnumerateProjectClosure(referencedPath, visited);
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hasher = new XxHash64();
        await hasher.AppendAsync(stream, cancellationToken);
        return hasher.GetCurrentHashAsUInt64().ToString("x16");
    }

    private static bool IsWithinWorkspace(string candidate, string workspaceRootFull)
    {
        var candidateFull = Path.GetFullPath(candidate);
        if (string.Equals(candidateFull, workspaceRootFull, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = workspaceRootFull.EndsWith(Path.DirectorySeparatorChar)
            ? workspaceRootFull
            : workspaceRootFull + Path.DirectorySeparatorChar;
        return candidateFull.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string MakeRelative(string workspaceRoot, string fullPath)
        => Path.GetRelativePath(workspaceRoot, fullPath).Replace('\\', '/');
}

public sealed record PublishFingerprintResult(
    string Fingerprint,
    int SchemaVersion,
    IReadOnlyList<string> Inputs);
