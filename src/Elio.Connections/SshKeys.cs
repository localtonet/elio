using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Org.BouncyCastle.Math.EC.Rfc8032;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Security;

namespace Elio.Connections;

/// <summary>The kinds of key Elio can make.</summary>
public enum SshKeyType
{
    Ed25519,
    EcdsaP256,
    Rsa4096,
}

/// <summary>A new key pair: the private half as the text of an OpenSSH key file, the public half as an authorized_keys line.</summary>
public sealed class GeneratedKey(string privateKey, string publicKey)
{
    public string PrivateKey { get; } = privateKey;
    public string PublicKey { get; } = publicKey;
}

/// <summary>
/// Makes SSH keys, writes them the way OpenSSH does, and reads them back. The reading is SSH.NET's; the writing is
/// here because SSH.NET has none, and follows the format's description in OpenSSH's PROTOCOL.key.
/// </summary>
public static partial class SshKeys
{
    private const string Ed25519Name = "ssh-ed25519";
    private const string EcdsaName = "ecdsa-sha2-nistp256";
    private const string EcdsaCurve = "nistp256";
    private const string RsaName = "ssh-rsa";

    private const string PemHeader = "-----BEGIN OPENSSH PRIVATE KEY-----";
    private const string PemFooter = "-----END OPENSSH PRIVATE KEY-----";
    private const int PemLineLength = 70;

    // What ssh-keygen used for most of the format's life. Every connection with the key pays for it again, so it
    // is not raised further.
    private const int KdfRounds = 16;
    private const int KdfSaltSize = 16;
    private const int AesKeySize = 32;
    private const int AesBlockSize = 16;

    // An unencrypted key is padded as if to a cipher with blocks of eight bytes.
    private const int PlainBlockSize = 8;

    private const int MaxCommentLength = 64;

    /// <summary>Makes a new key pair.</summary>
    /// <param name="comment">What the key is called in its public line. Anything <see cref="SafeComment"/> would change is changed.</param>
    /// <param name="passphrase">Encrypts the private key when given. Null or empty leaves it as it is, which inside Elio's vault is still encrypted at rest.</param>
    public static GeneratedKey Generate(SshKeyType type, string comment, string? passphrase)
    {
        comment = SafeComment(comment);

        string name;
        byte[] publicBlob;
        var secret = new SshWriter();

        switch (type)
        {
            case SshKeyType.Ed25519:
            {
                byte[] seed = RandomNumberGenerator.GetBytes(Ed25519.SecretKeySize);
                byte[] point = new byte[Ed25519.PublicKeySize];
                Ed25519.GeneratePublicKey(seed, 0, point, 0);

                name = Ed25519Name;
                publicBlob = new SshWriter().Text(name).Bytes(point).ToArray();
                secret.Bytes(point).Bytes([.. seed, .. point]);
                CryptographicOperations.ZeroMemory(seed);
                break;
            }

            case SshKeyType.EcdsaP256:
            {
                using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                ECParameters parts = ecdsa.ExportParameters(includePrivateParameters: true);

                // The point the way SSH writes it: 4 for "not compressed", then X and Y.
                byte[] point = [0x04, .. parts.Q.X!, .. parts.Q.Y!];

                name = EcdsaName;
                publicBlob = new SshWriter().Text(name).Text(EcdsaCurve).Bytes(point).ToArray();
                secret.Text(EcdsaCurve).Bytes(point).Number(parts.D!);
                CryptographicOperations.ZeroMemory(parts.D);
                break;
            }

            case SshKeyType.Rsa4096:
            {
                using var rsa = RSA.Create(4096);
                RSAParameters parts = rsa.ExportParameters(includePrivateParameters: true);

                name = RsaName;
                publicBlob = new SshWriter().Text(name).Number(parts.Exponent!).Number(parts.Modulus!).ToArray();
                secret.Number(parts.Modulus!).Number(parts.Exponent!).Number(parts.D!).Number(parts.InverseQ!).Number(parts.P!).Number(parts.Q!);
                foreach (byte[]? part in new[] { parts.D, parts.P, parts.Q, parts.DP, parts.DQ, parts.InverseQ }) CryptographicOperations.ZeroMemory(part);
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(type));
        }

        string privateKey = WritePrivateKey(name, publicBlob, secret.ToArray(), comment, passphrase);
        return new GeneratedKey(privateKey, $"{name} {Convert.ToBase64String(publicBlob)} {comment}");
    }

    /// <summary>
    /// Reads a private key from the text of a key file, in any of the formats SSH.NET knows. This is what a
    /// connection loads an identity's key with.
    /// </summary>
    /// <exception cref="SshPassPhraseNullOrEmptyException">The key is encrypted and no passphrase was given.</exception>
    /// <exception cref="SshException">The text is not a key, or the passphrase is wrong.</exception>
    public static PrivateKeyFile Load(string privateKey, string? passphrase)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(privateKey));
        return string.IsNullOrEmpty(passphrase) ? new PrivateKeyFile(stream) : new PrivateKeyFile(stream, passphrase);
    }

    /// <summary>The authorized_keys line that goes with a private key.</summary>
    /// <param name="comment">Put at the end of the line when the key carries no usable name of its own.</param>
    /// <exception cref="SshException">As for <see cref="Load"/>. A key that lives on a security key has no line to give here either.</exception>
    public static string PublicLine(string privateKey, string? passphrase, string comment)
    {
        using PrivateKeyFile file = Load(privateKey, passphrase);
        if (file.HostKeyAlgorithms.FirstOrDefault() is not HostAlgorithm algorithm) throw new SshException("The key has no public half.");

        // The key's type is the first thing in its encoded public half, which is safer to read back than to guess
        // from the algorithm's name: an RSA key signs under several names and has one type.
        byte[] blob = algorithm.Data;
        int length = BinaryPrimitives.ReadInt32BigEndian(blob);
        string name = Encoding.ASCII.GetString(blob, 4, length);

        string own = file.Key.Comment ?? string.Empty;
        string label = own.Length > 0 && SafeComment(own) == own ? own : SafeComment(comment);
        return $"{name} {Convert.ToBase64String(blob)} {label}";
    }

    [GeneratedRegex(@"^(ssh-ed25519|ssh-rsa|ecdsa-sha2-nistp(256|384|521)) [A-Za-z0-9+/]{16,}={0,2}( [A-Za-z0-9@._+:,=/-]{1,255})?$")]
    private static partial Regex PublicLinePattern();

    [GeneratedRegex(@"[^A-Za-z0-9@._+:,=/-]+")]
    private static partial Regex UnsafeInComment();

    /// <summary>
    /// Whether a line is a public key and nothing else: a known type, base64, and at most a comment made of letters,
    /// digits and a few marks. Such a line can be put inside double quotes in a shell command and stay what it is.
    /// </summary>
    public static bool IsPublicLine(string line) => PublicLinePattern().IsMatch(line);

    /// <summary>A comment with everything a shell could read something into replaced by a dash.</summary>
    public static string SafeComment(string text)
    {
        string safe = UnsafeInComment().Replace(text.Trim(), "-").Trim('-');
        if (safe.Length > MaxCommentLength) safe = safe[..MaxCommentLength];
        return safe.Length == 0 ? "elio" : safe;
    }

    /// <summary>What <see cref="InstallCommand"/> prints when it added the key.</summary>
    public const string InstallAdded = "elio-key-added";

    /// <summary>What <see cref="InstallCommand"/> prints when the key was there already.</summary>
    public const string InstallPresent = "elio-key-present";

    /// <summary>
    /// One command that adds a public key to the logged-in user's authorized_keys unless that exact line is in it
    /// already. A folder or file it has to create is open to that user alone; ones that exist are left as they are.
    /// </summary>
    /// <exception cref="ArgumentException">The line is not a plain public key. Nothing else is ever put into the command.</exception>
    public static string InstallCommand(string publicLine)
    {
        if (!IsPublicLine(publicLine)) throw new ArgumentException("Not a public key line.", nameof(publicLine));

        // The script holds no single quote, so it can travel inside a pair of them whatever the user's login shell
        // is, and is then run by sh. The key sits in double quotes, where none of the characters IsPublicLine lets
        // through mean anything.
        string script =
            "umask 077; d=\"$HOME/.ssh\"; f=\"$d/authorized_keys\"; k=\"" + publicLine + "\"; "
            + "mkdir -p \"$d\" || exit 1; "
            + "if [ -f \"$f\" ] && grep -qxF -e \"$k\" \"$f\"; then echo " + InstallPresent + "; exit 0; fi; "

            // A file whose last line was never ended would otherwise get the key glued onto that line.
            + "if [ -s \"$f\" ] && [ -n \"$(tail -c 1 \"$f\")\" ]; then echo >> \"$f\" || exit 1; fi; "
            + "printf \"%s\\n\" \"$k\" >> \"$f\" && echo " + InstallAdded;
        return "sh -c '" + script + "'";
    }

    private static string WritePrivateKey(string keyType, byte[] publicBlob, byte[] keyFields, string comment, string? passphrase)
    {
        bool encrypted = !string.IsNullOrEmpty(passphrase);
        int blockSize = encrypted ? AesBlockSize : PlainBlockSize;

        // The same random number twice: whoever decrypts sees at once whether the passphrase was right.
        byte[] check = RandomNumberGenerator.GetBytes(4);
        var section = new SshWriter().Raw(check).Raw(check).Text(keyType).Raw(keyFields).Text(comment);
        for (byte pad = 1; section.Length % blockSize != 0; pad++) section.Raw([pad]);
        byte[] secret = section.ToArray();

        var file = new SshWriter().Raw("openssh-key-v1\0"u8);
        if (encrypted)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(KdfSaltSize);
            byte[] keyAndIv = new byte[AesKeySize + AesBlockSize];
            OpenSshKdf.Derive(Encoding.UTF8.GetBytes(passphrase!), salt, KdfRounds, keyAndIv);

            byte[] plain = secret;
            secret = AesCtr(keyAndIv.AsSpan(0, AesKeySize), keyAndIv.AsSpan(AesKeySize), plain);
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(keyAndIv);

            file.Text("aes256-ctr").Text("bcrypt").Bytes(new SshWriter().Bytes(salt).UInt32(KdfRounds).ToArray());
        }
        else
        {
            file.Text("none").Text("none").Bytes([]);
        }

        file.UInt32(1).Bytes(publicBlob).Bytes(secret);

        string base64 = Convert.ToBase64String(file.ToArray());
        var text = new StringBuilder(PemHeader).Append('\n');
        for (int at = 0; at < base64.Length; at += PemLineLength) text.Append(base64, at, Math.Min(PemLineLength, base64.Length - at)).Append('\n');
        return text.Append(PemFooter).Append('\n').ToString();
    }

    // AES in counter mode, with the whole IV as one big-endian counter, which is what "aes256-ctr" means to OpenSSH.
    private static byte[] AesCtr(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> data)
    {
        using var aes = Aes.Create();
        aes.Key = key.ToArray();

        byte[] counter = iv.ToArray();
        byte[] pad = new byte[AesBlockSize];
        byte[] result = new byte[data.Length];

        for (int at = 0; at < data.Length; at += AesBlockSize)
        {
            aes.EncryptEcb(counter, pad, PaddingMode.None);
            for (int i = 0; i < AesBlockSize && at + i < data.Length; i++) result[at + i] = (byte)(data[at + i] ^ pad[i]);
            for (int i = AesBlockSize - 1; i >= 0 && ++counter[i] == 0; i--) { }
        }

        CryptographicOperations.ZeroMemory(pad);
        return result;
    }

    /// <summary>Writes values the way the SSH protocol encodes them.</summary>
    private sealed class SshWriter
    {
        private readonly MemoryStream _buffer = new();

        public long Length => _buffer.Length;

        public SshWriter Raw(ReadOnlySpan<byte> bytes)
        {
            _buffer.Write(bytes);
            return this;
        }

        public SshWriter UInt32(uint value)
        {
            Span<byte> encoded = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(encoded, value);
            return Raw(encoded);
        }

        /// <summary>A "string": its length, then its bytes.</summary>
        public SshWriter Bytes(ReadOnlySpan<byte> bytes) => UInt32((uint)bytes.Length).Raw(bytes);

        public SshWriter Text(string text) => Bytes(Encoding.UTF8.GetBytes(text));

        /// <summary>An "mpint", from a positive number written big end first: no leading zeros, but one where the top bit would otherwise read as a minus sign.</summary>
        public SshWriter Number(ReadOnlySpan<byte> bigEndian)
        {
            bigEndian = bigEndian.TrimStart((byte)0);
            if (bigEndian.Length > 0 && (bigEndian[0] & 0x80) != 0) return UInt32((uint)bigEndian.Length + 1).Raw([0]).Raw(bigEndian);
            return Bytes(bigEndian);
        }

        public byte[] ToArray() => _buffer.ToArray();
    }
}
