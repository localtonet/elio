using System.Security.Cryptography;
using System.Text.Json;

namespace Elio.Core;

/// <summary>
/// Passwords and key passphrases, encrypted on disk. The encryption key is itself locked to the
/// signed-in user by the operating system, so a copied file is useless on its own.
/// </summary>
public sealed class Vault
{
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly byte[] Context = "elio-vault-v1"u8.ToArray();

    private readonly string _keyPath = Path.Combine(ElioPaths.DataDirectory, "vault.key");
    private readonly string _dataPath = Path.Combine(ElioPaths.DataDirectory, "vault.dat");
    private readonly Lock _gate = new();

    // How the key is kept in its file: by the platform head's protector, or by DPAPI on Windows. Null where
    // the key does not live in a file at all.
    private readonly IKeyProtector? _protector;

    // On Windows the file is always read through this. It hands back a key stored without Windows Hello as
    // it is, and asks for a gesture for one stored with it.
    private readonly WindowsHelloProtector? _hello;

    private readonly bool _available;
    private byte[]? _key;

    // Null while the vault is locked behind Windows Hello.
    private VaultContents? _contents;

    // Set when the key in memory came through Windows Hello, with the moment it did, so that it is dropped
    // again when Hello is told to forget.
    private bool _keyFromHello;
    private int _helloEpoch;

    /// <summary>False where no system key store is wired up yet. Secrets then last only as long as the app.</summary>
    public bool IsAvailable => _available;

    /// <summary>Whether this vault can be put behind Windows Hello at all. <see cref="WindowsHelloProtector.IsAvailable"/> says whether Hello is set up.</summary>
    public bool HelloSupported => _hello is not null && _available;

    /// <summary>Whether the key file on disk needs a Windows Hello gesture to be read. Never prompts.</summary>
    public bool HelloEnabled
    {
        get
        {
            try
            {
                return _hello is not null && File.Exists(_keyPath) && _hello.IsProtected(File.ReadAllBytes(_keyPath));
            }
            catch (IOException)
            {
                return false;
            }
        }
    }

    /// <summary>Whether the next use will ask for a Windows Hello gesture. Never prompts.</summary>
    public bool IsLocked
    {
        get
        {
            lock (_gate)
            {
                ForgetIfRelocked();
                return _available && _contents is null;
            }
        }
    }

    /// <summary>Raised when a server's secret is set or removed, so it can be synced.</summary>
    public event Action? Changed;

    public Vault()
    {
        _protector = VaultKeys.Platform;
        if (OperatingSystem.IsWindows())
        {
            _protector ??= new DpapiProtector(Context);
            _hello = new WindowsHelloProtector(_protector);
        }

        // Opening the app must not raise a Windows Hello prompt, so a vault behind Hello starts out locked
        // and asks on first use.
        _key = LoadOrCreateKey(prompt: false, out bool locked);
        _available = _key is not null || locked;
        if (!locked) _contents = _key is null ? new VaultContents() : Read(_key);
    }

    /// <summary>
    /// Opens the vault if it is locked, asking for a Windows Hello gesture. The prompt blocks the calling
    /// thread until it is answered. Reading or changing a secret does the same on its own; this is for asking
    /// at a moment of the app's choosing.
    /// </summary>
    /// <exception cref="VaultLockedException">The user cancelled, or Hello could not open the vault. Nothing was changed.</exception>
    public void Unlock()
    {
        lock (_gate) _declined = false;
        _ = Open();
    }

    /// <summary>As <see cref="Unlock"/>, but answers false in place of throwing.</summary>
    public bool TryUnlock()
    {
        try
        {
            Unlock();
            return true;
        }
        catch (VaultLockedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Puts the vault's key behind Windows Hello. The stored secrets are untouched: only the key file is
    /// rewritten. Windows asks for a gesture twice, to lock and to prove the lock opens.
    /// </summary>
    /// <exception cref="VaultLockedException">The user cancelled, or Hello cannot be used. The vault is as it was.</exception>
    public void EnableHello()
    {
        lock (_gate)
        {
            if (_hello is null || !_available)
                throw new VaultLockedException(VaultLockReason.Unavailable, L.T("Windows Hello is not available on this device."));
            if (HelloEnabled) return;

            _ = Open();
            if (_key is null || _hello.Protect(_key) is not byte[] locked)
                throw new VaultLockedException(VaultLockReason.Failed, L.T("The vault's key could not be saved."));

            AtomicFile.Write(_keyPath, stream => stream.Write(locked));

            // The copy of the previous file that is kept beside it would still open without a gesture.
            File.Delete(_keyPath + ".bak");
            _keyFromHello = true;
            _helloEpoch = WindowsHelloProtector.Epoch;
        }
    }

    /// <summary>Takes the vault's key back out from behind Windows Hello. Asks for a gesture if the vault is locked.</summary>
    /// <exception cref="VaultLockedException">The user cancelled, or Hello could not open the vault. The vault is as it was.</exception>
    public void DisableHello()
    {
        lock (_gate)
        {
            if (_protector is null || !HelloEnabled) return;

            _ = Open();
            if (_key is null || _protector.Protect(_key) is not byte[] locked)
                throw new VaultLockedException(VaultLockReason.Failed, L.T("The vault's key could not be saved."));

            AtomicFile.Write(_keyPath, stream => stream.Write(locked));
            _keyFromHello = false;
        }
    }

    /// <summary>
    /// Starts an empty vault with a new key, for when the old one can never be opened again: Windows Hello
    /// was reset, or the files came from another device. The old files are kept beside the new ones under
    /// another name, not deleted.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            string suffix = $".unreadable-{DateTime.UtcNow:yyyyMMddHHmmss}";
            if (File.Exists(_keyPath)) File.Move(_keyPath, _keyPath + suffix, overwrite: true);
            if (File.Exists(_dataPath)) File.Move(_dataPath, _dataPath + suffix, overwrite: true);

            Forget();
            _key = LoadOrCreateKey(prompt: false, out _);
            _contents = new VaultContents();
        }
    }

    private bool _declined;

    /// <summary>
    /// The secrets, or null while the vault is locked and the user has already said no to opening it. Asking again on
    /// every password lookup would bury them in prompts, so after one refusal the vault behaves as an empty one that
    /// keeps nothing, until <see cref="Unlock"/> or <see cref="TryUnlock"/> is called.
    /// </summary>
    private VaultContents? OpenUnlessDeclined()
    {
        lock (_gate)
        {
            if (_declined && _contents is null) return null;

            try
            {
                VaultContents contents = Open();
                _declined = false;
                return contents;
            }
            catch (VaultLockedException)
            {
                _declined = true;
                return null;
            }
        }
    }

    // Every read and write of the secrets comes through here, which is what makes a locked vault ask.
    private VaultContents Open()
    {
        lock (_gate)
        {
            ForgetIfRelocked();
            if (_contents is not null) return _contents;

            _key = LoadOrCreateKey(prompt: true, out _);
            return _contents = _key is null ? new VaultContents() : Read(_key);
        }
    }

    private void ForgetIfRelocked()
    {
        if (_keyFromHello && _helloEpoch != WindowsHelloProtector.Epoch) Forget();
    }

    private void Forget()
    {
        if (_key is not null) CryptographicOperations.ZeroMemory(_key);
        _key = null;
        _contents = null;
        _keyFromHello = false;
    }

    private byte[]? LoadOrCreateKey(bool prompt, out bool locked)
    {
        locked = false;
        if (_protector is not null) return LoadOrCreateProtectedKey(_protector, prompt, out locked);
        return SystemKeyStore.Supported ? LoadOrCreateStoredKey() : null;
    }

    // The key stays in a file, wrapped by something only this user on this device can undo: the device's own
    // key store on a phone, the Windows account on Windows, and Windows Hello on top of that if it is turned on.
    private byte[]? LoadOrCreateProtectedKey(IKeyProtector protector, bool prompt, out bool locked)
    {
        locked = false;
        if (File.Exists(_keyPath))
        {
            byte[] stored = File.ReadAllBytes(_keyPath);
            bool hello = false;
            byte[]? existing = _hello is not null ? _hello.Open(stored, prompt, out hello) : protector.Unprotect(stored);
            if (existing is { Length: KeySize })
            {
                _keyFromHello = hello;
                if (hello) _helloEpoch = WindowsHelloProtector.Epoch;
                return existing;
            }

            if (hello)
            {
                // A good file that only a gesture can open. It must never be replaced by a new key.
                locked = true;
                return null;
            }

            // Created by another user or machine. Move it aside rather than silently losing it.
            if (OperatingSystem.IsWindows())
                File.Move(_keyPath, $"{_keyPath}.unreadable-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);
        }

        byte[] key = RandomNumberGenerator.GetBytes(KeySize);
        if (protector.Protect(key) is not byte[] wrapped) return null;

        AtomicFile.Write(_keyPath, stream => stream.Write(wrapped));
        return key;
    }

    // On macOS and Linux the key lives in the system's keychain, not in a file beside the vault.
    private static byte[]? LoadOrCreateStoredKey()
    {
        if (SystemKeyStore.Read(ElioPaths.DataDirectory) is { Length: KeySize } stored) return stored;

        byte[] key = RandomNumberGenerator.GetBytes(KeySize);
        return SystemKeyStore.Write(ElioPaths.DataDirectory, key) ? key : null;
    }

    private VaultContents Read(byte[] key)
    {
        if (!File.Exists(_dataPath)) return new VaultContents();

        byte[] file = File.ReadAllBytes(_dataPath);
        if (file.Length < NonceSize + TagSize) return new VaultContents();

        byte[] plain = new byte[file.Length - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(file.AsSpan(0, NonceSize), file.AsSpan(NonceSize + TagSize), file.AsSpan(NonceSize, TagSize), plain, Context);
            return Parse(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            File.Move(_dataPath, $"{_dataPath}.unreadable-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);
            return new VaultContents();
        }
    }

    private static VaultContents Parse(byte[] plain)
    {
        using JsonDocument document = JsonDocument.Parse(plain);
        if (document.RootElement.TryGetProperty("Format", out _))
            return JsonSerializer.Deserialize(plain, CoreJsonContext.Default.VaultContents) ?? new VaultContents();

        // The first version kept a plain map of server id to secret, with no times.
        var contents = new VaultContents();
        foreach (var (id, secret) in JsonSerializer.Deserialize(plain, CoreJsonContext.Default.DictionaryStringString) ?? [])
            if (Guid.TryParse(id, out Guid hostId)) contents.Entries[hostId] = new VaultEntry { Secret = secret };
        return contents;
    }

    private void Write()
    {
        if (_key is null || _contents is null) return;

        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(_contents, CoreJsonContext.Default.VaultContents);
        byte[] file = new byte[NonceSize + TagSize + plain.Length];
        RandomNumberGenerator.Fill(file.AsSpan(0, NonceSize));

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(file.AsSpan(0, NonceSize), plain, file.AsSpan(NonceSize + TagSize), file.AsSpan(NonceSize, TagSize), Context);
        AtomicFile.Write(_dataPath, stream => stream.Write(file));
    }

    // Everything below opens the vault first. With Windows Hello turned on that can raise the prompt, and
    // throws VaultLockedException if the user turns it away.

    public string? Get(Guid hostId) => OpenUnlessDeclined() is { } contents && contents.Entries.TryGetValue(hostId, out VaultEntry? entry) ? entry.Secret : null;

    public void Set(Guid hostId, string secret)
    {
        lock (_gate)
        {
            if (OpenUnlessDeclined() is not VaultContents contents) return;
            contents.Entries[hostId] = new VaultEntry { Secret = secret, ModifiedUtc = DateTime.UtcNow };
            contents.Deleted.Remove(hostId);
            Write();
        }

        Changed?.Invoke();
    }

    public void Remove(Guid hostId)
    {
        lock (_gate)
        {
            if (OpenUnlessDeclined() is not VaultContents contents) return;
            if (!contents.Entries.Remove(hostId)) return;
            contents.Deleted[hostId] = DateTime.UtcNow;
            Write();
        }

        Changed?.Invoke();
    }

    /// <summary>The servers' secrets, for syncing.</summary>
    public IReadOnlyDictionary<Guid, VaultEntry> Entries => OpenUnlessDeclined()?.Entries ?? [];

    public IReadOnlyDictionary<Guid, DateTime> Deleted => OpenUnlessDeclined()?.Deleted ?? [];

    /// <summary>Replaces the servers' secrets with the result of a sync.</summary>
    public void Replace(Dictionary<Guid, VaultEntry> entries, Dictionary<Guid, DateTime> deleted)
    {
        lock (_gate)
        {
            if (OpenUnlessDeclined() is not VaultContents contents) return;
            contents.Entries = entries;
            contents.Deleted = deleted;
            Write();
        }
    }

    /// <summary>A secret that belongs to this device only.</summary>
    public string? GetLocal(string name) => OpenUnlessDeclined()?.Local.GetValueOrDefault(name);

    public void SetLocal(string name, string? value)
    {
        lock (_gate)
        {
            if (OpenUnlessDeclined() is not VaultContents contents) return;
            if (value is null) contents.Local.Remove(name);
            else contents.Local[name] = value;
            Write();
        }
    }
}
