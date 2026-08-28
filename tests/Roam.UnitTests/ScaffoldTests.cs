using System.Runtime.InteropServices;
using Xunit;

namespace Roam.UnitTests;

public sealed class ScaffoldTests
{
    [Fact]
    public async Task InitWithoutPubxmlScaffoldsRoamNativePublishBlock()
    {
        var temp = Directory.CreateTempSubdirectory("roam-init-");

        try
        {
            Directory.CreateDirectory(Path.Combine(temp.FullName, "src", "App", "Properties"));
            await File.WriteAllTextAsync(
                Path.Combine(temp.FullName, "src", "App", "App.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            await File.WriteAllTextAsync(
                Path.Combine(temp.FullName, "src", "App", "Properties", "launchSettings.json"),
                """
                {
                  "profiles": {
                    "Development": {
                      "commandName": "Project"
                    }
                  }
                }
                """);

            var commands = new RoamCommands();
            await commands.RunInitAsync(new CliOptions(null, false, false, null, false), null, Path.Combine("src", "App", "App.csproj"), false, CancellationToken.None, temp.FullName);

            var content = await File.ReadAllTextAsync(Path.Combine(temp.FullName, "roamfile.yaml"));

            // With no pubxml, the scaffold names no publish shape at all — ConfigLoader synthesizes
            // the block, so writing it out would only be restating a default.
            Assert.DoesNotContain("publish-profile:", content);
            Assert.DoesNotContain("publish:", content);

            // Same for the single launch profile, the version, the local host, and the host roles.
            Assert.DoesNotContain("launch-profile:", content);
            Assert.DoesNotContain("version:", content);
            Assert.DoesNotContain("hosts:", content);
            Assert.DoesNotContain("source:", content);

            Assert.Contains("csproj: src/App/App.csproj", content);
            Assert.Contains("process-name: App", content);

            // The scaffold must still round-trip through the loader it was written for.
            var roamfile = ConfigLoader.Load(Path.Combine(temp.FullName, "roamfile.yaml"));
            var profile = roamfile.Profiles["dev-local"];
            var arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                _ => throw new InvalidOperationException($"unsupported architecture {RuntimeInformation.OSArchitecture}")
            };
            var expectedRid = OperatingSystem.IsWindows() ? $"win-{arch}"
                            : OperatingSystem.IsMacOS()   ? $"osx-{arch}"
                                                          : $"linux-{arch}";
            Assert.Equal(expectedRid, profile.Publish!.Rid);
            Assert.True(profile.Publish.SelfContained);
            Assert.Equal("Release", profile.Publish.Configuration);
            Assert.Equal("local", profile.Source);
            Assert.Equal("local", profile.Build);
            Assert.Equal("local", profile.Target);
            Assert.Null(profile.LaunchProfile);
        }
        finally
        {
            temp.Delete(true);
        }
    }
}
