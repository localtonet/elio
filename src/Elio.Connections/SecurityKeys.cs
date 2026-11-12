using System.Buffers.Binary;
using System.ComponentModel;
using System.Formats.Asn1;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Elio.Core;
using Renci.SshNet;
using Renci.SshNet.Security;

namespace Elio.Connections;

/// <summary>What a security key gave back for one signature.</summary>
/// <param name="Flags">What the device did: bit 0 says the user touched it, bit 2 that it verified the user.</param>
/// <param name="Counter">The device's count of signatures made.</param>
/// <param name="Signature">The signature as the device makes it: 64 bytes for Ed25519, DER for ECDSA.</param>
public sealed record SecurityKeyAssertion(byte Flags, uint Counter, byte[] Signature);

/// <summary>Something that can ask a FIDO2 security key to sign.</summary>
public interface ISecurityKeyDevice
{
    /// <summary>Asks the device to sign. Waits for the user to touch it, which may take a while.</summary>
    /// <param name="application">The application the credential was made for, usually "ssh:".</param>
    /// <param name="keyHandle">Which credential on the device.</param>
    /// <param name="message">What to sign. The device signs its SHA-256 hash together with its own data.</param>
    /// <param name="verifyUser">Whether the device must check a PIN or a fingerprint as well.</param>
    SecurityKeyAssertion Sign(string application, byte[] keyHandle, byte[] message, bool verifyUser);
}

/// <summary>Logging in with a key that lives on a FIDO2 security key, from the OpenSSH file that points at it.</summary>
public static class SecurityKeys
{
    private static ISecurityKeyDevice? _device;

    /// <summary>
    /// How security keys are reached on this system. Windows has a way built in; elsewhere it is null unless one is set,
    /// and such keys work through the SSH agent instead.
    /// </summary>
    public static ISecurityKeyDevice? Device
    {
        get => _device ?? (OperatingSystem.IsWindows() ? WindowsSecurityKey.Instance : null);
        set => _device = value;
    }

    internal static IPrivateKeySource Source(SecurityKeyFile file)
    {
        ISecurityKeyDevice device = Device ?? throw new NotSupportedException(L.T("This key lives on a security key. On this system, add it to the SSH agent and turn on \"Use the keys in the SSH agent\"."));
        return new DeviceKey(file, device);
    }

    private sealed class DeviceKey(SecurityKeyFile file, ISecurityKeyDevice device) : IPrivateKeySource
    {
        public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms { get; } = [new DeviceAlgorithm(file, device)];
    }

    private sealed class DeviceAlgorithm(SecurityKeyFile file, ISecurityKeyDevice device) : HostAlgorithm(file.KeyType)
    {
        public override byte[] Data => file.PublicKey;

        public override byte[] Sign(byte[] data)
        {
            SecurityKeyAssertion assertion = device.Sign(file.Application, file.KeyHandle, data, (file.Flags & SecurityKeyFile.VerifyUserFlag) != 0);
            return EncodeSignature(file.KeyType, assertion);
        }

        public override bool VerifySignature(byte[] data, byte[] signature) => false;
    }

    /// <summary>Puts a device's answer in the form a server expects: key type, signature, flags and counter.</summary>
    internal static byte[] EncodeSignature(string keyType, SecurityKeyAssertion assertion)
    {
        byte[] signature = keyType == SecurityKeyFile.Ecdsa ? EcdsaFromDer(assertion.Signature) : assertion.Signature;

        using var blob = new MemoryStream();
        WriteString(blob, Encoding.ASCII.GetBytes(keyType));
        WriteString(blob, signature);
        blob.WriteByte(assertion.Flags);
        Span<byte> counter = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(counter, assertion.Counter);
        blob.Write(counter);
        return blob.ToArray();
    }

    /// <summary>Devices give an ECDSA signature as an ASN.1 pair of numbers. SSH wants the same pair as two of its own numbers.</summary>
    private static byte[] EcdsaFromDer(byte[] der)
    {
        var reader = new AsnReader(der, AsnEncodingRules.DER);
        AsnReader pair = reader.ReadSequence();
        BigInteger r = pair.ReadInteger();
        BigInteger s = pair.ReadInteger();

        using var numbers = new MemoryStream();
        WriteString(numbers, r.ToByteArray(isUnsigned: false, isBigEndian: true));
        WriteString(numbers, s.ToByteArray(isUnsigned: false, isBigEndian: true));
        return numbers.ToArray();
    }

    private static void WriteString(Stream target, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)value.Length);
        target.Write(length);
        target.Write(value);
    }
}

/// <summary>Reaches security keys through Windows itself, which shows its own prompt asking the user to touch the key.</summary>
internal sealed unsafe partial class WindowsSecurityKey : ISecurityKeyDevice
{
    public static readonly WindowsSecurityKey Instance = new();

    private const int FlagsOffset = 32;
    private const int CounterOffset = 33;
    private const int AuthenticatorDataLength = 37;
    private const uint TimeoutMilliseconds = 60_000;
    private const uint VerificationRequired = 1;
    private const uint VerificationDiscouraged = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct ClientData
    {
        public uint Version;
        public uint Length;
        public byte* Data;
        public char* HashAlgorithm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Version;
        public uint IdLength;
        public byte* Id;
        public char* Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Credentials
    {
        public uint Count;
        public Credential* Items;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Extensions
    {
        public uint Count;
        public void* Items;
    }

    // The first version of the options structure, which every Windows that has this API understands.
    [StructLayout(LayoutKind.Sequential)]
    private struct AssertionOptions
    {
        public uint Version;
        public uint TimeoutMilliseconds;
        public Credentials Allowed;
        public Extensions Extensions;
        public uint Attachment;
        public uint UserVerification;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Assertion
    {
        public uint Version;
        public uint AuthenticatorDataLength;
        public byte* AuthenticatorData;
        public uint SignatureLength;
        public byte* Signature;
        public Credential Credential;
        public uint UserIdLength;
        public byte* UserId;
    }

    [LibraryImport("webauthn.dll", EntryPoint = "WebAuthNAuthenticatorGetAssertion")]
    private static partial int GetAssertion(nint window, char* relyingParty, ClientData* clientData, AssertionOptions* options, Assertion** assertion);

    [LibraryImport("webauthn.dll", EntryPoint = "WebAuthNFreeAssertion")]
    private static partial void FreeAssertion(Assertion* assertion);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    public SecurityKeyAssertion Sign(string application, byte[] keyHandle, byte[] message, bool verifyUser)
    {
        // Windows hashes what it is given with SHA-256 and has the device sign that, which is exactly what a server checks.
        fixed (byte* messageStart = message)
        fixed (byte* handleStart = keyHandle)
        fixed (char* party = application)
        fixed (char* hash = "SHA-256")
        fixed (char* type = "public-key")
        {
            var clientData = new ClientData { Version = 1, Length = (uint)message.Length, Data = messageStart, HashAlgorithm = hash };
            var credential = new Credential { Version = 1, IdLength = (uint)keyHandle.Length, Id = handleStart, Type = type };
            var options = new AssertionOptions
            {
                Version = 1,
                TimeoutMilliseconds = TimeoutMilliseconds,
                Allowed = new Credentials { Count = 1, Items = &credential },
                UserVerification = verifyUser ? VerificationRequired : VerificationDiscouraged,
            };

            Assertion* assertion = null;
            int result;
            try
            {
                result = GetAssertion(GetForegroundWindow(), party, &clientData, &options, &assertion);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                throw new NotSupportedException(L.T("This version of Windows cannot use security keys."), ex);
            }

            if (result < 0 || assertion is null) throw new InvalidOperationException(L.T("The security key did not sign: {0}", new Win32Exception(result).Message));

            try
            {
                if (assertion->AuthenticatorDataLength < AuthenticatorDataLength) throw new InvalidOperationException(L.T("The security key gave an answer that makes no sense."));

                var data = new ReadOnlySpan<byte>(assertion->AuthenticatorData, (int)assertion->AuthenticatorDataLength);
                byte[] signature = new ReadOnlySpan<byte>(assertion->Signature, (int)assertion->SignatureLength).ToArray();
                return new SecurityKeyAssertion(data[FlagsOffset], BinaryPrimitives.ReadUInt32BigEndian(data[CounterOffset..]), signature);
            }
            finally
            {
                FreeAssertion(assertion);
            }
        }
    }
}
