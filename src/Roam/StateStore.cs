namespace Roam;

public sealed class StateStore
{
    private const int SchemaVersion = 1;

    private readonly string _root;

    public StateStore(string workspaceRoot)
    {
        _root = Path.Combine(workspaceRoot, ".roam");
    }

    public string RootPath => _root;

    public void EnsureInitialized()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "manifests"));
        Directory.CreateDirectory(Path.Combine(_root, "runs"));
        Directory.CreateDirectory(Path.Combine(_root, "tmp"));
        File.WriteAllText(Path.Combine(_root, "schema-version"), "1\n");
    }

    public SyncManifest? LoadSourceManifest(string profile)
        => LoadManifest(GetManifestPath(profile, "source.json"));

    public SyncManifest? LoadArtifactManifest(string profile)
        => LoadManifest(GetManifestPath(profile, "artifacts.json"));

    public PublishManifest? LoadPublishManifest(string profile)
    {
        var path = GetManifestPath(profile, "publish.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<PublishManifest>(File.ReadAllText(path));
        }
        catch (System.Text.Json.JsonException)
        {
            // A corrupt or unreadable publish manifest is exactly equivalent to "no cached
            // fingerprint" — fall through to a full publish on the next run, which rewrites it.
            return null;
        }
    }

    public DeployedVersionsManifest? LoadDeployedVersionsManifest(string profile)
    {
        var path = GetManifestPath(profile, "deployed-versions.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<DeployedVersionsManifest>(File.ReadAllText(path));
        }
        catch (System.Text.Json.JsonException)
        {
            // A corrupt provenance manifest is non-fatal: treat as "no prior deploy" so the next diff
            // shows every assembly as new and rewrites a valid file.
            return null;
        }
    }

    public void SaveDeployedVersionsManifest(string profile, DeployedVersionsManifest manifest)
        => SaveJson(GetManifestPath(profile, "deployed-versions.json"), manifest);

    public void SaveSourceManifest(string profile, SyncManifest manifest)
        => SaveJson(GetManifestPath(profile, "source.json"), manifest);

    public void SaveArtifactManifest(string profile, SyncManifest manifest)
        => SaveJson(GetManifestPath(profile, "artifacts.json"), manifest);

    public void SavePublishManifest(string profile, PublishManifest manifest)
        => SaveJson(GetManifestPath(profile, "publish.json"), manifest);

    // Wipes every manifest for a profile (source, artifacts, publish) so the next `roam run`
    // is a cold deploy with no false-warm diff. Used by `roam uninstall` once the target-side
    // tear-down has succeeded. Returns the absolute path that was removed (or would have been
    // removed), for the operator-facing "removed:" summary; returns null if there was nothing
    // to remove.
    public string? RemoveManifests(string profile)
    {
        var profileDirectory = Path.Combine(_root, "manifests", profile);
        if (!Directory.Exists(profileDirectory))
        {
            return null;
        }

        Directory.Delete(profileDirectory, recursive: true);
        return profileDirectory;
    }

    public void SaveRunSummary(string profile, RunSummary summary)
    {
        SaveJson(Path.Combine(_root, "runs", $"{profile}.json"), summary);
        SaveJson(Path.Combine(_root, "runs", "last.json"), summary);
    }

    private static SyncManifest? LoadManifest(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<SyncManifest>(File.ReadAllText(path));
        }
        catch (System.Text.Json.JsonException)
        {
            // A corrupt or unreadable manifest must not abort a deploy: degrade to "no baseline"
            // so the sync does one full upload and rewrites a valid manifest.
            return null;
        }
    }

    private string GetManifestPath(string profile, string fileName)
        => Path.Combine(_root, "manifests", profile, fileName);

    private static void SaveJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = $"{path}.tmp";
        var json = System.Text.Json.JsonSerializer.Serialize(value, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
        });
        File.WriteAllText(tempPath, json + Environment.NewLine);
        File.Move(tempPath, path, true);
    }
}
