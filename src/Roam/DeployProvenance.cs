using System.Text.RegularExpressions;

namespace Roam;

// Builds the deploy-provenance record (Feature 1, the stale-package footgun guard). Given the
// just-completed artifacts.json sync manifest, the on-disk publish root, and the profile's
// provenance globs, it reads each matching managed assembly's declared versions out of PE/CLI
// metadata and pairs them with the content hash artifacts.json already computed. The result is a
// SURFACE, not an assertion: roam cannot know the version you EXPECTED, only show whether the
// version/bytes changed since the previous deploy so an unchanged one stands out.
public static class DeployProvenance
{
    private static readonly string[] AssemblyExtensions = [".dll", ".exe"];

    // Scans the synced payload for managed assemblies whose file name matches a provenance glob.
    //   manifestEntries  - artifacts.json entries (Path relative to the deploy root, plus ContentHash)
    //   localPublishRoot - the publish output directory on the controller (assemblies read from here)
    //   flattenPublish   - whether artifacts.json paths are relative to the publish root (true) or
    //                      include the publish folder name as a leading segment (false)
    //   globs            - deploy.provenance globs; when empty, falls back to <projectName>.dll/.exe
    //   projectName      - the project's assembly name, for the default glob
    public static IReadOnlyList<DeployedAssembly> Scan(
        IReadOnlyList<ManifestEntry> manifestEntries,
        string localPublishRoot,
        bool flattenPublish,
        IReadOnlyList<string>? globs,
        string projectName)
    {
        var patterns = ResolvePatterns(globs, projectName);
        var publishFolderName = Path.GetFileName(localPublishRoot.TrimEnd('/', '\\'));

        var result = new List<DeployedAssembly>();
        foreach (var entry in manifestEntries)
        {
            var fileName = entry.Path.Split('/').Last();
            if (!HasAssemblyExtension(fileName) || !MatchesAny(fileName, patterns))
            {
                continue;
            }

            // Resolve the assembly's path under the publish root. Non-flatten manifests prefix every
            // entry with the publish folder name; flatten manifests are already publish-root-relative.
            var relativeUnderPublish = entry.Path;
            if (!flattenPublish)
            {
                var prefix = publishFolderName + "/";
                relativeUnderPublish = entry.Path.StartsWith(prefix, StringComparison.Ordinal)
                    ? entry.Path[prefix.Length..]
                    : entry.Path;
            }

            var localPath = Path.Combine(localPublishRoot, relativeUnderPublish.Replace('/', Path.DirectorySeparatorChar));
            var version = AssemblyVersionReader.Read(localPath);
            if (version is null)
            {
                // Matched the glob but isn't a managed assembly (a native DLL named Contoso.*.dll,
                // say). Nothing to report — skip rather than emit a versionless row.
                continue;
            }

            result.Add(new DeployedAssembly(
                entry.Path,
                version.InformationalVersion,
                version.FileVersion,
                version.AssemblyVersion,
                entry.ContentHash));
        }

        return result
            .OrderBy(a => a.Path, StringComparer.Ordinal)
            .ToArray();
    }

    // Computes the human-readable diff lines between the previous deploy's provenance and this one.
    // Each line: "<name>  <oldDisplay>  ->  <newDisplay-or-(unchanged)>". An assembly whose version
    // AND content hash are byte-identical to the prior deploy is the red flag the feature exists to
    // surface, so it is marked Unchanged=true (the caller decides how to highlight it).
    public static IReadOnlyList<ProvenanceDiffLine> Diff(
        DeployedVersionsManifest? previous,
        DeployedVersionsManifest current)
    {
        var prior = previous?.Assemblies.ToDictionary(a => a.Path, StringComparer.Ordinal)
                    ?? new Dictionary<string, DeployedAssembly>(StringComparer.Ordinal);

        var lines = new List<ProvenanceDiffLine>();
        foreach (var assembly in current.Assemblies)
        {
            var name = assembly.Path.Split('/').Last();
            if (!prior.TryGetValue(assembly.Path, out var before))
            {
                lines.Add(new ProvenanceDiffLine(name, "(new)", assembly.Display, Unchanged: false, IsNew: true));
                continue;
            }

            var versionSame = string.Equals(before.Display, assembly.Display, StringComparison.Ordinal);
            var hashSame = before.ContentHash is not null
                && string.Equals(before.ContentHash, assembly.ContentHash, StringComparison.Ordinal);
            var unchanged = versionSame && hashSame;

            lines.Add(new ProvenanceDiffLine(name, before.Display, assembly.Display, unchanged, IsNew: false));
        }

        return lines.OrderBy(l => l.Name, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<Regex> ResolvePatterns(IReadOnlyList<string>? globs, string projectName)
    {
        var raw = globs is { Count: > 0 }
            ? globs
            : [$"{projectName}.dll", $"{projectName}.exe"];

        return raw
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Select(GlobToRegex)
            .ToArray();
    }

    private static bool HasAssemblyExtension(string fileName)
        => AssemblyExtensions.Any(ext => fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    private static bool MatchesAny(string fileName, IReadOnlyList<Regex> patterns)
        => patterns.Any(p => p.IsMatch(fileName));

    // Converts a file-name glob (`*`, `?`) to an anchored, case-insensitive regex. A glob without a
    // dot still only matches file names with the right extension because the caller pre-filters on
    // extension; e.g. `Contoso.*` matches `Contoso.Widgets.dll`.
    private static Regex GlobToRegex(string glob)
    {
        var pattern = "^" + Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

public sealed record ProvenanceDiffLine(
    string Name,
    string Before,
    string After,
    bool Unchanged,
    bool IsNew);
