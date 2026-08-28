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
            Assert.Contains("launch-profile: Development", content);
            Assert.DoesNotContain("publish-profile:", content);
            Assert.Contains("publish:", content);
            // Roam scaffolds the RID for the current host (RoamCommands.DetectCurrentRid),
            // so this test must compute the same expectation rather than hardcode linux-x64.
            var arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                _ => throw new InvalidOperationException($"unsupported architecture {RuntimeInformation.OSArchitecture}")
            };
            var expectedRid = OperatingSystem.IsWindows() ? $"win-{arch}"
                            : OperatingSystem.IsMacOS()   ? $"osx-{arch}"
                                                          : $"linux-{arch}";
            Assert.Contains($"rid: {expectedRid}", content);
            Assert.Contains("self-contained: true", content);
            Assert.Contains("configuration: Release", content);
        }
        finally
        {
            temp.Delete(true);
        }
    }
}
