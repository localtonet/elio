using Elio.Core;
using Elio.Sync;

namespace Elio.Tests;

/// <summary>One keyring for the whole class: every key derivation costs 64 MiB and about half a second.</summary>
public sealed class KeyringFixture
{
    public const string Passphrase = "correct horse battery staple";
    public const string NewPassphrase = "a different passphrase";

    public byte[] DataKey { get; } = SyncCrypto.NewDataKey();
    public Keyring Keyring { get; }
    public string RecoveryKey { get; }
    public Keyring Changed { get; }

    public KeyringFixture()
    {
        (Keyring, RecoveryKey) = SyncCrypto.CreateKeyring(DataKey, Passphrase);
        Changed = SyncCrypto.ChangePassphrase(Keyring, DataKey, NewPassphrase);
    }
}

public sealed class SyncCryptoTests(KeyringFixture fixture) : IClassFixture<KeyringFixture>
{
    [Fact]
    public void Unlock_WithPassphrase_YieldsDataKey() =>
        Assert.Equal(fixture.DataKey, SyncCrypto.Unlock(fixture.Keyring, KeyringFixture.Passphrase));

    [Fact]
    public void Unlock_WithRecoveryKey_YieldsDataKey() =>
        Assert.Equal(fixture.DataKey, SyncCrypto.Unlock(fixture.Keyring, fixture.RecoveryKey));

    [Fact]
    public void Unlock_WithRecoveryKeyTypedInLowerCaseWithSpaces_YieldsDataKey() =>
        Assert.Equal(fixture.DataKey, SyncCrypto.Unlock(fixture.Keyring, fixture.RecoveryKey.ToLowerInvariant().Replace('-', ' ')));

    [Fact]
    public void RecoveryKey_IsThirteenGroupsOfFour()
    {
        string[] groups = fixture.RecoveryKey.Split('-');
        Assert.Equal(13, groups.Length);
        Assert.All(groups, group => Assert.Equal(4, group.Length));
    }

    [Fact]
    public void Unlock_WithWrongPassphrase_Throws() =>
        Assert.Throws<WrongPassphraseException>(() => SyncCrypto.Unlock(fixture.Keyring, "not the passphrase"));

    [Fact]
    public void ChangePassphrase_NewPassphraseYieldsSameDataKey() =>
        Assert.Equal(fixture.DataKey, SyncCrypto.Unlock(fixture.Changed, KeyringFixture.NewPassphrase));

    [Fact]
    public void ChangePassphrase_RecoveryKeyStillWorks() =>
        Assert.Equal(fixture.DataKey, SyncCrypto.Unlock(fixture.Changed, fixture.RecoveryKey));

    [Fact]
    public void ChangePassphrase_OldPassphraseStopsWorking() =>
        Assert.Throws<WrongPassphraseException>(() => SyncCrypto.Unlock(fixture.Changed, KeyringFixture.Passphrase));

    [Theory]
    [InlineData(1024, 3, 4)] // too little memory
    [InlineData(64 * 1024 - 1, 3, 4)] // just under the least Elio ever used
    [InlineData(64 * 1024, 1, 4)] // too few iterations
    [InlineData(64 * 1024, 1000, 4)] // too many iterations
    [InlineData(int.MaxValue, 3, 4)] // absurdly large memory
    [InlineData(1024 * 1024 + 1, 3, 4)] // just over the most that is allowed
    [InlineData(64 * 1024, 3, 0)] // no threads
    [InlineData(64 * 1024, 3, 1000)] // too many threads
    public void Unlock_WithKdfSettingsOutOfBounds_IsRejected(int memoryKiB, int iterations, int parallelism)
    {
        Keyring keyring = WithKdf(new KdfSettings { Salt = fixture.Keyring.Kdf.Salt, MemoryKiB = memoryKiB, Iterations = iterations, Parallelism = parallelism });

        // Were the settings used before being checked, the absurd ones would run out of memory or never return.
        Assert.Throws<InvalidDataException>(() => SyncCrypto.Unlock(keyring, KeyringFixture.Passphrase));
    }

    [Fact]
    public void Unlock_WithTooShortSalt_IsRejected()
    {
        KdfSettings real = fixture.Keyring.Kdf;
        Keyring keyring = WithKdf(new KdfSettings { Salt = Convert.ToBase64String(new byte[4]), MemoryKiB = real.MemoryKiB, Iterations = real.Iterations, Parallelism = real.Parallelism });

        Assert.Throws<InvalidDataException>(() => SyncCrypto.Unlock(keyring, KeyringFixture.Passphrase));
    }

    [Fact]
    public void Unlock_WithUnknownKdfName_IsNotSupported()
    {
        KdfSettings real = fixture.Keyring.Kdf;
        Keyring keyring = WithKdf(new KdfSettings { Name = "md5", Salt = real.Salt, MemoryKiB = real.MemoryKiB, Iterations = real.Iterations, Parallelism = real.Parallelism });

        Assert.Throws<NotSupportedException>(() => SyncCrypto.Unlock(keyring, KeyringFixture.Passphrase));
    }

    private Keyring WithKdf(KdfSettings kdf) =>
        new() { Kdf = kdf, Passphrase = fixture.Keyring.Passphrase, Recovery = fixture.Keyring.Recovery };
}

/// <summary>Sealing and opening a document needs a data key only, so nothing here derives one from a passphrase.</summary>
public sealed class SyncDocumentSealTests
{
    private static SyncDocument Sample() => new()
    {
        Hosts = [new HostEntry { Name = "web", Address = "web.example.com", Port = 2222, User = "deploy" }],
        Snippets = [new Snippet { Name = "uptime", Command = "uptime" }],
    };

    [Fact]
    public void SealThenOpen_RoundTrips()
    {
        byte[] key = SyncCrypto.NewDataKey();

        SyncDocument opened = SyncCrypto.Open(key, SyncCrypto.Seal(key, Sample()));

        HostEntry host = Assert.Single(opened.Hosts);
        Assert.Equal(("web", "web.example.com", 2222, "deploy"), (host.Name, host.Address, host.Port, host.User));
        Assert.Equal("uptime", Assert.Single(opened.Snippets).Command);
    }

    [Fact]
    public void Seal_UsesAFreshNonceEachTime()
    {
        byte[] key = SyncCrypto.NewDataKey();
        SyncDocument document = Sample();

        Assert.NotEqual(SyncCrypto.Seal(key, document), SyncCrypto.Seal(key, document));
    }

    [Fact]
    public void Open_WithFlippedCiphertextByte_Fails()
    {
        byte[] key = SyncCrypto.NewDataKey();
        byte[] sealedData = SyncCrypto.Seal(key, Sample());
        sealedData[^1] ^= 0x01;

        Assert.Throws<InvalidDataException>(() => SyncCrypto.Open(key, sealedData));
    }

    [Fact]
    public void Open_WithAnotherKey_Fails()
    {
        byte[] sealedData = SyncCrypto.Seal(SyncCrypto.NewDataKey(), Sample());

        Assert.Throws<InvalidDataException>(() => SyncCrypto.Open(SyncCrypto.NewDataKey(), sealedData));
    }

    [Fact]
    public void Open_WithDataTooShortToHoldNonceAndTag_Fails() =>
        Assert.Throws<InvalidDataException>(() => SyncCrypto.Open(SyncCrypto.NewDataKey(), new byte[27]));
}
