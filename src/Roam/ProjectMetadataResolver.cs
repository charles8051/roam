using System.Text.Json;
using System.Xml.Linq;

namespace Roam;

public static class ProjectMetadataResolver
{
    public static ResolvedProjectPaths ResolveProjectPaths(Roamfile roamfile, string roamfilePath)
    {
        var workspaceRoot = Path.GetDirectoryName(roamfilePath)
            ?? throw new InvalidOperationException("Could not determine workspace root.");

        if (!string.IsNullOrWhiteSpace(roamfile.Csproj))
        {
            var projectPath = Path.GetFullPath(roamfile.Csproj!, workspaceRoot);
            if (!File.Exists(projectPath))
            {
                throw new RoamException(ExitCode.Config, "parse", "local", $"csproj '{roamfile.Csproj}' was not found relative to '{workspaceRoot}'");
            }

            return new ResolvedProjectPaths(
                workspaceRoot,
                roamfilePath,
                projectPath,
                Path.GetDirectoryName(projectPath)!,
                Path.GetFileNameWithoutExtension(projectPath),
                null);
        }

        var solutionPath = Path.GetFullPath(roamfile.Solution!, workspaceRoot);
        if (!File.Exists(solutionPath))
        {
            throw new RoamException(ExitCode.Config, "parse", "local", $"solution '{roamfile.Solution}' was not found relative to '{workspaceRoot}'");
        }

        var projectCandidates = Directory.GetFiles(workspaceRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projectCandidates.Count == 0)
        {
            throw new RoamException(ExitCode.Config, "parse", "local", $"no csproj files were found under '{workspaceRoot}' for solution '{roamfile.Solution}'");
        }

        string? selected = null;
        if (!string.IsNullOrWhiteSpace(roamfile.Project))
        {
            selected = projectCandidates.FirstOrDefault(path => string.Equals(Path.GetFileNameWithoutExtension(path), roamfile.Project, StringComparison.OrdinalIgnoreCase));
        }

        selected ??= projectCandidates.Count == 1 ? projectCandidates[0] : null;

        if (selected is null)
        {
            throw new RoamException(ExitCode.Config, "parse", "local", $"solution '{roamfile.Solution}' resolved to multiple csproj files; set 'csproj' explicitly in roamfile.yaml");
        }

        return new ResolvedProjectPaths(
            workspaceRoot,
            roamfilePath,
            selected,
            Path.GetDirectoryName(selected)!,
            Path.GetFileNameWithoutExtension(selected),
            solutionPath);
    }

    public static PublishProfileInfo LoadPublishProfile(ResolvedProjectPaths paths, string profileName)
    {
        var filePath = Path.Combine(paths.ProjectDirectory, "Properties", "PublishProfiles", $"{profileName}.pubxml");
        if (!File.Exists(filePath))
        {
            var relative = Path.GetRelativePath(paths.WorkspaceRoot, filePath);
            throw new RoamException(ExitCode.Preflight, "preflight", "local", $"publish profile '{profileName}' not found at '{relative}'");
        }

        var document = XDocument.Load(filePath);
        var group = document.Root?.Element("PropertyGroup");
        if (group is null)
        {
            throw new RoamException(ExitCode.Config, "parse", "local", $"publish profile '{profileName}' is missing a PropertyGroup");
        }

        var publishDir = group.Element("PublishDir")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(publishDir))
        {
            throw new RoamException(ExitCode.Config, "parse", "local", $"publish profile '{profileName}' is missing PublishDir");
        }

        return new PublishProfileInfo(
            profileName,
            group.Element("RuntimeIdentifier")?.Value?.Trim(),
            bool.TryParse(group.Element("SelfContained")?.Value, out var selfContained) && selfContained,
            publishDir.Replace('\\', '/'),
            group.Element("Configuration")?.Value?.Trim(),
            group.Element("TargetFramework")?.Value?.Trim());
    }

    public static ResolvedPublishSettings ResolvePublishSettings(ResolvedProjectPaths paths, string profileName, ProfileSpec profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.PublishProfile))
        {
            var publishProfile = LoadPublishProfile(paths, profile.PublishProfile!);
            return new ResolvedPublishSettings(
                publishProfile.Name,
                true,
                publishProfile.RuntimeIdentifier,
                publishProfile.SelfContained,
                publishProfile.PublishDirectory,
                publishProfile.Configuration,
                publishProfile.TargetFramework);
        }

        var publish = profile.Publish
            ?? throw new RoamException(ExitCode.Config, "parse", "local", $"profile '{profileName}' must set exactly one of 'publish-profile' or 'publish'");

        return new ResolvedPublishSettings(
            null,
            false,
            publish.Rid,
            publish.SelfContained,
            $"obj/roam/{profileName}/publish",
            publish.Configuration,
            publish.Framework);
    }

    // Best-effort read of the project's target framework moniker for the framework-dependent
    // runtime preflight. SDK-style csproj elements carry no XML namespace, so a plain descendant
    // lookup works. Returns null on anything unreadable so the caller degrades to a warning.
    public static string? ReadTargetFramework(ResolvedProjectPaths paths)
    {
        if (!File.Exists(paths.ProjectPath))
        {
            return null;
        }

        XDocument document;
        try
        {
            document = XDocument.Load(paths.ProjectPath);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var single = document.Descendants("TargetFramework").FirstOrDefault()?.Value?.Trim();
        if (!string.IsNullOrWhiteSpace(single))
        {
            return single;
        }

        var multi = document.Descendants("TargetFrameworks").FirstOrDefault()?.Value?.Trim();
        return multi?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
    }

    public static LaunchProfileInfo LoadLaunchProfile(ResolvedProjectPaths paths, string profileName)
    {
        var launchSettingsPath = Path.Combine(paths.ProjectDirectory, "Properties", "launchSettings.json");
        if (!File.Exists(launchSettingsPath))
        {
            throw new RoamException(ExitCode.Preflight, "preflight", "local", $"launch settings not found at '{Path.GetRelativePath(paths.WorkspaceRoot, launchSettingsPath)}'");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(launchSettingsPath));
        if (!document.RootElement.TryGetProperty("profiles", out var profilesElement))
        {
            throw new RoamException(ExitCode.Config, "parse", "local", $"launchSettings.json at '{launchSettingsPath}' has no 'profiles' object");
        }

        foreach (var child in profilesElement.EnumerateObject())
        {
            if (!string.Equals(child.Name, profileName, StringComparison.Ordinal))
            {
                continue;
            }

            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (child.Value.TryGetProperty("environmentVariables", out var envElement))
            {
                foreach (var envChild in envElement.EnumerateObject())
                {
                    env[envChild.Name] = envChild.Value.GetString() ?? string.Empty;
                }
            }

            return new LaunchProfileInfo(
                child.Name,
                child.Value.TryGetProperty("commandName", out var commandName) ? commandName.GetString() : null,
                child.Value.TryGetProperty("commandLineArgs", out var args) ? args.GetString() : null,
                env);
        }

        var known = profilesElement.EnumerateObject().Select(x => x.Name).ToArray();
        throw new RoamException(ExitCode.Preflight, "preflight", "local", $"launch profile '{profileName}' not found in '{Path.GetRelativePath(paths.WorkspaceRoot, launchSettingsPath)}' (available: {string.Join(", ", known)})");
    }
}
