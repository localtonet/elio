using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Elio.Core;

/// <summary>
/// Signs with a key kept by Windows Hello, through the "Microsoft Passport Key Storage Provider". Windows
/// shows its own prompt for the PIN, fingerprint or face, and the key never leaves the device.
/// </summary>
/// <remarks>
/// These are the plain Win32 key storage calls from ncrypt.h, not the WinRT KeyCredentialManager, so nothing
/// here needs a projection. Names and values are copied from the Windows SDK headers. The three "Ngc" and
/// "PinCache" settings are not all in the public headers; they are the ones other Hello integrations rely on
/// to make every use ask for a gesture, and a Windows that does not know one of them is simply left at its default.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class CngHelloSigner : IHelloSigner
{
    private const string Provider = "Microsoft Passport Key Storage Provider";
    private const string RsaAlgorithm = "RSA";
    private const string HashAlgorithm = "SHA256";
    private const int KeyBits = 2048;

    private const string LengthProperty = "Length";
    private const string KeyUsageProperty = "Key Usage";
    private const string WindowHandleProperty = "HWND Handle";
    private const string UseContextProperty = "Use Context";
    private const string CacheTypeProperty = "NgcCacheType";
    private const string CacheTypePropertyOld = "NgcCacheTypeProperty";
    private const string GestureRequiredProperty = "PinCacheIsGestureRequired";

    private const int AllowSigning = 0x00000002;
    private const int CacheTypeAuthMandatory = 0x00000001;
    private const int PadPkcs1 = 0x00000002;
    private const int Silent = 0x00000040;

    private const int NteNoKey = unchecked((int)0x8009000D);
    private const int NteNotFound = unchecked((int)0x80090011);
    private const int NteBadKeyset = unchecked((int)0x80090016);
    private const int NteNotSupported = unchecked((int)0x80090029);
    private const int NteDeviceNotReady = unchecked((int)0x80090030);
    private const int NteDeviceNotFound = unchecked((int)0x80090035);
    private const int NteUserCancelled = unchecked((int)0x80090036);
    private const int NteNotActiveConsole = unchecked((int)0x80090038);
    private const int ErrorCancelled = unchecked((int)0x800704C7);
    private const int ScardCancelledByUser = unchecked((int)0x8010006E);

    // The security chip sometimes fails a first attempt and works on the next.
    private const int TpmHandle = unchecked((int)0x8028008B);
    private const int TpmSize = unchecked((int)0x80280095);
    private const int Tpm159 = unchecked((int)0x80280159);
    private const int BiometricDataProtectionFailure = unchecked((int)0x80098046);
    private const int Attempts = 3;

    // Room for the signature of an RSA key far larger than the one made here, so no call is spent asking for the size.
    private const int SignatureRoom = 1024;

    /// <summary>Whether the Hello key store opens and this user has Hello set up. Never prompts and never throws.</summary>
    public static bool IsAvailable()
    {
        try
        {
            if (NCryptOpenStorageProvider(out nint provider, Provider, 0) < 0) return false;

            try
            {
                if (NCryptOpenKey(provider, out nint key, KeyName(), 0, Silent) >= 0)
                {
                    _ = NCryptFreeObject(key);
                    return true;
                }

                // Elio has no key yet. A user who has set up Hello already has keys in this store, the ones
                // Windows itself signs in with; a user who has not has none.
                nint state = 0;
                int status = NCryptEnumKeys(provider, 0, out nint name, ref state, Silent);
                if (name != 0) _ = NCryptFreeBuffer(name);
                if (state != 0) _ = NCryptFreeBuffer(state);
                return status >= 0;
            }
            finally
            {
                _ = NCryptFreeObject(provider);
            }
        }
        catch (Exception)
        {
            // A Windows without the library, a locked-down account: whatever it is, Hello is not on offer.
            return false;
        }
    }

    public byte[] Sign(byte[] challenge, bool create)
    {
        for (int attempt = 1; ; attempt++)
        {
            int status = TrySign(challenge, create, out byte[]? signature);
            if (signature is not null) return signature;

            bool passing = status is TpmHandle or TpmSize or Tpm159 or BiometricDataProtectionFailure;
            if (!passing || attempt == Attempts) throw Failure(status);
            Thread.Sleep(200);
        }
    }

    private static int TrySign(byte[] challenge, bool create, out byte[]? signature)
    {
        signature = null;

        int status;
        nint provider;
        try
        {
            status = NCryptOpenStorageProvider(out provider, Provider, 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return NteNotSupported;
        }

        if (status < 0) return NteNotSupported;

        try
        {
            string name = KeyName();
            status = NCryptOpenKey(provider, out nint key, name, 0, 0);
            if (status is NteNoKey or NteBadKeyset or NteNotFound)
            {
                // Opening a vault with a newly made key could never work, so only locking one may make it.
                if (!create) return NteNoKey;
                status = CreateKey(provider, name, out key);
            }

            if (status < 0) return status;

            try
            {
                ShowContext(key);

                // Without this Windows may accept a gesture given to another app a moment ago and not ask at all.
                _ = NCryptSetProperty(key, GestureRequiredProperty, BitConverter.GetBytes(1), sizeof(int), 0);

                // PKCS#1 v1.5 signs a hash, and is told which hash so it can name it inside the signature.
                byte[] hash = SHA256.HashData(challenge);
                byte[] room = new byte[SignatureRoom];
                var padding = new Pkcs1PaddingInfo { AlgorithmId = Marshal.StringToHGlobalUni(HashAlgorithm) };
                try
                {
                    status = NCryptSignHash(key, ref padding, hash, hash.Length, room, room.Length, out int size, PadPkcs1);
                    if (status >= 0 && size > 0 && size <= room.Length) signature = room[..size];
                    else if (status >= 0) status = NteNotSupported;
                }
                finally
                {
                    Marshal.FreeHGlobal(padding.AlgorithmId);
                    CryptographicOperations.ZeroMemory(room);
                }

                return status;
            }
            finally
            {
                _ = NCryptFreeObject(key);
            }
        }
        finally
        {
            _ = NCryptFreeObject(provider);
        }
    }

    private static int CreateKey(nint provider, string name, out nint key)
    {
        int status = NCryptCreatePersistedKey(provider, out key, RsaAlgorithm, name, 0, 0);
        if (status < 0) return status;

        status = NCryptSetProperty(key, LengthProperty, BitConverter.GetBytes(KeyBits), sizeof(int), 0);
        if (status >= 0) status = NCryptSetProperty(key, KeyUsageProperty, BitConverter.GetBytes(AllowSigning), sizeof(int), 0);
        if (status >= 0)
        {
            // Ask for a gesture on every use, for good. Older Windows knows this setting by another name.
            byte[] mandatory = BitConverter.GetBytes(CacheTypeAuthMandatory);
            if (NCryptSetProperty(key, CacheTypeProperty, mandatory, sizeof(int), 0) < 0)
                _ = NCryptSetProperty(key, CacheTypePropertyOld, mandatory, sizeof(int), 0);

            ShowContext(key);
            status = NCryptFinalizeKey(key, 0);
        }

        if (status < 0)
        {
            // Never finished, so there is nothing stored to remove: only the handle to let go of.
            _ = NCryptFreeObject(key);
            key = 0;
        }

        return status;
    }

    // Gives the prompt a parent window and a line of text. Purely for looks, so failures are let pass.
    private static void ShowContext(nint key)
    {
        nint window = WindowsHelloProtector.Window?.Invoke() ?? 0;
        if (window == 0) window = GetForegroundWindow();
        if (window != 0)
        {
            byte[] handle = nint.Size == 8 ? BitConverter.GetBytes((long)window) : BitConverter.GetBytes((int)window);
            _ = NCryptSetProperty(key, WindowHandleProperty, handle, handle.Length, 0);
        }

        // Passed as UTF-16 with its closing zero, which the length must count.
        byte[] message = Encoding.Unicode.GetBytes(L.T("Unlock the Elio password vault") + '\0');
        _ = NCryptSetProperty(key, UseContextProperty, message, message.Length, 0);
    }

    // Hello names its keys by user, then by the app's own two-level path.
    private static string KeyName() => $"{WindowsIdentity.GetCurrent().User?.Value}//Elio//Vault";

    private static VaultLockedException Failure(int status) => status switch
    {
        NteUserCancelled or ErrorCancelled or ScardCancelledByUser =>
            new VaultLockedException(VaultLockReason.Cancelled, L.T("Windows Hello was cancelled, so the vault stays locked.")),
        NteNoKey or NteBadKeyset or NteNotFound =>
            new VaultLockedException(VaultLockReason.KeyMissing,
                L.T("The Windows Hello key that locks the vault is no longer on this device. Windows Hello may have been reset.")),
        NteNotSupported or NteDeviceNotReady or NteDeviceNotFound or NteNotActiveConsole =>
            new VaultLockedException(VaultLockReason.Unavailable, L.T("Windows Hello is not available right now. Check that it is set up in Windows settings.")),
        _ => new VaultLockedException(VaultLockReason.Failed, L.T("Windows Hello could not unlock the vault (error 0x{0}).", status.ToString("X8"))),
    };

    // typedef struct _BCRYPT_PKCS1_PADDING_INFO { LPCWSTR pszAlgId; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Pkcs1PaddingInfo
    {
        public nint AlgorithmId;
    }

    // The project does not allow unsafe code, which the LibraryImport generator needs, so these are classic
    // imports. Handles are ULONG_PTR, statuses are LONG, and every count is a DWORD.
    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int NCryptOpenStorageProvider(out nint provider, string providerName, int flags);

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int NCryptOpenKey(nint provider, out nint key, string keyName, int legacyKeySpec, int flags);

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int NCryptCreatePersistedKey(nint provider, out nint key, string algorithmId, string keyName, int legacyKeySpec, int flags);

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int NCryptSetProperty(nint handle, string property, byte[] input, int inputSize, int flags);

    [DllImport("ncrypt.dll", ExactSpelling = true)]
    private static extern int NCryptFinalizeKey(nint key, int flags);

    [DllImport("ncrypt.dll", ExactSpelling = true)]
    private static extern int NCryptSignHash(nint key, ref Pkcs1PaddingInfo paddingInfo, byte[] hash, int hashSize, byte[] signature, int signatureSize, out int resultSize, int flags);

    [DllImport("ncrypt.dll", ExactSpelling = true)]
    private static extern int NCryptEnumKeys(nint provider, nint scope, out nint keyName, ref nint enumState, int flags);

    [DllImport("ncrypt.dll", ExactSpelling = true)]
    private static extern int NCryptFreeBuffer(nint buffer);

    [DllImport("ncrypt.dll", ExactSpelling = true)]
    private static extern int NCryptFreeObject(nint handle);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetForegroundWindow();
}
