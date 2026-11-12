using System.Security.Cryptography;
using Elio.Core;
using Foundation;
using Security;

namespace Elio.iOS;

/// <summary>
/// Wraps the vault's key with a key kept in the iOS Keychain. The Keychain item belongs to this app on this
/// device and is not carried to another one by a backup, so the file holding the wrapped vault key is useless
/// anywhere else.
/// </summary>
internal sealed class IosKeyProtector : IKeyProtector
{
    private const string Service = "sh.elio.app.vault";
    private const string Account = "wrapping-key";
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public byte[]? Protect(byte[] key)
    {
        if (WrappingKey(create: true) is not byte[] wrapping) return null;

        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] sealedKey = new byte[key.Length];
        byte[] tag = new byte[TagSize];
        using (var aes = new AesGcm(wrapping, TagSize)) aes.Encrypt(nonce, key, sealedKey, tag);
        return [.. nonce, .. tag, .. sealedKey];
    }

    public byte[]? Unprotect(byte[] stored)
    {
        if (stored.Length <= NonceSize + TagSize || WrappingKey(create: false) is not byte[] wrapping) return null;

        try
        {
            byte[] key = new byte[stored.Length - NonceSize - TagSize];
            using var aes = new AesGcm(wrapping, TagSize);
            aes.Decrypt(stored.AsSpan(0, NonceSize), stored.AsSpan(NonceSize + TagSize), stored.AsSpan(NonceSize, TagSize), key);
            return key;
        }
        catch (CryptographicException)
        {
            // Restored from a backup onto another device, where the Keychain item did not come along.
            return null;
        }
    }

    private static SecRecord Query() => new(SecKind.GenericPassword) { Service = Service, Account = Account };

    private static byte[]? WrappingKey(bool create)
    {
        using (SecRecord query = Query())
        {
            SecRecord? found = SecKeyChain.QueryAsRecord(query, out SecStatusCode status);
            if (status == SecStatusCode.Success && found?.ValueData is NSData data && data.Length == KeySize) return data.ToArray();
            if (!create || status is not (SecStatusCode.ItemNotFound or SecStatusCode.Success)) return null;
        }

        byte[] made = RandomNumberGenerator.GetBytes(KeySize);
        using SecRecord record = Query();
        record.ValueData = NSData.FromArray(made);

        // Readable once the device has been unlocked after a restart, and never on any other device.
        record.Accessible = SecAccessible.AfterFirstUnlockThisDeviceOnly;
        return SecKeyChain.Add(record) == SecStatusCode.Success ? made : null;
    }
}
