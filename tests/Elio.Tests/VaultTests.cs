using System.Security.Cryptography;
using System.Text;
using Elio.Core;

namespace Elio.Tests;

/// <summary>A test that means something only on Windows, and is skipped elsewhere.</summary>
internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows Hello exists only on Windows.";
    }
}

/// <summary>
/// The vault that keeps passwords and passphrases: encrypted on disk with a key that only this device can unwrap,
/// and, when Windows Hello is on, only after a gesture. The device's key store and Windows Hello are stood in for
/// here, so nothing touches the real ones.
/// </summary>
public sealed class VaultTests : TempDataDirectoryTest, IDisposable
{
    private readonly TestVaultKeys _keys = new(new TestKeyProtector(1));

    public new void Dispose()
    {
        WindowsHelloProtector.SignerOverride = null;
        WindowsHelloProtector.Lock();
        _keys.Dispose();
        base.Dispose();
    }

    /// <summary>Stands in for Windows Hello: the same signature for the same challenge, from a secret only it has.</summary>
    private sealed class FakeHello(byte[] secret) : IHelloSigner
    {
        public int Gestures { get; private set; }

        public bool Refuse { get; set; }

        public byte[] Sign(byte[] challenge, bool create)
        {
            Gestures++;
            if (Refuse) throw new VaultLockedException(VaultLockReason.Cancelled, "The user closed the prompt.");
            return HMACSHA256.HashData(secret, challenge);
        }
    }

    private string DataFile => Path.Combine(DataDirectory, "vault.dat");

    private string KeyFile => Path.Combine(DataDirectory, "vault.key");

    private string[] SetAside => [.. Directory.GetFiles(DataDirectory, "*.unreadable-*").Select(Path.GetFileName)!];

    [Fact]
    public void Secret_IsReadBackByTheNextStartButNeverWrittenInTheClear()
    {
        Guid id = Guid.NewGuid();
        new Vault().Set(id, "hunter2-password");

        Assert.Equal("hunter2-password", new Vault().Get(id));
        byte[] stored = File.ReadAllBytes(DataFile);
        Assert.Equal(-1, stored.AsSpan().IndexOf("hunter2"u8));
        Assert.Equal(-1, stored.AsSpan().IndexOf(Encoding.Unicode.GetBytes("hunter2")));
    }

    [Fact]
    public void Vault_FromAnotherDevice_DoesNotOpenAndIsSetAsideRatherThanLost()
    {
        Guid id = Guid.NewGuid();
        new Vault().Set(id, "secret");

        using (new TestVaultKeys(new TestKeyProtector(2)))
        {
            var elsewhere = new Vault();

            Assert.Null(elsewhere.Get(id));
            Assert.Empty(elsewhere.Entries);
        }

        Assert.Contains(SetAside, name => name.StartsWith("vault.dat.unreadable-", StringComparison.Ordinal));
    }

    [Fact]
    public void Data_ChangedOnDisk_IsSetAsideAndTheVaultStartsEmpty()
    {
        Guid id = Guid.NewGuid();
        new Vault().Set(id, "secret");
        byte[] stored = File.ReadAllBytes(DataFile);
        stored[^1] ^= 0x01;
        File.WriteAllBytes(DataFile, stored);

        var vault = new Vault();

        Assert.Null(vault.Get(id));
        Assert.Single(SetAside, name => name.StartsWith("vault.dat.unreadable-", StringComparison.Ordinal));

        // It goes on working with a fresh file.
        vault.Set(id, "again");
        Assert.Equal("again", new Vault().Get(id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(27)]
    [InlineData(28)]
    [InlineData(500)]
    public void Data_ThatIsGarbage_NeverThrows(int length)
    {
        new Vault().Set(Guid.NewGuid(), "secret");
        File.WriteAllBytes(DataFile, RandomNumberGenerator.GetBytes(length));

        Assert.Empty(new Vault().Entries);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5000)]
    public void KeyFile_ThatIsGarbage_GivesANewKeyInsteadOfAnError(int length)
    {
        Guid id = Guid.NewGuid();
        new Vault().Set(id, "secret");
        byte[] garbage = RandomNumberGenerator.GetBytes(length);
        if (garbage.Length > 0) garbage[0] = 1;
        File.WriteAllBytes(KeyFile, garbage);

        var vault = new Vault();
        Assert.Null(vault.Get(id));
        vault.Set(id, "new");

        Assert.Equal("new", new Vault().Get(id));
    }

    [Fact]
    public void Remove_LeavesARecordForSyncAndTellsAboutEachChange()
    {
        var vault = new Vault();
        int changes = 0;
        vault.Changed += () => changes++;
        Guid id = Guid.NewGuid();

        vault.Set(id, "secret");
        vault.Remove(id);
        vault.Remove(id);

        Assert.Equal(2, changes);
        Assert.Empty(vault.Entries);
        Assert.True(new Vault().Deleted.ContainsKey(id));

        vault.Set(id, "back");
        Assert.False(vault.Deleted.ContainsKey(id));
    }

    [Fact]
    public void Replace_TakesWhatSyncMerged()
    {
        var vault = new Vault();
        Guid kept = Guid.NewGuid(), removed = Guid.NewGuid();
        vault.Set(removed, "old");

        vault.Replace(new() { [kept] = new VaultEntry { Secret = "merged", ModifiedUtc = DateTime.UtcNow } }, new() { [removed] = DateTime.UtcNow });

        var reopened = new Vault();
        Assert.Equal("merged", reopened.Get(kept));
        Assert.Null(reopened.Get(removed));
        Assert.True(reopened.Deleted.ContainsKey(removed));
    }

    [Fact]
    public void LocalSecrets_StayOutOfWhatIsSynced()
    {
        var vault = new Vault();

        vault.SetLocal("sync-key", "device only");

        Assert.Equal("device only", new Vault().GetLocal("sync-key"));
        Assert.Empty(vault.Entries);
        vault.SetLocal("sync-key", null);
        Assert.Null(new Vault().GetLocal("sync-key"));
    }

    [Fact]
    public void PluginApprovals_AreKeptInTheVaultAndAnEmptyOneNeverCounts()
    {
        var approvals = new PluginApprovals(new Vault());

        approvals.Add("ABCDEF");
        approvals.Add("ABCDEF");
        approvals.Add(string.Empty);

        var later = new PluginApprovals(new Vault());
        Assert.True(later.Contains("ABCDEF"));
        Assert.False(later.Contains("abcdef"));
        Assert.False(later.Contains(string.Empty));
        Assert.Equal("ABCDEF", new Vault().GetLocal("plugin-approvals"));
    }

    // ---- Windows Hello ------------------------------------------------------------------------

    private (Guid Id, FakeHello Hello) VaultBehindHello()
    {
        var hello = new FakeHello(RandomNumberGenerator.GetBytes(32));
        WindowsHelloProtector.SignerOverride = hello;
        var vault = new Vault();
        Guid id = Guid.NewGuid();
        vault.Set(id, "behind hello");
        vault.EnableHello();
        Assert.True(vault.HelloEnabled);

        // As if the app had been closed: nothing remembered.
        WindowsHelloProtector.Lock();
        return (id, hello);
    }

    [WindowsFact]
    public void Hello_VaultStartsLockedAndAsksOnFirstUseOnly()
    {
        var (id, hello) = VaultBehindHello();
        int before = hello.Gestures;

        var vault = new Vault();
        Assert.True(vault.IsLocked);
        Assert.Equal(before, hello.Gestures);

        Assert.Equal("behind hello", vault.Get(id));
        Assert.Equal("behind hello", vault.Get(id));
        Assert.Equal(before + 1, hello.Gestures);
        Assert.False(vault.IsLocked);
    }

    [WindowsFact]
    public void Hello_TurnedDown_TheVaultKeepsNothingAndDoesNotAskAgainUntilTold()
    {
        var (id, hello) = VaultBehindHello();
        byte[] dataBefore = File.ReadAllBytes(DataFile);
        hello.Refuse = true;
        var vault = new Vault();

        Assert.Null(vault.Get(id));
        int asked = hello.Gestures;
        Assert.Null(vault.Get(id));
        vault.Set(Guid.NewGuid(), "dropped");
        Assert.Equal(asked, hello.Gestures);
        Assert.Equal(dataBefore, File.ReadAllBytes(DataFile));
        Assert.False(vault.TryUnlock());

        hello.Refuse = false;
        Assert.True(vault.TryUnlock());
        Assert.Equal("behind hello", vault.Get(id));
    }

    [WindowsFact]
    public void Hello_OfAnotherDevice_CannotOpenTheVaultAndTheKeyFileIsKept()
    {
        var (id, _) = VaultBehindHello();
        byte[] keyBefore = File.ReadAllBytes(KeyFile);
        WindowsHelloProtector.SignerOverride = new FakeHello(RandomNumberGenerator.GetBytes(32));

        var vault = new Vault();
        VaultLockedException refused = Assert.Throws<VaultLockedException>(vault.Unlock);

        Assert.Equal(VaultLockReason.Mismatch, refused.Reason);
        Assert.Null(vault.Get(id));
        Assert.Equal(keyBefore, File.ReadAllBytes(KeyFile));
    }

    // The key file as a newer Elio, or a damaged disk, might leave it: still wrapped by this device, but not readable here.
    private void RewriteHelloKey(Func<byte[], byte[]> change)
    {
        var protector = new TestKeyProtector(1);
        byte[] blob = protector.Unprotect(File.ReadAllBytes(KeyFile))!;
        File.WriteAllBytes(KeyFile, protector.Protect(change(blob))!);
    }

    [WindowsFact]
    public void Hello_KeyFileThatCannotBeRead_IsNeverReplaced()
    {
        VaultBehindHello();
        RewriteHelloKey(blob => blob[..20]);
        byte[] keyBefore = File.ReadAllBytes(KeyFile);

        Record.Exception(() => new Vault().TryUnlock());

        Assert.Equal(keyBefore, File.ReadAllBytes(KeyFile));
        Assert.Empty(SetAside);
    }

    [Fact]
    public void Hello_KeyFileFromANewerVersion_StillLetsTheVaultBeMadeAndSaysWhy()
    {
        if (!OperatingSystem.IsWindows()) return;
        VaultBehindHello();
        RewriteHelloKey(blob =>
        {
            blob[8] = 2;
            return blob;
        });

        var vault = new Vault();

        Assert.True(vault.IsLocked);
        Assert.Throws<VaultLockedException>(vault.Unlock);
    }

    [WindowsFact]
    public void Hello_TurnedOffAgain_OpensWithoutAGesture()
    {
        var (id, hello) = VaultBehindHello();
        new Vault().DisableHello();
        WindowsHelloProtector.Lock();
        int before = hello.Gestures;

        var vault = new Vault();

        Assert.False(vault.IsLocked);
        Assert.Equal("behind hello", vault.Get(id));
        Assert.Equal(before, hello.Gestures);
    }

    [WindowsFact]
    public void PluginApprovals_WhileTheVaultIsLocked_CountForNothing()
    {
        var (_, hello) = VaultBehindHello();
        new PluginApprovals(new Vault()).Add("BEFORE");
        WindowsHelloProtector.Lock();
        int before = hello.Gestures;

        var locked = new PluginApprovals(new Vault());

        Assert.False(locked.Contains("BEFORE"));
        Assert.Equal(before, hello.Gestures);
    }
}
