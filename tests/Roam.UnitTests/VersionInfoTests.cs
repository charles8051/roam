using System.Reflection;
using Roam;
using Xunit;

namespace Roam.UnitTests;

public sealed class VersionInfoTests
{
    [Fact]
    public void CurrentReturnsAssemblyInformationalVersion()
    {
        var expected = typeof(VersionInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion;

        Assert.Equal(expected, VersionInfo.Current);
    }
}
