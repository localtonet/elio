using Android.Security.Keystore;
using Elio.Core;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace Elio.Android;

/// <summary>
/// Wraps the vault's key with an AES key held by the Android Keystore. That key cannot be read out of the
/// device, by this app or any other, so the file holding the wrapped vault key is useless anywhere else.
/// </summary>
internal sealed class AndroidKeyProtector : IKeyProtector
{
    private const string Store = "AndroidKeyStore";
    private const string Alias = "elio-vault";
    private const string Transformation = "AES/GCM/NoPadding";
    private const int NonceSize = 12;
    private const int TagBits = 128;

    public byte[]? Protect(byte[] key)
    {
        try
        {
            Cipher cipher = Cipher.GetInstance(Transformation)!;
            cipher.Init(CipherMode.EncryptMode, WrappingKey());

            // The keystore picks the nonce. It is kept in front of the result, which is where Unprotect looks for it.
            byte[] nonce = cipher.GetIV()!;
            byte[] sealedKey = cipher.DoFinal(key)!;
            return [.. nonce, .. sealedKey];
        }
        catch (Exception ex) when (ex is GeneralSecurityException or Java.Lang.RuntimeException && Report(ex))
        {
            return null;
        }
    }

    public byte[]? Unprotect(byte[] stored)
    {
        if (stored.Length <= NonceSize) return null;

        try
        {
            Cipher cipher = Cipher.GetInstance(Transformation)!;
            cipher.Init(CipherMode.DecryptMode, WrappingKey(), new GCMParameterSpec(TagBits, stored, 0, NonceSize));
            return cipher.DoFinal(stored, NonceSize, stored.Length - NonceSize);
        }
        catch (Exception ex) when (ex is GeneralSecurityException or Java.Lang.RuntimeException && Report(ex))
        {
            // Restored from a backup onto another device, or the keystore was reset.
            return null;
        }
    }

    // Why the keystore refused, in the system log: without it a vault that cannot be set up says nothing about
    // the cause. The exception names an operation, never the key.
    private static bool Report(Exception ex)
    {
        global::Android.Util.Log.Warn("Elio", $"The keystore refused: {ex.GetType().Name}: {ex.Message}");
        return true;
    }

    private static IKey WrappingKey()
    {
        KeyStore store = KeyStore.GetInstance(Store)!;
        store.Load(null);
        if (store.GetKey(Alias, null) is IKey existing) return existing;

        KeyGenerator generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, Store)!;
        generator.Init(new KeyGenParameterSpec.Builder(Alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)!
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)!
            .SetKeySize(256)!
            .Build());
        return generator.GenerateKey()!;
    }
}
