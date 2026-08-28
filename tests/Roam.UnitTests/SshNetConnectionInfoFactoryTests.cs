using Xunit;

namespace Roam.UnitTests;

public sealed class SshNetConnectionInfoFactoryTests
{
    [Fact]
    public void ResolveIdentityCandidatesKeepsExplicitKeyFirstThenAllSshConfigKeysThenDefaults()
    {
        var tempHome = Directory.CreateTempSubdirectory("roam-sshnet-home-");
        try
        {
            var ssh = Path.Combine(tempHome.FullName, ".ssh");
            Directory.CreateDirectory(ssh);
            var explicitKey = Path.Combine(tempHome.FullName, "explicit_ed25519");
            var configKey1 = Path.Combine(tempHome.FullName, "config_ed25519");
            var configKey2 = Path.Combine(tempHome.FullName, "config_rsa");
            var defaultEd25519 = Path.Combine(ssh, "id_ed25519");
            var defaultRsa = Path.Combine(ssh, "id_rsa");
            foreach (var path in new[] { explicitKey, configKey1, configKey2, defaultEd25519, defaultRsa })
            {
                File.WriteAllText(path, "not a real key");
            }

            var host = new HostResolution(
                "target",
                "target.example.test",
                "roam",
                2222,
                explicitKey,
                [configKey1, configKey2, defaultEd25519],
                null,
                null,
                "linux",
                false);

            var candidates = SshNetConnectionInfoFactory.ResolveIdentityCandidates(host, tempHome.FullName);

            Assert.Equal(
                [explicitKey, configKey1, configKey2, defaultEd25519, defaultRsa],
                candidates.Select(x => x.Path).ToArray());
            Assert.Equal(["explicit", "ssh-config", "ssh-config", "ssh-config", "default"], candidates.Select(x => x.Source).ToArray());
            Assert.All(candidates, candidate => Assert.True(candidate.Exists));
        }
        finally
        {
            tempHome.Delete(recursive: true);
        }
    }

    [Fact]
    public void LoadIdentityCandidatesReportsMissingAndUnsupportedKeys()
    {
        var tempHome = Directory.CreateTempSubdirectory("roam-sshnet-load-");
        try
        {
            var badKey = Path.Combine(tempHome.FullName, "bad_key");
            var missingKey = Path.Combine(tempHome.FullName, "missing_key");
            File.WriteAllText(badKey, "not a real private key");
            var host = new HostResolution(
                "target",
                "target.example.test",
                "roam",
                2222,
                badKey,
                [missingKey],
                null,
                null,
                "linux",
                false);

            var result = SshNetConnectionInfoFactory.LoadIdentityCandidates(host, tempHome.FullName);

            Assert.Empty(result.Keys);
            Assert.Contains(result.Candidates, x => x.Path == badKey && x.Exists && !x.Loadable && x.FailureReason == "encrypted or unsupported key");
            Assert.Contains(result.Candidates, x => x.Path == missingKey && !x.Exists && !x.Loadable && x.FailureReason == "file not found");
        }
        finally
        {
            tempHome.Delete(recursive: true);
        }
    }

    [Fact]
    public void FormatAuthFailureIncludesHostTupleAndCandidateStatusesWithoutKeyMaterial()
    {
        var host = new HostResolution(
            "target",
            "target.example.test",
            "roam",
            2222,
            "/home/roam/.ssh/id_ed25519",
            ["/home/roam/.ssh/id_rsa"],
            null,
            null,
            "linux",
            false);
        var candidates = new[]
        {
            new SshIdentityCandidate("/home/roam/.ssh/id_ed25519", "explicit", Exists: true, Loadable: false, FailureReason: "encrypted or unsupported key"),
            new SshIdentityCandidate("/home/roam/.ssh/id_rsa", "ssh-config", Exists: false, Loadable: false, FailureReason: "file not found"),
        };

        var message = SshNetConnectionInfoFactory.FormatAuthenticationFailure(
            host,
            candidates,
            "Permission denied (publickey).",
            loadedKeyCount: 0);

        Assert.Contains("host 'target'", message);
        Assert.Contains("target.example.test", message);
        Assert.Contains("user 'roam'", message);
        Assert.Contains("port 2222", message);
        Assert.Contains("/home/roam/.ssh/id_ed25519 [explicit: encrypted or unsupported key]", message);
        Assert.Contains("/home/roam/.ssh/id_rsa [ssh-config: file not found]", message);
        Assert.Contains("SSH.NET SFTP sync requires non-interactive private-key authentication", message);
        Assert.DoesNotContain("BEGIN OPENSSH PRIVATE KEY", message);
    }
}
