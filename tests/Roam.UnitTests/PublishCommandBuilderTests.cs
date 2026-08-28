using Xunit;

namespace Roam.UnitTests;

public sealed class PublishCommandBuilderTests
{
    [Fact]
    public void BuildPublishCommand_UsesPublishProfileWhenConfigured()
    {
        var command = PublishCommandBuilder.Build(
            new ResolvedProjectPaths("/repo", "/repo/roamfile.yaml", "/repo/src/App/App.csproj", "/repo/src/App", "App", null),
            new ResolvedPublishSettings("Demo", true, "linux-x64", true, "publish", "Release", "net10.0"),
            "/remote/workspace",
            ciBuild: true);

        Assert.Contains("cd '/remote/workspace' &&", command);
        Assert.Contains("dotnet publish 'src/App/App.csproj' -p:PublishProfile='Demo'", command);
        Assert.Contains("-p:ContinuousIntegrationBuild=true", command);
        Assert.Contains("--disable-build-servers", command);
        Assert.DoesNotContain(" --runtime ", command);
        Assert.DoesNotContain(" --self-contained ", command);
        Assert.DoesNotContain(" --output ", command);
    }

    [Fact]
    public void BuildPublishCommand_UsesRoamPublishBlockWhenConfigured()
    {
        var command = PublishCommandBuilder.Build(
            new ResolvedProjectPaths("/repo", "/repo/roamfile.yaml", "/repo/src/App/App.csproj", "/repo/src/App", "App", null),
            new ResolvedPublishSettings(null, false, "linux-x64", true, "obj/roam/dev-local/publish", "Release", null),
            null,
            ciBuild: false);

        Assert.Equal("dotnet publish 'src/App/App.csproj' --configuration 'Release' --runtime 'linux-x64' --self-contained true --output 'src/App/obj/roam/dev-local/publish' --disable-build-servers", command);
    }
}
