using System.IO.Hashing;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Roam;

// Feature 2: the structural half of the stale-local-feed fix. The publish fingerprint trusts the
// (id, version, sha512) coordinates project.assets.json records as a faithful identity for a
// package's bytes. For an HTTP feed (nuget.org, GitHub Packages) that holds — a version is
// immutable. For a LOCAL FOLDER feed it does NOT: a package can be re-packed at the same version,
// and because NuGet's global-packages cache already has that version extracted it keeps the old
// sha512 in assets.json, so the fingerprint sees "nothing changed" and skips publish on stale bytes
// (Mode B's skip-publish half). LocalFeedResolver closes that by hashing the actual .nupkg FILE in
// the folder feed and folding (id, version, fileHash) into the fingerprint — a same-version re-pack
// changes the file hash and forces a republish.
//
// Scope boundary: this fixes the *fingerprint skip*. It does NOT bypass the NuGet global-cache
// extraction itself (the cache may still serve old bytes to dotnet publish); a forced/clean restore
// is the separate cure, tracked in the issue tracker. HTTP-feed packages are untouched — their version
// coordinate is trustworthy, so they never contribute a file hash.
public static class LocalFeedResolver
{
    public static async Task<IReadOnlyList<LocalFeedPackageHash>> ResolveAsync(
        string workspaceRoot,
        IEnumerable<string> projectClosure,
        CancellationToken cancellationToken)
    {
        var configDirs = ConfigDirectories(workspaceRoot, projectClosure);

        // sourceKey -> resolved folder directory, for every folder (local-path / file://) source in
        // the effective config walk. Nearest-to-project config wins on a key collision; over-
        // inclusion (hashing a folder feed a package didn't actually restore from) only ever forces
        // an extra republish, which is the fail-safe direction.
        var folderSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // The nearest config that declares a non-empty packageSourceMapping wins entirely (NuGet does
        // not merge mappings across files). Empty => no mapping in effect => every folder source is a
        // candidate for every package.
        Dictionary<string, IReadOnlyList<Regex>>? sourceMapping = null;

        foreach (var dir in configDirs)
        {
            foreach (var configPath in NuGetConfigFiles(dir))
            {
                ParseConfig(configPath, folderSources, ref sourceMapping);
            }
        }

        if (folderSources.Count == 0)
        {
            return [];
        }

        var packages = ResolvedPackages(projectClosure);
        var result = new List<LocalFeedPackageHash>();

        foreach (var (id, version) in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var (sourceKey, folderDir) in CandidateSources(id, folderSources, sourceMapping))
            {
                var nupkg = FindNupkg(folderDir, id, version);
                if (nupkg is null)
                {
                    continue;
                }

                var hash = await HashFileAsync(nupkg, cancellationToken);
                result.Add(new LocalFeedPackageHash(id, version, sourceKey, hash));
                break; // first folder source that actually has the package wins
            }
        }

        return result
            .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Version, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // The project directory of each closure project plus every ancestor up to (and including) the
    // workspace root — the same walk the fingerprint uses for nuget.config, so the set of configs we
    // honor matches what already feeds the hash.
    private static IReadOnlyList<string> ConfigDirectories(string workspaceRoot, IEnumerable<string> projectClosure)
    {
        var workspaceRootFull = Path.GetFullPath(workspaceRoot);
        var dirs = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var projectPath in projectClosure)
        {
            var current = Path.GetDirectoryName(projectPath);
            while (!string.IsNullOrEmpty(current) && IsWithinWorkspace(current, workspaceRootFull))
            {
                var full = Path.GetFullPath(current);
                if (seen.Add(full))
                {
                    dirs.Add(full);
                }

                if (string.Equals(full, workspaceRootFull, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                current = Path.GetDirectoryName(current);
            }
        }

        return dirs;
    }

    private static IEnumerable<string> NuGetConfigFiles(string directory)
    {
        foreach (var name in new[] { "nuget.config", "NuGet.Config", "NuGet.config" })
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path))
            {
                yield return path;
                yield break; // NuGet matches the name case-insensitively; one physical file per dir
            }
        }
    }

    private static void ParseConfig(
        string configPath,
        Dictionary<string, string> folderSources,
        ref Dictionary<string, IReadOnlyList<Regex>>? sourceMapping)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(configPath);
        }
        catch (System.Xml.XmlException)
        {
            return;
        }

        var configDir = Path.GetDirectoryName(configPath)!;

        var packageSources = document.Root?.Element("packageSources");
        if (packageSources is not null)
        {
            foreach (var add in packageSources.Elements("add"))
            {
                var key = add.Attribute("key")?.Value;
                var value = add.Attribute("value")?.Value;
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var folder = ResolveFolderSource(value!, configDir);
                if (folder is not null && !folderSources.ContainsKey(key!))
                {
                    folderSources[key!] = folder;
                }
            }
        }

        // The nearest config with a non-empty mapping wins; once set, leave it.
        if (sourceMapping is null)
        {
            var mappingElement = document.Root?.Element("packageSourceMapping");
            if (mappingElement is not null)
            {
                var parsed = ParseSourceMapping(mappingElement);
                if (parsed.Count > 0)
                {
                    sourceMapping = parsed;
                }
            }
        }
    }

    private static Dictionary<string, IReadOnlyList<Regex>> ParseSourceMapping(XElement mappingElement)
    {
        var map = new Dictionary<string, IReadOnlyList<Regex>>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in mappingElement.Elements("packageSource"))
        {
            var key = source.Attribute("key")?.Value;
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var patterns = source.Elements("package")
                .Select(p => p.Attribute("pattern")?.Value)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => PatternToRegex(p!))
                .ToArray();
            map[key!] = patterns;
        }

        return map;
    }

    // The folder sources that could supply this package id, honoring packageSourceMapping when set.
    // No mapping => all folder sources. With a mapping, a folder source is a candidate only if one of
    // its patterns matches the id (NuGet picks the single longest-pattern match; checking every
    // matching source is a safe over-approximation — at worst it hashes one more file).
    private static IEnumerable<KeyValuePair<string, string>> CandidateSources(
        string id,
        Dictionary<string, string> folderSources,
        Dictionary<string, IReadOnlyList<Regex>>? sourceMapping)
    {
        foreach (var pair in folderSources.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (sourceMapping is null)
            {
                yield return pair;
                continue;
            }

            if (sourceMapping.TryGetValue(pair.Key, out var patterns) && patterns.Any(p => p.IsMatch(id)))
            {
                yield return pair;
            }
        }
    }

    // Resolves a packageSources value to a local folder path, or null when it is an HTTP(S) feed or a
    // path that doesn't exist on disk. Handles file:// URIs and relative paths (resolved against the
    // config file's directory, as NuGet does).
    private static string? ResolveFolderSource(string value, string configDir)
    {
        var path = value;
        if (value.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                path = uri.LocalPath;
            }
            else
            {
                return null;
            }
        }
        else if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                 || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!Path.IsPathRooted(path))
        {
            path = Path.GetFullPath(path, configDir);
        }

        return Directory.Exists(path) ? Path.GetFullPath(path) : null;
    }

    // Looks for the package's .nupkg in a folder feed, covering both layouts NuGet writes:
    //   - flat        <dir>/<Id>.<Version>.nupkg            (dotnet pack -o <dir>)
    //   - hierarchical <dir>/<id>/<version>/<id>.<version>.nupkg (lowercased; dotnet nuget push <dir>)
    // Falls back to a case-insensitive flat scan so a casing mismatch on a case-sensitive filesystem
    // still resolves.
    private static string? FindNupkg(string folderDir, string id, string version)
    {
        var flat = Path.Combine(folderDir, $"{id}.{version}.nupkg");
        if (File.Exists(flat))
        {
            return flat;
        }

        var lowerId = id.ToLowerInvariant();
        var lowerVersion = version.ToLowerInvariant();
        var hierarchical = Path.Combine(folderDir, lowerId, lowerVersion, $"{lowerId}.{lowerVersion}.nupkg");
        if (File.Exists(hierarchical))
        {
            return hierarchical;
        }

        var expected = $"{id}.{version}.nupkg";
        try
        {
            foreach (var candidate in Directory.EnumerateFiles(folderDir, "*.nupkg"))
            {
                if (string.Equals(Path.GetFileName(candidate), expected, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // raced with a deletion; treat as absent
        }

        return null;
    }

    // (id, version) of every type=="package" entry in each closure project's resolved graph.
    private static IReadOnlyList<(string Id, string Version)> ResolvedPackages(IEnumerable<string> projectClosure)
    {
        var packages = new HashSet<(string, string)>();
        foreach (var projectPath in projectClosure)
        {
            var projectDirectory = Path.GetDirectoryName(projectPath);
            if (string.IsNullOrWhiteSpace(projectDirectory))
            {
                continue;
            }

            var assetsPath = Path.Combine(projectDirectory, "obj", "project.assets.json");
            if (!File.Exists(assetsPath))
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(File.ReadAllText(assetsPath));
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                if (!document.RootElement.TryGetProperty("libraries", out var libraries)
                    || libraries.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var library in libraries.EnumerateObject())
                {
                    if (library.Value.ValueKind == JsonValueKind.Object
                        && library.Value.TryGetProperty("type", out var type)
                        && type.ValueKind == JsonValueKind.String
                        && !string.Equals(type.GetString(), "package", StringComparison.OrdinalIgnoreCase))
                    {
                        continue; // a project reference, not a NuGet package
                    }

                    var slash = library.Name.IndexOf('/');
                    if (slash <= 0 || slash >= library.Name.Length - 1)
                    {
                        continue;
                    }

                    packages.Add((library.Name[..slash], library.Name[(slash + 1)..]));
                }
            }
        }

        return packages.ToArray();
    }

    private static Regex PatternToRegex(string pattern)
    {
        // packageSourceMapping patterns use NuGet glob semantics: a trailing `*` is a prefix wildcard,
        // a bare id is exact. Convert to an anchored, case-insensitive regex.
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return new Regex(regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
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
}

// One resolved package that lives in a local folder feed, with the hash of its actual .nupkg file.
// Folded into the publish fingerprint so a same-version re-pack forces a republish. Source is the
// nuget.config packageSources key, for diagnostics.
public sealed record LocalFeedPackageHash(string Id, string Version, string Source, string FileHash);
