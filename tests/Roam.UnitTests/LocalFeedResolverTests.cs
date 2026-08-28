using Xunit;

namespace Roam.UnitTests;

public sealed class LocalFeedResolverTests
{
    // Hierarchical v3 feed layout (what `dotnet nuget push <dir>` writes): lowercased
    // <id>/<version>/<id>.<version>.nupkg. The resolver must find and hash it.
    [Fact]
    public async Task Resolve_FindsNupkg_InHierarchicalLayout()
    {
        using var ws = new Workspace();
        var feed = ws.CreateFeed("local-feed");
        var dir = Path.Combine(feed, "example.devices", "1.5.1-alpha.1");
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, "example.devices.1.5.1-alpha.1.nupkg"), [1, 2, 3]);

        ws.WriteNuGetConfig($"<configuration><packageSources><add key=\"local\" value=\"{feed.Replace('\\', '/')}\" /></packageSources></configuration>");
        ws.WriteAssets("{ \"libraries\": { \"Example.Devices/1.5.1-alpha.1\": { \"type\": \"package\" } } }");

        var result = await LocalFeedResolver.ResolveAsync(ws.Root, ws.Closure, CancellationToken.None);

        var package = Assert.Single(result);
        Assert.Equal("Example.Devices", package.Id);
        Assert.Equal("1.5.1-alpha.1", package.Version);
        Assert.False(string.IsNullOrEmpty(package.FileHash));
    }

    // An HTTP source is not a folder feed, so nothing is content-keyed.
    [Fact]
    public async Task Resolve_IgnoresHttpSources()
    {
        using var ws = new Workspace();
        ws.WriteNuGetConfig("<configuration><packageSources><add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\" /></packageSources></configuration>");
        ws.WriteAssets("{ \"libraries\": { \"Serilog/3.1.0\": { \"type\": \"package\" } } }");

        var result = await LocalFeedResolver.ResolveAsync(ws.Root, ws.Closure, CancellationToken.None);

        Assert.Empty(result);
    }

    // packageSourceMapping routes Example.* to one folder feed only; the package exists in both
    // feeds but must be content-keyed from the mapped one.
    [Fact]
    public async Task Resolve_RespectsPackageSourceMapping()
    {
        using var ws = new Workspace();
        var feedA = ws.CreateFeed("feed-a");
        var feedB = ws.CreateFeed("feed-b");
        await File.WriteAllBytesAsync(Path.Combine(feedA, "Example.Devices.1.0.0.nupkg"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(feedB, "Example.Devices.1.0.0.nupkg"), [2]);

        ws.WriteNuGetConfig(
            "<configuration>"
            + "<packageSources>"
            + $"<add key=\"feedA\" value=\"{feedA.Replace('\\', '/')}\" />"
            + $"<add key=\"feedB\" value=\"{feedB.Replace('\\', '/')}\" />"
            + "</packageSources>"
            + "<packageSourceMapping>"
            + "<packageSource key=\"feedA\"><package pattern=\"Example.*\" /></packageSource>"
            + "<packageSource key=\"feedB\"><package pattern=\"Other.*\" /></packageSource>"
            + "</packageSourceMapping>"
            + "</configuration>");
        ws.WriteAssets("{ \"libraries\": { \"Example.Devices/1.0.0\": { \"type\": \"package\" } } }");

        var result = await LocalFeedResolver.ResolveAsync(ws.Root, ws.Closure, CancellationToken.None);

        var package = Assert.Single(result);
        Assert.Equal("feedA", package.Source);
    }

    // Project references (type=="project") are not NuGet packages and must be ignored.
    [Fact]
    public async Task Resolve_IgnoresProjectReferences()
    {
        using var ws = new Workspace();
        var feed = ws.CreateFeed("local-feed");
        await File.WriteAllBytesAsync(Path.Combine(feed, "Real.Pkg.1.0.0.nupkg"), [1]);
        ws.WriteNuGetConfig($"<configuration><packageSources><add key=\"local\" value=\"{feed.Replace('\\', '/')}\" /></packageSources></configuration>");
        ws.WriteAssets("{ \"libraries\": { \"SomeProject/1.0.0\": { \"type\": \"project\" }, \"Real.Pkg/1.0.0\": { \"type\": \"package\" } } }");

        var result = await LocalFeedResolver.ResolveAsync(ws.Root, ws.Closure, CancellationToken.None);

        var package = Assert.Single(result);
        Assert.Equal("Real.Pkg", package.Id);
    }

    private sealed class Workspace : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("roam-localfeed-");

        public string Root => _root.FullName;

        private string ProjectDir => Path.Combine(Root, "src", "App");

        public IReadOnlyList<string> Closure => [Path.Combine(ProjectDir, "App.csproj")];

        public string CreateFeed(string name)
        {
            var feed = Path.Combine(Root, name);
            Directory.CreateDirectory(feed);
            return feed;
        }

        public void WriteNuGetConfig(string xml)
            => File.WriteAllText(Path.Combine(Root, "nuget.config"), xml);

        public void WriteAssets(string json)
        {
            var objDir = Path.Combine(ProjectDir, "obj");
            Directory.CreateDirectory(objDir);
            File.WriteAllText(Path.Combine(objDir, "project.assets.json"), json);
        }

        public void Dispose() => _root.Delete(recursive: true);
    }
}
