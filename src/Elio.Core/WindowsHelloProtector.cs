using System.Security.Cryptography;

namespace Elio.Core;

/// <summary>Why the vault could not be opened.</summary>
public enum VaultLockReason
{
    /// <summary>The user closed the Windows Hello prompt.</summary>
    Cancelled,

    /// <summary>Windows Hello is not set up, or cannot be reached right now.</summary>
    Unavailable,

    /// <summary>The Windows Hello key that locked the vault is no longer on this device.</summary>
    KeyMissing,

    /// <summary>Windows Hello answered, but not with what locked the vault: another device, or Hello was set up again.</summary>
    Mismatch,

    /// <summary>Anything else, such as an error from the security chip.</summary>
    Failed,
}

/// <summary>
/// The vault is locked and could not be opened. Nothing on disk was changed. The message is already in the
/// user's language and can be shown as it is.
/// </summary>
public sealed class VaultLockedException(VaultLockReason reason, string message) : Exception(message)
{
    public VaultLockReason Reason { get; } = reason;
}

/// <summary>The one step that needs the user: signing a challenge with their Windows Hello key.</summary>
internal interface IHelloSigner
{
    /// <summary>
    /// The signature of the challenge. It must come out the same every time for the same user on the same
    /// device, and must not be obtainable without a Hello gesture.
    /// </summary>
    /// <param name="create">Make the Hello key if there is none yet. False when opening a vault, where a new key would be useless.</param>
    /// <exception cref="VaultLockedException">The user cancelled, or Hello could not sign.</exception>
    byte[] Sign(byte[] challenge, bool create);
}

/// <summary>
/// Makes reading the vault's key need a Windows Hello gesture: a PIN, a fingerprint or a face.
/// </summary>
/// <remarks>
/// Hello cannot encrypt for us in a way that always asks, but it can sign. A PKCS#1 v1.5 signature of the same
/// data by the same key is always the same bytes, and only this user on this device can make it, after a
/// gesture. So a random challenge is kept beside the wrapped key, its signature is turned into an encryption
/// key, and that wraps the vault's key. The result then goes through the usual protector as well, so it stays
/// tied to the Windows account exactly as it was without Hello.
/// </remarks>
public sealed class WindowsHelloProtector : IKeyProtector
{
    private const byte Version = 1;
    private const int ChallengeSize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KekSize = 32;

    private static readonly byte[] Magic = "ELIOHELO"u8.ToArray();
    private static readonly byte[] KekInfo = "elio-vault-hello-v1"u8.ToArray();

    // Magic, version, challenge. Everything before the nonce is also fed to AES-GCM, so none of it can be swapped.
    private static readonly int HeaderSize = Magic.Length + 1 + ChallengeSize;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, (byte[] Key, TimeSpan At)> Unlocked = [];
    private static int _epoch;

    private readonly IKeyProtector _inner;
    private readonly IHelloSigner? _signer;

    /// <summary>The window the Hello prompt belongs to. Called on whichever thread unlocks. Unset or zero means the foreground window.</summary>
    public static Func<nint>? Window { get; set; }

    /// <summary>How long one gesture keeps the vault open. Null, the default, means until the app closes.</summary>
    public static TimeSpan? Relock { get; set; }

    /// <summary>Stands in for Windows Hello in tests, which must never raise the real prompt.</summary>
    internal static IHelloSigner? SignerOverride { get; set; }

    /// <summary>Time that only moves forward, so changing the system clock cannot keep the vault open.</summary>
    internal static Func<TimeSpan> Clock { get; set; } = () => TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>Changes whenever a remembered key is forgotten. A vault compares it to know its own copy is stale.</summary>
    internal static int Epoch
    {
        get
        {
            lock (Gate)
            {
                Expire();
                return _epoch;
            }
        }
    }

    /// <param name="inner">What the Hello-wrapped key is passed through before it is stored: DPAPI on Windows.</param>
    public WindowsHelloProtector(IKeyProtector inner) => _inner = inner;

    internal WindowsHelloProtector(IKeyProtector inner, IHelloSigner signer) : this(inner) => _signer = signer;

    /// <summary>Whether Windows Hello is set up for this user. Never prompts and never throws.</summary>
    public static bool IsAvailable() => OperatingSystem.IsWindows() && CngHelloSigner.IsAvailable();

    /// <summary>Forgets every key a gesture has unlocked, so the next use asks again.</summary>
    public static void Lock()
    {
        lock (Gate)
        {
            foreach ((byte[] key, _) in Unlocked.Values) CryptographicOperations.ZeroMemory(key);
            Unlocked.Clear();
            _epoch++;
        }
    }

    /// <summary>Whether these bytes, already out of the inner protector, are a key wrapped by Windows Hello.</summary>
    public static bool IsHelloBlob(ReadOnlySpan<byte> unwrapped) => unwrapped.StartsWith(Magic);

    /// <summary>Whether a stored key needs Windows Hello to be read. Never prompts.</summary>
    public bool IsProtected(byte[] stored) => _inner.Unprotect(stored) is { } unwrapped && IsHelloBlob(unwrapped);

    /// <summary>Wraps the key behind Windows Hello. Asks for a gesture twice: once to lock, once to prove it can be opened again.</summary>
    /// <exception cref="VaultLockedException">The user cancelled, or Hello cannot be used.</exception>
    public byte[]? Protect(byte[] key)
    {
        IHelloSigner signer = Signer();
        byte[] challenge = RandomNumberGenerator.GetBytes(ChallengeSize);
        byte[] signature = signer.Sign(challenge, create: true);
        byte[] again = signer.Sign(challenge, create: false);
        try
        {
            // Everything rests on the signature repeating. Find out now, while the key is still readable
            // without Hello, rather than after the vault has been locked with something that cannot be redone.
            if (!CryptographicOperations.FixedTimeEquals(signature, again))
                throw new VaultLockedException(VaultLockReason.Failed, L.T("Windows Hello on this device cannot be used to lock the vault."));

            byte[] blob = Seal(key, challenge, signature);
            byte[]? stored = _inner.Protect(blob);
            if (stored is not null) Remember(blob, key);
            return stored;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
            CryptographicOperations.ZeroMemory(again);
        }
    }

    /// <summary>
    /// The key back, after a gesture unless one was given recently. A key stored without Hello comes back as
    /// it is, so one reader serves both. Null means the inner protector could not read the file at all.
    /// </summary>
    /// <exception cref="VaultLockedException">The user cancelled, or Hello could not open it. The stored bytes are still good.</exception>
    public byte[]? Unprotect(byte[] stored) => Open(stored, prompt: true, out _);

    /// <param name="prompt">False to answer only from memory. A Hello-wrapped key that needs a gesture then comes back null.</param>
    /// <param name="hello">Whether the stored key is wrapped by Hello.</param>
    internal byte[]? Open(byte[] stored, bool prompt, out bool hello)
    {
        hello = false;
        byte[]? blob = _inner.Unprotect(stored);
        if (blob is null || !IsHelloBlob(blob)) return blob;

        hello = true;
        if (blob.Length <= HeaderSize + NonceSize + TagSize)
            throw new VaultLockedException(VaultLockReason.Failed, L.T("The vault's key file is damaged."));
        if (blob[Magic.Length] != Version)
            throw new VaultLockedException(VaultLockReason.Failed, L.T("The vault was locked by a newer version of Elio."));

        if (Recall(blob) is byte[] remembered) return remembered;
        if (!prompt) return null;

        byte[] challenge = blob.AsSpan(Magic.Length + 1, ChallengeSize).ToArray();
        byte[] signature = Signer().Sign(challenge, create: false);
        byte[] kek = DeriveKek(signature, challenge);
        byte[] key = new byte[blob.Length - HeaderSize - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(kek, TagSize);
            aes.Decrypt(
                blob.AsSpan(HeaderSize, NonceSize),
                blob.AsSpan(HeaderSize + NonceSize + TagSize),
                blob.AsSpan(HeaderSize + NonceSize, TagSize),
                key,
                blob.AsSpan(0, HeaderSize));
        }
        catch (CryptographicException)
        {
            // A different signature: another device's Hello, a Hello that was set up again, or a damaged file.
            throw new VaultLockedException(VaultLockReason.Mismatch,
                L.T("Windows Hello on this device cannot open the vault. It was locked on another device, or Windows Hello has been set up again since."));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
            CryptographicOperations.ZeroMemory(kek);
        }

        Remember(blob, key);
        return key;
    }

    private IHelloSigner Signer()
    {
        if (_signer is not null) return _signer;
        if (SignerOverride is IHelloSigner replaced) return replaced;
        if (OperatingSystem.IsWindows()) return new CngHelloSigner();
        throw new VaultLockedException(VaultLockReason.Unavailable, L.T("Windows Hello is not available on this device."));
    }

    // Layout: magic, version, challenge, nonce, tag, wrapped key.
    private static byte[] Seal(byte[] key, byte[] challenge, byte[] signature)
    {
        byte[] blob = new byte[HeaderSize + NonceSize + TagSize + key.Length];
        Magic.CopyTo(blob, 0);
        blob[Magic.Length] = Version;
        challenge.CopyTo(blob, Magic.Length + 1);
        RandomNumberGenerator.Fill(blob.AsSpan(HeaderSize, NonceSize));

        byte[] kek = DeriveKek(signature, challenge);
        try
        {
            using var aes = new AesGcm(kek, TagSize);
            aes.Encrypt(
                blob.AsSpan(HeaderSize, NonceSize),
                key,
                blob.AsSpan(HeaderSize + NonceSize + TagSize),
                blob.AsSpan(HeaderSize + NonceSize, TagSize),
                blob.AsSpan(0, HeaderSize));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }

        return blob;
    }

    private static byte[] DeriveKek(byte[] signature, byte[] challenge) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, signature, KekSize, challenge, KekInfo);

    // Remembered under a hash of the whole blob, so a file that has changed in any way is never answered from memory.
    private static string Id(byte[] blob) => Convert.ToHexString(SHA256.HashData(blob));

    private static void Remember(byte[] blob, byte[] key)
    {
        lock (Gate) Unlocked[Id(blob)] = ((byte[])key.Clone(), Clock());
    }

    private static byte[]? Recall(byte[] blob)
    {
        lock (Gate)
        {
            Expire();
            return Unlocked.TryGetValue(Id(blob), out var entry) ? (byte[])entry.Key.Clone() : null;
        }
    }

    // There is no timer: a key that has outlived Relock is dropped the next time anyone looks.
    private static void Expire()
    {
        if (Relock is not TimeSpan limit) return;

        TimeSpan now = Clock();
        foreach ((string id, (byte[] key, TimeSpan at)) in Unlocked.ToArray())
        {
            if (now - at < limit) continue;
            CryptographicOperations.ZeroMemory(key);
            Unlocked.Remove(id);
            _epoch++;
        }
    }
}
