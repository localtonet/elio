using Elio.Core;

namespace Elio.Tests;

public sealed class KnownHostsTests : TempDataDirectoryTest
{
    private const string Fingerprint = "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string OtherFingerprint = "SHA256:BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    [Fact]
    public void Check_HostNeverSeen_IsUnknown() =>
        Assert.Equal(HostKeyStatus.Unknown, new KnownHosts().Check("example.com", 22, Fingerprint));

    [Fact]
    public void Check_TrustedHostWithSameFingerprint_IsTrusted()
    {
        var known = new KnownHosts();
        known.Trust("example.com", 22, "ssh-ed25519", Fingerprint);

        Assert.Equal(HostKeyStatus.Trusted, known.Check("example.com", 22, Fingerprint));
    }

    [Fact]
    public void Check_TrustedHostWithAnotherFingerprint_IsChanged()
    {
        var known = new KnownHosts();
        known.Trust("example.com", 22, "ssh-ed25519", Fingerprint);

        Assert.Equal(HostKeyStatus.Changed, known.Check("example.com", 22, OtherFingerprint));
    }

    [Fact]
    public void Check_SameHostOnAnotherPort_IsADifferentEntry()
    {
        var known = new KnownHosts();
        known.Trust("example.com", 22, "ssh-ed25519", Fingerprint);

        Assert.Equal(HostKeyStatus.Unknown, known.Check("example.com", 2222, Fingerprint));
        Assert.Null(known.Fingerprint("example.com", 2222));
    }

    [Fact]
    public void Check_HostNameCase_DoesNotMatter()
    {
        var known = new KnownHosts();
        known.Trust("Example.COM", 22, "ssh-ed25519", Fingerprint);

        Assert.Equal(HostKeyStatus.Trusted, known.Check("example.com", 22, Fingerprint));
    }

    [Fact]
    public void Trust_IsRememberedByTheNextInstance_InTheTestDataDirectory()
    {
        new KnownHosts().Trust("example.com", 22, "ssh-ed25519", Fingerprint);

        Assert.True(File.Exists(Path.Combine(DataDirectory, "known-hosts.json")));
        Assert.Equal(HostKeyStatus.Trusted, new KnownHosts().Check("example.com", 22, Fingerprint));
    }

    [Fact]
    public void Merge_DifferentKeyFromElsewhere_NeverReplacesTheOnePinnedHere()
    {
        var known = new KnownHosts();
        known.Trust("example.com", 22, "ssh-ed25519", Fingerprint);

        known.Merge(new Dictionary<string, KnownHostKey>
        {
            ["example.com:22"] = new() { Algorithm = "ssh-ed25519", Fingerprint = OtherFingerprint, AddedAt = DateTimeOffset.UtcNow.AddHours(1) },
        });

        // A changed key has to be met at connect time, where the user is asked, not arrive quietly from another device.
        Assert.Equal(Fingerprint, known.Fingerprint("example.com", 22));
    }

    [Fact]
    public void Merge_OlderKeyFromElsewhere_IsIgnored()
    {
        var known = new KnownHosts();
        known.Trust("example.com", 22, "ssh-ed25519", Fingerprint);

        known.Merge(new Dictionary<string, KnownHostKey>
        {
            ["example.com:22"] = new() { Algorithm = "ssh-ed25519", Fingerprint = OtherFingerprint, AddedAt = DateTimeOffset.UtcNow.AddHours(-1) },
        });

        Assert.Equal(Fingerprint, known.Fingerprint("example.com", 22));
    }
}
