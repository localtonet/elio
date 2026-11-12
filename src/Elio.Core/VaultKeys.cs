using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Elio.Core;

/// <summary>Locks the vault's key with something only this device, and this user on it, can use.</summary>
public interface IKeyProtector
{
    /// <summary>The key in a form that is safe to keep in a file. Null if the device would not do it.</summary>
    byte[]? Protect(byte[] key);

    /// <summary>The key back from that form. Null if it was made elsewhere or cannot be read any more.</summary>
    byte[]? Unprotect(byte[] stored);
}

/// <summary>
/// The Windows default: the key is encrypted for the signed-in Windows account, silently, by the system.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DpapiProtector(byte[] entropy) : IKeyProtector
{
    public byte[]? Protect(byte[] key)
    {
        try
        {
            return ProtectedData.Protect(key, entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public byte[]? Unprotect(byte[] stored)
    {
        try
        {
            return ProtectedData.Unprotect(stored, entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            // Made by another user or on another machine.
            return null;
        }
    }
}

/// <summary>Where a platform head says how the vault's key is kept on its platform.</summary>
public static class VaultKeys
{
    /// <summary>Set once at startup, before anything asks for the vault. Null leaves the choice to the vault.</summary>
    public static IKeyProtector? Platform { get; set; }
}
