using Xunit;

namespace Roam.UnitTests;

public sealed class ExampleRoamfileTests
{
    [Fact]
    public void AllExampleRoamfilesParse()
    {
        var repositoryRoot = FindRepositoryRoot();
        var examplesRoot = Path.Combine(repositoryRoot, "examples");
        var roamfiles = Directory.GetFiles(examplesRoot, "roamfile.yaml", SearchOption.AllDirectories)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(roamfiles);

        foreach (var roamfile in roamfiles)
        {
            var parsed = ConfigLoader.Load(roamfile);
            Assert.NotEmpty(parsed.Hosts);
            Assert.NotEmpty(parsed.Profiles);
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "examples")) &&
                File.Exists(Path.Combine(current.FullName, "src", "Roam", "Roam.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("could not locate repository root");
    }
}
