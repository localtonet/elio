using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Elio.Core;
using Konscious.Security.Cryptography;

namespace Elio.Sync;

/// <summary>How a passphrase becomes a key. Stored with the data, so the cost can be raised later.</summary>
public sealed class KdfSettings
{
    public string Name { get; set; } = "argon2id";
    public string Salt { get; set; } = string.Empty;
    public int MemoryKiB { get; set; }
    public int Iterations { get; set; }
    public int Parallelism { get; set; }
}

public sealed class WrappedKey
{
    public string Nonce { get; set; } = string.Empty;
    public string Ciphertext { get; set; } = string.Empty;
}

/// <summary>
/// The data key, locked twice: once with the passphrase and once with the recovery key. The server keeps
/// this, and neither half is any use without one of the two secrets that never leave the user's devices.
/// </summary>
public sealed class Keyring
{
    public int Format { get; set; } = 1;
    public KdfSettings Kdf { get; set; } = new();
    public WrappedKey Passphrase { get; set; } = new();
    public WrappedKey Recovery { get; set; } = new();
}

public sealed class WrongPassphraseException() : Exception(L.T("The password or recovery key is wrong."));

/// <summary>Everything that is encrypted before it leaves the device.</summary>
public static class SyncCrypto
{
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int SaltSize = 16;

    // About half a second on a laptop, and too much memory per guess for a graphics card to try many at once.
    private const int DefaultMemoryKiB = 64 * 1024;
    private const int DefaultIterations = 3;
    private const int DefaultParallelism = 4;

    // The settings come back from the server with the keyring, and the server is not trusted. Lower than any Elio
    // ever used and a guess at the password would be cheap; much higher and unlocking would hang or run out of memory.
    private const int MinMemoryKiB = 64 * 1024;
    private const int MaxMemoryKiB = 1024 * 1024;
    private const int MinIterations = 3;
    private const int MaxIterations = 10;
    private const int MaxParallelism = 16;
    private const int MinSaltSize = 8;

    private static readonly byte[] KeyringContext = "elio-keyring-v1"u8.ToArray();
    private static readonly byte[] DataContext = "elio-data-v1"u8.ToArray();
    private const string RecoveryAlphabet = "ABCDEFGHJKMNPQRSTVWXYZ0123456789";
    private const int RecoveryGroup = 4;

    public static byte[] NewDataKey() => RandomNumberGenerator.GetBytes(KeySize);

    /// <summary>A new keyring for a data key, and the recovery key to show the user once.</summary>
    public static (Keyring Keyring, string RecoveryKey) CreateKeyring(byte[] dataKey, string passphrase)
    {
        var kdf = new KdfSettings
        {
            Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(SaltSize)),
            MemoryKiB = DefaultMemoryKiB,
            Iterations = DefaultIterations,
            Parallelism = DefaultParallelism,
        };

        byte[] recovery = RandomNumberGenerator.GetBytes(KeySize);
        var keyring = new Keyring
        {
            Kdf = kdf,
            Passphrase = Wrap(Derive(passphrase, kdf), dataKey),
            Recovery = Wrap(recovery, dataKey),
        };
        return (keyring, FormatRecovery(recovery));
    }

    /// <summary>The same data key behind a new passphrase. The recovery key keeps working.</summary>
    public static Keyring ChangePassphrase(Keyring keyring, byte[] dataKey, string passphrase)
    {
        // Never the settings of the keyring that came from the server: it could have lowered them, and the key
        // locked here goes straight back to it.
        var kdf = new KdfSettings
        {
            Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(SaltSize)),
            MemoryKiB = DefaultMemoryKiB,
            Iterations = DefaultIterations,
            Parallelism = DefaultParallelism,
        };
        return new Keyring { Format = keyring.Format, Kdf = kdf, Passphrase = Wrap(Derive(passphrase, kdf), dataKey), Recovery = keyring.Recovery };
    }

    /// <summary>Opens the keyring with either the passphrase or the recovery key.</summary>
    /// <exception cref="InvalidDataException">The keyring is incomplete, or its settings are outside what is safe to use.</exception>
    public static byte[] Unlock(Keyring keyring, string secret)
    {
        if (keyring?.Kdf is null || keyring.Passphrase is null || keyring.Recovery is null) throw new InvalidDataException(L.T("The synced data is damaged."));
        if (ParseRecovery(secret) is byte[] recovery && TryUnwrap(recovery, keyring.Recovery) is byte[] fromRecovery) return fromRecovery;
        return TryUnwrap(Derive(secret, keyring.Kdf), keyring.Passphrase) ?? throw new WrongPassphraseException();
    }

    private static byte[] Derive(string passphrase, KdfSettings kdf)
    {
        if (kdf.Name != "argon2id") throw new NotSupportedException(L.T("Unknown key derivation: {0}", kdf.Name));

        // Checked before any work is done with them, and before the password is put through them.
        byte[] salt = Convert.FromBase64String(kdf.Salt ?? string.Empty);
        if (kdf.MemoryKiB is < MinMemoryKiB or > MaxMemoryKiB
            || kdf.Iterations is < MinIterations or > MaxIterations
            || kdf.Parallelism is < 1 or > MaxParallelism
            || salt.Length < MinSaltSize)
        {
            throw new InvalidDataException(L.T("The sync server sent key settings outside the safe range, so the password was not tried."));
        }

        using var argon = new Argon2id(Encoding.UTF8.GetBytes(passphrase.Normalize(NormalizationForm.FormC)))
        {
            Salt = salt,
            MemorySize = kdf.MemoryKiB,
            Iterations = kdf.Iterations,
            DegreeOfParallelism = kdf.Parallelism,
        };
        return argon.GetBytes(KeySize);
    }

    private static WrappedKey Wrap(byte[] key, byte[] dataKey)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] sealedKey = new byte[dataKey.Length + TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, dataKey, sealedKey.AsSpan(0, dataKey.Length), sealedKey.AsSpan(dataKey.Length), KeyringContext);
        return new WrappedKey { Nonce = Convert.ToBase64String(nonce), Ciphertext = Convert.ToBase64String(sealedKey) };
    }

    private static byte[]? TryUnwrap(byte[] key, WrappedKey wrapped)
    {
        byte[] sealedKey = Convert.FromBase64String(wrapped.Ciphertext);
        if (sealedKey.Length < TagSize) return null;

        byte[] dataKey = new byte[sealedKey.Length - TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(Convert.FromBase64String(wrapped.Nonce), sealedKey.AsSpan(0, dataKey.Length), sealedKey.AsSpan(dataKey.Length), dataKey, KeyringContext);
            return dataKey;
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
    }

    /// <summary>32 random bytes as 52 letters and digits in groups of four, avoiding ones that look alike.</summary>
    private static string FormatRecovery(byte[] key)
    {
        var text = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (byte b in key)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                text.Append(RecoveryAlphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0) text.Append(RecoveryAlphabet[(buffer << (5 - bits)) & 31]);
        return string.Join('-', text.ToString().Chunk(RecoveryGroup).Select(c => new string(c)));
    }

    private static byte[]? ParseRecovery(string text)
    {
        string clean = new(text.ToUpperInvariant().Where(c => c is not ('-' or ' ')).ToArray());
        if (clean.Length != 52 || clean.Any(c => RecoveryAlphabet.IndexOf(c) < 0)) return null;

        byte[] key = new byte[KeySize];
        int buffer = 0, bits = 0, index = 0;
        foreach (char c in clean)
        {
            buffer = (buffer << 5) | RecoveryAlphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8 && index < KeySize)
            {
                key[index++] = (byte)(buffer >> (bits - 8));
                bits -= 8;
            }
        }

        return key;
    }

    /// <summary>Compresses and encrypts a document. Each call uses a fresh nonce.</summary>
    public static byte[] Seal(byte[] dataKey, SyncDocument document)
    {
        using var compressed = new MemoryStream();
        using (var brotli = new BrotliStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            JsonSerializer.Serialize(brotli, document, SyncJson.Default.SyncDocument);

        byte[] plain = compressed.ToArray();
        byte[] result = new byte[NonceSize + TagSize + plain.Length];
        RandomNumberGenerator.Fill(result.AsSpan(0, NonceSize));
        using var aes = new AesGcm(dataKey, TagSize);
        aes.Encrypt(result.AsSpan(0, NonceSize), plain, result.AsSpan(NonceSize + TagSize), result.AsSpan(NonceSize, TagSize), DataContext);
        return result;
    }

    public static SyncDocument Open(byte[] dataKey, byte[] sealedData)
    {
        if (sealedData.Length < NonceSize + TagSize) throw new InvalidDataException(L.T("The synced data is damaged."));

        byte[] plain = new byte[sealedData.Length - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(dataKey, TagSize);
            aes.Decrypt(sealedData.AsSpan(0, NonceSize), sealedData.AsSpan(NonceSize + TagSize), sealedData.AsSpan(NonceSize, TagSize), plain, DataContext);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new InvalidDataException(L.T("The synced data does not open with this key, or it was changed."));
        }

        using var brotli = new BrotliStream(new MemoryStream(plain), CompressionMode.Decompress);
        return JsonSerializer.Deserialize(brotli, SyncJson.Default.SyncDocument) ?? new SyncDocument();
    }
}
