using System.Buffers.Binary;
using Elio.Core;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace Elio.Connections;

/// <summary>Seals and opens Mosh datagrams: AES-128 in OCB mode with the key the server printed.</summary>
/// <remarks>
/// A datagram is the last 8 bytes of the nonce in the clear, then the ciphertext, then a 16 byte tag.
/// The nonce is a counter that never repeats, with its top bit saying which way the datagram travels.
/// </remarks>
internal sealed class MoshCipher
{
    public const int NonceLength = 8;
    public const int TagLength = 16;

    /// <summary>The largest datagram either side is allowed to send.</summary>
    public const int ReceiveLimit = 2048;

    private readonly KeyParameter _key;
    private readonly OcbBlockCipher _cipher = new(new AesEngine(), new AesEngine());

    public MoshCipher(string base64Key)
    {
        // The server prints 16 bytes as base64 with the two padding characters cut off.
        byte[] key = new byte[18];
        if (base64Key.Length != 22
            || !Convert.TryFromBase64String(base64Key + "==", key, out int length)
            || length != 16
            || Convert.ToBase64String(key, 0, 16)[..22] != base64Key)
        {
            throw new FormatException(L.T("The Mosh key from the server is not valid."));
        }

        _key = new KeyParameter(key, 0, 16);
    }

    /// <summary>Encrypts one datagram. <paramref name="nonce"/> must never be used twice with this key.</summary>
    public byte[] Seal(ulong nonce, ReadOnlySpan<byte> plaintext)
    {
        byte[] datagram = new byte[NonceLength + plaintext.Length + TagLength];
        BinaryPrimitives.WriteUInt64BigEndian(datagram, nonce);

        _cipher.Init(true, Parameters(nonce));
        int written = _cipher.ProcessBytes(plaintext, datagram.AsSpan(NonceLength));
        _cipher.DoFinal(datagram.AsSpan(NonceLength + written));
        return datagram;
    }

    /// <summary>Decrypts one datagram. False when it was not made with this key, or was changed on the way.</summary>
    public bool TryOpen(ReadOnlySpan<byte> datagram, out ulong nonce, out byte[] plaintext)
    {
        nonce = 0;
        plaintext = [];
        if (datagram.Length < NonceLength + TagLength || datagram.Length > ReceiveLimit) return false;

        nonce = BinaryPrimitives.ReadUInt64BigEndian(datagram);
        ReadOnlySpan<byte> body = datagram[NonceLength..];
        byte[] output = new byte[_cipher.GetOutputSize(body.Length) + TagLength];
        try
        {
            _cipher.Init(false, Parameters(nonce));
            int written = _cipher.ProcessBytes(body, output);
            written += _cipher.DoFinal(output.AsSpan(written));
            plaintext = output.AsSpan(0, written).ToArray();
            return true;
        }
        catch (InvalidCipherTextException)
        {
            return false;
        }
    }

    private AeadParameters Parameters(ulong nonce)
    {
        // OCB takes 12 bytes. Mosh leaves the first four at zero.
        byte[] full = new byte[12];
        BinaryPrimitives.WriteUInt64BigEndian(full.AsSpan(4), nonce);
        return new AeadParameters(_key, TagLength * 8, full);
    }
}
