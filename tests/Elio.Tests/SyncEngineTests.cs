using System.Text.Json;
using Elio.Core;
using Elio.Sync;

namespace Elio.Tests;

/// <summary>
/// Two devices keeping one library in step through the real sync server on the loopback address. Every key
/// derivation here is the real one, so these are slower than most.
/// </summary>
public sealed class SyncEngineTests
{
    private const string Passphrase = "correct horse battery staple";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    /// <summary>One device's library and the key it keeps, as the app would hand them over.</summary>
    private sealed class Device : ISyncLocal
    {
        public SyncDocument State { get; set; } = new();

        public byte[]? Key { get; private set; }

        /// <summary>Runs once, the next time the engine reads this device's library.</summary>
        public Action? BeforeNextCapture { get; set; }

        public SyncDocument Capture()
        {
            Action? before = BeforeNextCapture;
            BeforeNextCapture = null;
            before?.Invoke();
            return State.Clone();
        }

        public void Apply(SyncDocument merged) => State = merged.Clone();

        public byte[]? LoadDataKey() => Key;

        public void SaveDataKey(byte[]? key) => Key = key;
    }

    private static SyncEngine Engine(RunningSyncServer server, string account, Device device) =>
        new(new SyncServerClient(server.Address, _ => Task.FromResult(account), server.Client()), device);

    private static HostEntry Host(string name) => new() { Name = name, Address = name + ".example.invalid", ModifiedUtc = DateTime.UtcNow };

    [Fact]
    public async Task TwoDevices_ShareEditsAndRemovalsThroughTheServer()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync();
        string account = RunningSyncServer.NewOwnKey();
        Device laptop = new(), phone = new();
        SyncEngine onLaptop = Engine(server, account, laptop), onPhone = Engine(server, account, phone);

        HostEntry web = Host("web");
        laptop.State.Hosts.Add(web);
        string recovery = await onLaptop.CreateAsync(Passphrase, CancellationToken.None).WaitAsync(Patience);
        Assert.False(string.IsNullOrWhiteSpace(recovery));

        await onPhone.UnlockAsync(Passphrase, CancellationToken.None).WaitAsync(Patience);
        Assert.Equal(laptop.Key, phone.Key);

        SyncResult first = await onPhone.SyncAsync(CancellationToken.None).WaitAsync(Patience);
        Assert.Equal(("web", false), (Assert.Single(phone.State.Hosts).Name, first.Uploaded));

        // The phone removes it, the laptop hears of it.
        phone.State.Hosts.Clear();
        phone.State.Deleted[web.Id] = DateTime.UtcNow.AddSeconds(1);
        Assert.True((await onPhone.SyncAsync(CancellationToken.None).WaitAsync(Patience)).Uploaded);
        await onLaptop.SyncAsync(CancellationToken.None).WaitAsync(Patience);

        Assert.Empty(laptop.State.Hosts);
        Assert.True(laptop.State.Deleted.ContainsKey(web.Id));
    }

    [Fact]
    public async Task Server_SeesOnlyCiphertext()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync();
        string account = RunningSyncServer.NewOwnKey();
        var laptop = new Device();
        laptop.State.Hosts.Add(Host("very-private-server"));
        laptop.State.Secrets[Guid.NewGuid()] = new VaultEntry { Secret = "hunter2-password", ModifiedUtc = DateTime.UtcNow };

        await Engine(server, account, laptop).CreateAsync(Passphrase, CancellationToken.None).WaitAsync(Patience);

        string stored = await server.Client(account).GetStringAsync("v1/vault");
        Assert.DoesNotContain("very-private-server", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("correct horse", stored, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(laptop.Key!), stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unlock_WithTheWrongPassphrase_FailsAndKeepsNoKey_TheRecoveryKeyWorks()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync();
        string account = RunningSyncServer.NewOwnKey();
        var laptop = new Device();
        string recovery = await Engine(server, account, laptop).CreateAsync(Passphrase, CancellationToken.None).WaitAsync(Patience);

        var phone = new Device();
        SyncEngine onPhone = Engine(server, account, phone);
        await Assert.ThrowsAsync<WrongPassphraseException>(() => onPhone.UnlockAsync("correct horse battery stable", CancellationToken.None).WaitAsync(Patience));
        Assert.False(onPhone.IsUnlocked);
        await Assert.ThrowsAsync<InvalidOperationException>(() => onPhone.SyncAsync(CancellationToken.None));

        await onPhone.UnlockAsync(recovery, CancellationToken.None).WaitAsync(Patience);
        Assert.Equal(laptop.Key, phone.Key);
    }

    [Fact]
    public async Task Sync_OfDataSealedWithAnotherKey_FailsInsteadOfMergingGarbage()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync();
        string account = RunningSyncServer.NewOwnKey();
        var laptop = new Device();
        laptop.State.Hosts.Add(Host("web"));
        SyncEngine onLaptop = Engine(server, account, laptop);
        await onLaptop.CreateAsync(Passphrase, CancellationToken.None).WaitAsync(Patience);

        // Someone who can write to the account, but has never had the key, replaces what is stored.
        using JsonDocument stored = JsonDocument.Parse(await server.Client(account).GetStringAsync("v1/vault"));
        var forged = new VaultUpload
        {
            ExpectedVersion = stored.RootElement.GetProperty("version").GetInt64(),
            Keyring = stored.RootElement.GetProperty("keyring").GetString()!,
            Data = Convert.ToBase64String(SyncCrypto.Seal(SyncCrypto.NewDataKey(), new SyncDocument { Hosts = [Host("planted")] })),
        };
        await new SyncServerClient(server.Address, _ => Task.FromResult(account), server.Client()).PutAsync(forged, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(() => onLaptop.SyncAsync(CancellationToken.None).WaitAsync(Patience));
        Assert.Equal("web", Assert.Single(laptop.State.Hosts).Name);
    }

    [Fact]
    public async Task Sync_ThatLosesARaceWithAnotherDevice_MergesWithItsUploadAndTriesAgain()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync();
        string account = RunningSyncServer.NewOwnKey();
        Device laptop = new(), phone = new();
        SyncEngine onLaptop = Engine(server, account, laptop), onPhone = Engine(server, account, phone);
        await onLaptop.CreateAsync(Passphrase, CancellationToken.None).WaitAsync(Patience);
        await onPhone.UnlockAsync(Passphrase, CancellationToken.None).WaitAsync(Patience);

        laptop.State.Hosts.Add(Host("from-laptop"));
        phone.State.Hosts.Add(Host("from-phone"));

        // While the laptop is between fetching and uploading, the phone uploads first.
        laptop.BeforeNextCapture = () => onPhone.SyncAsync(CancellationToken.None).WaitAsync(Patience).GetAwaiter().GetResult();
        SyncResult result = await onLaptop.SyncAsync(CancellationToken.None).WaitAsync(Patience);
        await onPhone.SyncAsync(CancellationToken.None).WaitAsync(Patience);

        Assert.True(result.Uploaded);
        Assert.Equal(3, result.Version);
        Assert.Equal(["from-laptop", "from-phone"], laptop.State.Hosts.Select(h => h.Name).Order());
        Assert.Equal(["from-laptop", "from-phone"], phone.State.Hosts.Select(h => h.Name).Order());
    }

    [Fact]
    public async Task ChangePassphrase_TheOldOneStopsWorkingAndTheNewOneOpens()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync();
        string account = RunningSyncServer.NewOwnKey();
        var laptop = new Device();
        SyncEngine onLaptop = Engine(server, account, laptop);
        await onLaptop.CreateAsync(Passphrase, CancellationToken.None).WaitAsync(Patience);

        await onLaptop.ChangePassphraseAsync("an entirely new passphrase", CancellationToken.None).WaitAsync(Patience);

        SyncEngine onPhone = Engine(server, account, new Device());
        await Assert.ThrowsAsync<WrongPassphraseException>(() => onPhone.UnlockAsync(Passphrase, CancellationToken.None).WaitAsync(Patience));
        await onPhone.UnlockAsync("an entirely new passphrase", CancellationToken.None).WaitAsync(Patience);
    }

    [Fact]
    public async Task Create_ForAnAccountThatAlreadySyncs_IsRefused()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync();
        string account = RunningSyncServer.NewOwnKey();
        await Engine(server, account, new Device()).CreateAsync(Passphrase, CancellationToken.None).WaitAsync(Patience);

        var other = new Device();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Engine(server, account, other).CreateAsync("another passphrase here", CancellationToken.None).WaitAsync(Patience));
        Assert.Null(other.Key);
    }
}
