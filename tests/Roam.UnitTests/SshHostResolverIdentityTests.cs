using System.Reflection;
using Xunit;

namespace Roam.UnitTests;

public sealed class SshHostResolverIdentityTests
{
    [Fact]
    public void ParseSshConfigKeepsAllIdentityFileEntriesInOrder()
    {
        const string config = """
hostname target.example.test
user roam
port 2222
identityfile ~/.ssh/id_ed25519
identityfile ~/.ssh/id_rsa
proxyjump bastion
""";

        var method = typeof(SshHostResolver).GetMethod("ParseSshConfig", BindingFlags.NonPublic | BindingFlags.Static);
        var snapshot = Assert.IsType<SshConfigSnapshot>(method!.Invoke(null, [config]));

        Assert.Equal("target.example.test", snapshot.HostName);
        Assert.Equal("roam", snapshot.User);
        Assert.Equal(2222, snapshot.Port);
        Assert.Equal("~/.ssh/id_ed25519", snapshot.IdentityFile);
        Assert.Equal(["~/.ssh/id_ed25519", "~/.ssh/id_rsa"], snapshot.IdentityFiles);
        Assert.Equal("bastion", snapshot.ProxyJump);
    }
}
