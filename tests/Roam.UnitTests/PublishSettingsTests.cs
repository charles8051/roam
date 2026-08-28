using Xunit;

namespace Roam.UnitTests;

public sealed class PublishSettingsTests
{
    [Fact]
    public void ResolvePublishSettings_LoadsLegacyPublishProfile()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var roamfile = ConfigLoader.Load(Path.Combine(repositoryRoot, "tests/fixtures/SampleApp/roamfile.yaml"));
        var paths = ProjectMetadataResolver.ResolveProjectPaths(roamfile, Path.Combine(repositoryRoot, "tests/fixtures/SampleApp/roamfile.yaml"));

        var settings = ProjectMetadataResolver.ResolvePublishSettings(paths, "kiosk", roamfile.Profiles["kiosk"]);

        Assert.True(settings.UsePublishProfile);
        Assert.Equal("TestLinuxX64SelfContained", settings.Name);
        Assert.Equal("linux-x64", settings.RuntimeIdentifier);
        Assert.True(settings.SelfContained);
        Assert.Equal("bin/Release/net10.0/linux-x64/publish/", settings.PublishDirectory);
    }

    [Fact]
    public void ResolvePublishSettings_UsesRoamPublishBlockWithoutPubxml()
    {
        var temp = CreateTempDirectory();
        var projectDirectory = Path.Combine(temp, "src", "App");
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(Path.Combine(projectDirectory, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        var roamfile = ConfigLoader.Load(WriteRoamfile(temp, """
version: 1
csproj: src/App/App.csproj
hosts:
  local:
    ssh: localhost
    user: test
    os: linux
profiles:
  dev-local:
    source: local
    build: local
    target: local
    launch-profile: Development
    publish:
      rid: linux-x64
      self-contained: true
      configuration: Release
    deploy:
      path: /tmp/app
"""));
        var paths = ProjectMetadataResolver.ResolveProjectPaths(roamfile, Path.Combine(temp, "roamfile.yaml"));

        var settings = ProjectMetadataResolver.ResolvePublishSettings(paths, "dev-local", roamfile.Profiles["dev-local"]);

        Assert.False(settings.UsePublishProfile);
        Assert.Null(settings.Name);
        Assert.Equal("linux-x64", settings.RuntimeIdentifier);
        Assert.True(settings.SelfContained);
        Assert.Equal("Release", settings.Configuration);
        Assert.Equal("obj/roam/dev-local/publish", settings.PublishDirectory);
    }

    private static string WriteRoamfile(string root, string content)
    {
        var path = Path.Combine(root, "roamfile.yaml");
        File.WriteAllText(path, content);
        return path;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "roam-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
